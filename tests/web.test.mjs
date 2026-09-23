import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
const source = await readFile(
  new URL("../SSO-Auth/Views/web.js", import.meta.url),
  "utf8",
);
const {
  readCredentials,
  saveLogin,
  serverEntry,
  authHeader,
  sameUser,
  request,
  device,
} = await import(
  `data:text/javascript;base64,${Buffer.from(source).toString("base64")}`
);

test("login preserves other servers, local settings, and project subpaths", () => {
  let saved;
  const credentials = {
    Servers: [
      { Id: "other", AccessToken: "untouched" },
      { Id: "current", Custom: "preserved" },
    ],
    Other: "preserved",
  };
  const base = "https://media.example:9443/jellyfin";
  saveLogin(
    credentials,
    { Id: "current", ServerName: "Test" },
    { ServerId: "current", AccessToken: "synthetic", User: { Id: "user" } },
    base,
    { setItem: (_, value) => (saved = JSON.parse(value)) },
  );
  assert.equal(saved.Servers.length, 2);
  assert.equal(saved.Servers[0].AccessToken, "untouched");
  assert.equal(saved.Servers[1].Custom, "preserved");
  assert.equal(saved.Servers[1].ManualAddress, base);
  assert.equal(saved.Other, "preserved");
});

test("server identity wins over a stale address and unregistered servers match only exact addresses", () => {
  const credentials = {
    Servers: [
      { Id: "other", ManualAddress: "https://example/jf" },
      { ManualAddress: "https://example/other/" },
      { Id: "current", ManualAddress: "http://old" },
    ],
  };
  assert.equal(
    serverEntry(credentials, { Id: "current" }, "https://example/jf")
      .ManualAddress,
    "http://old",
  );
  assert.equal(
    serverEntry(credentials, { Id: "new" }, "https://example/jf"),
    undefined,
  );
  assert.equal(
    serverEntry(credentials, { Id: "new" }, "https://example/other")
      .ManualAddress,
    "https://example/other/",
  );
});

test("malformed and inaccessible storage fail visibly", () => {
  for (const raw of [
    "{",
    "",
    "null",
    "[]",
    "{}",
    '{"Servers":null}',
    '{"Servers":[null]}',
    '{"Servers":[[]]}',
    '{"Servers":["invalid"]}',
    '{"Servers":[42]}',
  ]) {
    assert.throws(() => readCredentials({ getItem: () => raw }), {
      name: "Error",
      message:
        "Stored Jellyfin credentials are invalid. Sign in through Jellyfin again.",
    });
  }
  assert.throws(
    () =>
      readCredentials({
        getItem: () => {
          throw new Error("blocked");
        },
      }),
    /blocked/,
  );
  assert.throws(
    () =>
      saveLogin(
        { Servers: [] },
        { Id: "a" },
        { ServerId: "b" },
        "https://example",
        // Explicit storage: Node before 25 has no global localStorage.
        { setItem: () => assert.fail("rejected logins must not be stored") },
      ),
    /unexpected/,
  );
});

test("missing credentials start empty and valid server records are preserved", () => {
  assert.deepEqual(readCredentials({ getItem: () => null }), { Servers: [] });
  const credentials = {
    Servers: [
      { Id: "other", AccessToken: "untouched" },
      { ManualAddress: "https://example/jellyfin" },
    ],
    Other: "preserved",
  };
  assert.deepEqual(
    readCredentials({ getItem: () => JSON.stringify(credentials) }),
    credentials,
  );
});

test("authentication headers reject injection and GUID comparison accepts Jellyfin formats", () => {
  assert.throws(() => authHeader('x"\r\nInjected: true'));
  assert.throws(() => authHeader(""));
  assert.equal(
    authHeader("synthetic").Authorization,
    'MediaBrowser Token="synthetic"',
  );
  assert.ok(sameUser("AABB-CCDD", "aabbccdd"));
});

test("HTTP failures reject without treating error bodies as authentication", async () => {
  const original = globalThis.fetch;
  try {
    globalThis.fetch = async () => ({
      ok: false,
      status: 403,
      json: async () => ({ Error: "Denied" }),
    });
    await assert.rejects(request("/sso/test"), /Denied/);
    globalThis.fetch = async () => ({ ok: true, status: 204 });
    assert.equal(await request("/sso/test"), null);
  } finally {
    globalThis.fetch = original;
  }
});

test("unexpected HTTP error bodies retain status and recovery guidance", async () => {
  const original = globalThis.fetch;
  try {
    for (const body of [
      null,
      [],
      "gateway failure",
      42,
      false,
      {},
      { Error: {} },
      { error: [] },
      { Error: "  " },
    ]) {
      globalThis.fetch = async () => ({
        ok: false,
        status: 502,
        json: async () => body,
      });
      await assert.rejects(request("/sso/test"), {
        name: "Error",
        message:
          "Request failed (502). Sign in again or review the provider configuration.",
      });
    }
    globalThis.fetch = async () => ({
      ok: false,
      status: 502,
      json: async () => {
        throw new SyntaxError("not JSON");
      },
    });
    await assert.rejects(request("/sso/test"), /Request failed \(502\)/);
    for (const body of [{ error: "Denied" }, { Error: {}, error: "Denied" }]) {
      globalThis.fetch = async () => ({
        ok: false,
        status: 403,
        json: async () => body,
      });
      await assert.rejects(request("/sso/test"), /Denied/);
    }
  } finally {
    globalThis.fetch = original;
  }
});

test("device IDs are persistent UUIDs with and without native randomUUID", () => {
  const descriptor = Object.getOwnPropertyDescriptor(globalThis, "crypto");
  const native = globalThis.crypto;
  try {
    for (const nativeUUID of [true, false]) {
      let generations = 0;
      Object.defineProperty(globalThis, "crypto", {
        configurable: true,
        value: {
          ...(nativeUUID
            ? {
                randomUUID: () => {
                  generations++;
                  return native.randomUUID();
                },
              }
            : {}),
          getRandomValues: (bytes) => {
            generations++;
            return native.getRandomValues(bytes);
          },
        },
      });
      const values = new Map();
      const storage = {
        getItem: (key) => values.get(key) ?? null,
        setItem: (key, value) => values.set(key, value),
      };
      const first = device(storage);
      assert.match(
        first.DeviceID,
        /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/,
      );
      assert.equal(values.get("_deviceId2"), first.DeviceID);
      assert.equal(device(storage).DeviceID, first.DeviceID);
      assert.equal(generations, 1);
      values.set("_deviceId2", "existing-jellyfin-device");
      assert.equal(device(storage).DeviceID, "existing-jellyfin-device");
      assert.equal(generations, 1);
    }
  } finally {
    Object.defineProperty(globalThis, "crypto", descriptor);
  }
});

test("malformed login responses cannot mutate or persist credentials", () => {
  const valid = {
    ServerId: "current",
    AccessToken: "synthetic",
    User: { Id: "user" },
  };
  for (const result of [
    null,
    undefined,
    [],
    "invalid",
    42,
    {},
    { ...valid, ServerId: "other" },
    ...[undefined, null, "", "bad\\token", 'bad"token', 42, true, {}].map(
      (AccessToken) => ({ ...valid, AccessToken }),
    ),
    ...[undefined, null, {}, { Id: "" }, { Id: 42 }].map((User) => ({
      ...valid,
      User,
    })),
  ]) {
    const credentials = {
      Servers: [{ Id: "other", AccessToken: "preserved" }],
    };
    const before = structuredClone(credentials);
    assert.throws(
      () =>
        saveLogin(credentials, { Id: "current" }, result, "https://example", {
          setItem: () => assert.fail("must not save"),
        }),
      /unexpected login response/,
    );
    assert.deepEqual(credentials, before);
  }
});

test("completion preserves validation and storage errors across all cleanup failures", async () => {
  const completion = await readFile(
    new URL("../SSO-Auth/Views/complete.js", import.meta.url),
    "utf8",
  );
  const helpers = {
    readCredentials,
    serverEntry,
    saveLogin,
    request,
    authHeader,
    device,
    sameUser,
  };
  const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
  const run = new AsyncFunction(
    ...Object.keys(helpers),
    "document",
    "location",
    "localStorage",
    completion.replace(/^import[\s\S]*?from "\.\/web\.js";/, ""),
  );
  const valid = {
    ServerId: "current",
    AccessToken: "synthetic",
    User: { Id: "user" },
  };
  for (const result of [
    null,
    {},
    { ...valid, AccessToken: "bad token" },
    { ...valid, AccessToken: 42 },
    { ...valid, ServerId: "other" },
    valid,
  ]) {
    for (const cleanup of ["success", "sync", "async"]) {
      let writes = 0;
      let logouts = 0;
      const storage = {
        getItem: () => null,
        setItem: () => {
          if (++writes === 2) throw new Error("Storage quota exceeded");
        },
      };
      const elements = Object.fromEntries(
        ["#status", "#completion-title", "#continue", "#back", "#sso-data"].map(
          (id) => [
            id,
            { hidden: true, classList: { add() {} }, setAttribute() {} },
          ],
        ),
      );
      elements["#sso-data"].textContent = JSON.stringify({
        basePath: "/jf",
        provider: "test",
        mode: "OID",
        code: "once",
      });
      const mockRequest = (url) => {
        if (url.endsWith("/Public")) return Promise.resolve({ Id: "current" });
        if (url.endsWith("/Auth/test")) return Promise.resolve(result);
        assert.equal(url, "https://example/jf/Sessions/Logout");
        logouts++;
        if (cleanup === "sync") throw new Error("Synchronous cleanup failure");
        if (cleanup === "async")
          return Promise.reject(new Error("Network cleanup failure"));
        return Promise.resolve(null);
      };
      const injected = {
        ...helpers,
        readCredentials: () => readCredentials(storage),
        saveLogin: (...args) => saveLogin(...args, storage),
        request: mockRequest,
        device: () => ({ DeviceID: "persistent" }),
      };
      const document = {
        querySelector: (id) => elements[id],
        body: { dataset: {} },
      };
      await run(
        ...Object.values(injected),
        document,
        {
          origin: "https://example",
          replace: () => assert.fail("must not redirect"),
        },
        storage,
      );
      assert.equal(document.body.dataset.state, "error");
      assert.equal(elements["#back"].hidden, false);
      assert.equal(
        elements["#status"].textContent,
        result === valid
          ? "Storage quota exceeded"
          : "Jellyfin returned an unexpected login response.",
      );
      assert.equal(elements["#continue"].hidden, true);
      assert.equal(logouts, result?.AccessToken === "synthetic" ? 1 : 0);
    }
  }
});
