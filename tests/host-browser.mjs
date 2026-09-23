// Synthetic OIDC provider and real Jellyfin browser integration; no production accounts.
import { readFile } from "node:fs/promises";
import { createServer, request as httpRequest } from "node:http";
import { createServer as createHttpsServer } from "node:https";
import { generateKeyPairSync, sign, createHash, randomUUID } from "node:crypto";
import assert from "node:assert/strict";
import { dashboardChecks } from "./dashboard-browser.mjs";
import { samlFixture } from "./saml-fixture.mjs";
import { chromium } from "/driver/node_modules/playwright/index.mjs";

const state = JSON.parse(await readFile(process.argv[2], "utf8"));
const { token, userId, info } = state;
let base = state.base;
let proxy;
{
  process.env.NODE_TLS_REJECT_UNAUTHORIZED = "0"; // Synthetic self-signed proxy, confined to this test process.
  proxy = createHttpsServer(
    {
      key: await readFile("/test/proxy.key"),
      cert: await readFile("/test/proxy.crt"),
    },
    (req, res) => {
      const target = new URL(req.url, new URL(state.base).origin);
      const upstream = httpRequest(
        target,
        {
          method: req.method,
          headers: {
            ...req.headers,
            "x-forwarded-proto": "https",
            "x-forwarded-host": req.headers.host,
            "x-forwarded-for": "203.0.113.44",
          },
        },
        (response) => {
          res.writeHead(response.statusCode, response.headers);
          response.pipe(res);
        },
      );
      upstream.on("error", () => {
        res.writeHead(502);
        res.end();
      });
      req.pipe(upstream);
    },
  );
  await new Promise((resolve) => proxy.listen(0, "127.0.0.1", resolve));
  if (state.proxy) base = `https://127.0.0.1:${proxy.address().port}/jellyfin`;
}

const headers = {
  Authorization: `MediaBrowser Token="${token}"`,
  "Content-Type": "application/json",
};
async function api(path, data, method = data === undefined ? "GET" : "POST") {
  const response = await fetch(base + path, {
    method,
    headers,
    body: data === undefined ? undefined : JSON.stringify(data),
  });
  assert.ok(response.ok, `API ${path}: ${response.status}`);
  return response.status === 204 ? null : response.json();
}
const { privateKey, publicKey } = generateKeyPairSync("rsa", {
  modulusLength: 2048,
});
const jwk = {
  ...publicKey.export({ format: "jwk" }),
  kid: "synthetic",
  alg: "RS256",
  use: "sig",
};
const codes = new Map();
let identity = {
  sub: "stable-browser-subject",
  preferred_username: "browser-user",
  groups: ["allowed"],
};
let issuer;
const server = createServer(async (req, res) => {
  try {
    const url = new URL(req.url, issuer);
    let result;
    if (url.pathname.endsWith("openid-configuration"))
      result = {
        issuer,
        authorization_endpoint: issuer + "/authorize",
        token_endpoint: issuer + "/token",
        jwks_uri: issuer + "/jwks",
        userinfo_endpoint: issuer + "/userinfo",
        response_types_supported: ["code"],
        subject_types_supported: ["public"],
        id_token_signing_alg_values_supported: ["RS256"],
        token_endpoint_auth_methods_supported: ["client_secret_basic"],
      };
    else if (url.pathname === "/jwks") result = { keys: [jwk] };
    else if (url.pathname === "/authorize") {
      const code = randomUUID();
      codes.set(code, { params: url.searchParams, identity: { ...identity } });
      const callback = new URL(url.searchParams.get("redirect_uri"));
      callback.searchParams.set("state", url.searchParams.get("state"));
      callback.searchParams.set("code", code);
      res.writeHead(302, { Location: callback.href });
      res.end();
      return;
    } else if (url.pathname === "/token") {
      let data = "";
      for await (const chunk of req) data += chunk;
      const form = new URLSearchParams(data),
        code = form.get("code"),
        entry = codes.get(code);
      codes.delete(code);
      assert.ok(entry, "one-use authorization code");
      assert.equal(
        createHash("sha256")
          .update(form.get("code_verifier"))
          .digest("base64url"),
        entry.params.get("code_challenge"),
      );
      assert.equal(form.get("redirect_uri"), entry.params.get("redirect_uri"));
      const claims = {
        ...entry.identity,
        iss: issuer,
        aud: "jellyfin",
        iat: Math.floor(Date.now() / 1000),
        exp: Math.floor(Date.now() / 1000) + 300,
        nonce: entry.params.get("nonce"),
      };
      const encode = (x) =>
        Buffer.from(JSON.stringify(x)).toString("base64url");
      const input =
        encode({ alg: "RS256", kid: "synthetic" }) + "." + encode(claims);
      result = {
        access_token: "synthetic-access",
        token_type: "Bearer",
        expires_in: 300,
        id_token:
          input +
          "." +
          sign("RSA-SHA256", Buffer.from(input), privateKey).toString(
            "base64url",
          ),
      };
    } else if (url.pathname === "/userinfo") result = identity;
    else {
      res.writeHead(404);
      res.end();
      return;
    }
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify(result));
  } catch (error) {
    res.writeHead(400);
    res.end(JSON.stringify({ error: "invalid_request" }));
  }
});
await new Promise((resolve) => server.listen(0, "0.0.0.0", resolve));
issuer = `http://${state.gateway}:${server.address().port}`;
const config = {
  Enabled: true,
  OidEndpoint: issuer,
  OidClientId: "jellyfin",
  OidSecret: "synthetic-secret",
  DisableHttps: true,
  DisablePushedAuthorization: true,
  DoNotLoadProfile: true,
  RoleClaim: "groups",
  Roles: ["allowed"],
  EnableAuthorization: true,
  EnableAllFolders: true,
};
const samlProvider = await samlFixture(
  await readFile("/test/proxy.key"),
  await readFile("/test/proxy.crt"),
  state.gateway,
);
const browser = await chromium.launch({
  headless: true,
  args: ["--no-sandbox"],
});
try {
  await dashboardChecks(browser, { ...state, base });
  await api("/sso/OID/Add/browser", config);
  const certificate = (await readFile("/test/proxy.crt", "utf8"))
    .replace(/-----[^-]+-----/g, "")
    .replace(/\s/g, "");
  await api("/sso/SAML/Add/saml-runtime", {
    Enabled: true,
    SamlEndpoint: "https://idp.example/saml",
    SamlClientId: "synthetic-saml",
    SamlIssuer: "https://idp.example",
    SamlCertificate: certificate,
  });
  const saml = await fetch(base + "/sso/SAML/start/saml-runtime", {
    redirect: "manual",
  });
  assert.equal(
    saml.status,
    302,
    "SAML runtime dependency and request generation",
  );
  assert.ok(
    new URL(saml.headers.get("location")).searchParams.has("SAMLRequest"),
  );
  await api("/sso/SAML/Del/saml-runtime", undefined, "DELETE");
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await context.newPage();
  const errors = [];
  page.on("pageerror", (e) => errors.push(e.message));
  let lastAuthentication;
  await page.route("**/sso/OID/Auth/*", async (route) => {
    const response = await route.fetch();
    lastAuthentication = {
      status: response.status(),
      body: await response.json(),
    };
    await route.fulfill({ response });
  });
  async function login(path = "/sso/OID/start/browser") {
    const responsePromise = page.waitForResponse(
      (r) => r.url().includes("/sso/OID/Auth/browser"),
      { timeout: 20000 },
    );
    await page.goto(base + path);
    const response = await responsePromise;
    assert.equal(response.status(), 200, "SSO completion rejected");
    const result = lastAuthentication.body;
    await page.waitForURL("**/web/**", { timeout: 20000 });
    // Wait for this sign-in's token; other saved servers may already have one.
    await page.waitForFunction(
      (token) =>
        JSON.parse(localStorage.getItem("jellyfin_credentials")).Servers.some(
          (s) => s.AccessToken === token,
        ),
      result.AccessToken,
    );
    const credentials = await page.evaluate(() =>
      JSON.parse(localStorage.getItem("jellyfin_credentials")),
    );
    assert.equal(
      credentials.Servers.find((s) => s.Id === result.ServerId).UserId,
      result.User.Id,
    );
    const me = await fetch(base + "/Users/Me", {
      headers: { Authorization: `MediaBrowser Token="${result.AccessToken}"` },
    });
    assert.equal(me.status, 200);
    return result;
  }
  await context.addInitScript(() => {
    if (!localStorage.getItem("layout"))
      localStorage.setItem("layout", "modern");
  });
  const first = await login();
  if (state.proxy)
    assert.equal(first.SessionInfo.RemoteEndPoint, "203.0.113.44");
  identity = { ...identity, preferred_username: "renamed-external-user" };
  await page.evaluate(() => localStorage.setItem("layout", "desktop-legacy"));
  const renamed = await login("/sso/OID/p/browser");
  assert.equal(
    renamed.User.Id,
    first.User.Id,
    "stable subject preserves user GUID after external rename",
  );
  // Keep unrelated server credentials while signing into this server.
  // Inject them outside the web client, which rewrites storage from its in-memory copy.
  await page.goto(base + "/System/Info/Public");
  await page.evaluate(() => {
    const c = JSON.parse(localStorage.getItem("jellyfin_credentials"));
    c.Servers.push({
      Id: "other",
      AccessToken: "other-synthetic",
      ManualAddress: "https://other.invalid",
    });
    localStorage.setItem("jellyfin_credentials", JSON.stringify(c));
  });
  await login();
  assert.equal(
    await page.evaluate(
      () =>
        JSON.parse(localStorage.getItem("jellyfin_credentials")).Servers.find(
          (s) => s.Id === "other",
        ).AccessToken,
    ),
    "other-synthetic",
  );
  await page.goto(base + "/SSOViews/linking");
  await api("/Sessions/Capabilities/Full?id=" + first.SessionInfo.Id, {
    SupportsPersistentIdentifier: true,
  });
  const originalPolicy = (await api("/Users/" + first.User.Id)).Policy;
  const restrictions = [
    [{ MaxActiveSessions: 1 }, 400, "session limit"],
    [{ IsDisabled: true }, 403, "disabled account"],
    [{ EnableAllDevices: false, EnabledDevices: [] }, 400, "device allowlist"],
    [
      {
        AccessSchedules: [{ DayOfWeek: "Everyday", StartHour: 0, EndHour: 0 }],
      },
      403,
      "access schedule",
    ],
  ];
  if (state.proxy)
    restrictions.push([{ EnableRemoteAccess: false }, 403, "remote access"]);
  for (const [policy, status, label] of restrictions) {
    await api("/Users/" + first.User.Id + "/Policy", {
      ...originalPolicy,
      ...policy,
    });
    const denial = page.waitForResponse((r) =>
      r.url().includes("/sso/OID/Auth/browser"),
    );
    await page.goto(base + "/sso/OID/start/browser");
    assert.equal((await denial).status(), status, label);
    await api("/Users/" + first.User.Id + "/Policy", originalPolicy);
  }
  await login();
  config.EnableAuthorization = false;
  await api("/sso/OID/Add/browser", {
    ...config,
    SubjectLinks: (await api("/sso/OID/Get")).browser.SubjectLinks,
  });
  // Explicit linking uses a different local username and preserves the local session.
  identity = {
    sub: "subject-to-link",
    preferred_username: "external-different-name",
    groups: ["allowed"],
  };
  await page.goto(base + "/SSOViews/linking");
  await page.evaluate(
    ({ info, base, token, userId }) =>
      localStorage.setItem(
        "jellyfin_credentials",
        JSON.stringify({
          Servers: [
            {
              Id: info.Id,
              Name: info.ServerName,
              ManualAddress: base,
              LocalAddress: base,
              UserId: userId,
              AccessToken: token,
            },
          ],
        }),
      ),
    { ...state, base },
  );
  await page.goto(base + "/SSOViews/linking");
  await page
    .getByRole("button", { name: "Link account", exact: true })
    .first()
    .waitFor({ timeout: 10000 })
    .catch(async (error) => {
      throw new Error(
        "Linking page: " + (await page.locator("#status").textContent()),
      );
    });
  await page
    .getByRole("button", { name: "Link account", exact: true })
    .first()
    .click();
  await page.getByText("Account linked.").waitFor({ timeout: 20000 });
  const links = await api("/sso/OID/links/" + userId);
  assert.equal(links.browser.length, 1);
  assert.equal(
    await page.evaluate(
      () =>
        JSON.parse(localStorage.getItem("jellyfin_credentials")).Servers[0]
          .AccessToken,
    ),
    token,
  );
  const linked = await login();
  assert.equal(linked.User.Id.replaceAll("-", ""), userId.replaceAll("-", ""));
  await page.goto(base + "/SSOViews/linking");
  await page
    .getByRole("button", { name: "Unlink", exact: true })
    .first()
    .click();
  await page
    .getByRole("button", { name: "Confirm unlink", exact: true })
    .click();
  await page.waitForFunction(
    () =>
      !Array.from(document.querySelectorAll("button")).some(
        (b) => b.textContent === "Unlink",
      ),
  );
  assert.equal((await api("/sso/OID/links/" + userId)).browser.length, 0);
  // First-time username matching reuses the existing account without an explicit link.
  identity = {
    sub: "automatic-username-match",
    preferred_username: "local-admin",
    groups: ["allowed"],
  };
  const usersBeforeMatch = (await api("/Users")).map((u) => u.Id).sort();
  const matched = await login();
  assert.equal(matched.User.Id.replaceAll("-", ""), userId.replaceAll("-", ""));
  assert.deepEqual(
    (await api("/Users")).map((u) => u.Id).sort(),
    usersBeforeMatch,
  );
  identity = {
    ...identity,
    preferred_username: "renamed-after-automatic-match",
  };
  const renamedMatch = await login();
  assert.equal(renamedMatch.User.Id, matched.User.Id);
  // Inaccessible storage is reported before redeeming a completion.
  const blocked = await browser.newContext({ ignoreHTTPSErrors: true });
  await blocked.addInitScript(() =>
    Object.defineProperty(window, "localStorage", {
      get() {
        throw new Error("Storage blocked for test");
      },
    }),
  );
  const blockedPage = await blocked.newPage();
  identity = {
    sub: "storage-user",
    preferred_username: "storage-user",
    groups: ["allowed"],
  };
  await blockedPage.goto(base + "/sso/OID/start/browser");
  await blockedPage.getByText("Storage blocked for test").waitFor();
  assert.equal(
    (await api("/Users")).some((u) => u.Name === "storage-user"),
    false,
  );
  await blocked.close();
  // Real cross-site SAML POST login and linking with HttpOnly Secure cookies.
  base = `https://127.0.0.1:${proxy.address().port}${new URL(state.base).pathname.replace(/\/$/, "")}`;
  await api("/sso/SAML/Add/browser-saml", {
    Enabled: true,
    SamlEndpoint: samlProvider.issuer + "/saml",
    SamlIssuer: samlProvider.issuer,
    SamlClientId: "browser-saml-client",
    SamlCertificate: samlProvider.certificate,
    Roles: ["allowed"],
  });
  const samlContext = await browser.newContext({ ignoreHTTPSErrors: true });
  const samlPage = await samlContext.newPage();
  const samlLogin = samlPage.waitForResponse((r) =>
    r.url().includes("/sso/SAML/Auth/browser-saml"),
  );
  await samlPage.goto(base + "/sso/SAML/start/browser-saml");
  assert.equal(
    (await samlLogin).status(),
    200,
    "signed SAML browser completion",
  );
  await samlPage.waitForURL("**/web/**");
  const form = samlProvider.lastForm;
  const replay = await samlContext.request.post(form.callback, {
    form: { SAMLResponse: form.encoded, RelayState: form.relay },
  });
  assert.equal(replay.status(), 400, "SAML callback replay");
  await samlPage.goto(base + "/SSOViews/linking");
  await samlPage.evaluate(
    ({ base, token, userId, info }) =>
      localStorage.setItem(
        "jellyfin_credentials",
        JSON.stringify({
          Servers: [
            {
              Id: info.Id,
              ManualAddress: base,
              LocalAddress: base,
              UserId: userId,
              AccessToken: token,
            },
          ],
        }),
      ),
    { base, token, userId, info },
  );
  const count = (await api("/Users")).length;
  samlProvider.subject = "different-saml-name";
  await samlPage.reload();
  const section = samlPage.locator("section").filter({
    has: samlPage.getByRole("heading", {
      name: "browser-saml",
      exact: true,
    }),
  });
  await section.getByRole("button", { name: "Link account" }).click();
  await samlPage.getByText("Account linked.").waitFor();
  assert.equal(
    (await api("/sso/SAML/links/" + userId))["browser-saml"].length,
    1,
  );
  assert.equal(
    (await api("/Users")).length,
    count,
    "SAML linking never provisions",
  );
  for (const correlation of ["missing", "tampered"]) {
    samlProvider.correlation = correlation;
    const denied = samlPage.waitForResponse(
      (r) =>
        r.request().method() === "POST" && r.url().includes("/sso/SAML/post/"),
    );
    await samlPage.goto(base + "/sso/SAML/start/browser-saml");
    assert.equal(
      (await denied).status(),
      400,
      "SAML " + correlation + " correlation",
    );
  }
  samlProvider.correlation = "valid";
  samlProvider.role = "denied";
  samlProvider.subject = "denied-saml-user";
  const deniedRole = samlPage.waitForResponse(
    (r) =>
      r.request().method() === "POST" && r.url().includes("/sso/SAML/post/"),
  );
  await samlPage.goto(base + "/sso/SAML/start/browser-saml");
  assert.equal((await deniedRole).status(), 403, "SAML admission");
  assert.equal((await api("/Users")).length, count);
  await samlContext.close();
  assert.equal(errors.length, 0, errors.join("\n"));
  await context.close();
  console.log(
    "OIDC and signed SAML browser login/linking, callback aliases, stable identity, replay/correlation/admission rejection, host restrictions, multiple servers, and inaccessible storage passed",
  );
} finally {
  await browser.close();
  await samlProvider.close();
  await new Promise((resolve) => server.close(resolve));
  if (proxy) await new Promise((resolve) => proxy.close(resolve));
}
