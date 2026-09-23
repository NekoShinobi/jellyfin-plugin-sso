export function readCredentials(storage = localStorage) {
  const raw = storage.getItem("jellyfin_credentials");
  const invalid = () =>
    new Error(
      "Stored Jellyfin credentials are invalid. Sign in through Jellyfin again.",
    );
  let credentials;
  try {
    credentials = JSON.parse(raw ?? '{"Servers":[]}');
  } catch {
    throw invalid();
  }
  if (
    !credentials ||
    !Array.isArray(credentials.Servers) ||
    !credentials.Servers.every(
      (server) =>
        server && typeof server === "object" && !Array.isArray(server),
    )
  )
    throw invalid();
  return credentials;
}

export function serverEntry(credentials, info, baseUrl) {
  const normalize = (value) => String(value || "").replace(/\/$/, "");
  return (
    credentials.Servers.find((server) => server.Id === info.Id) ||
    credentials.Servers.find(
      (server) =>
        !server.Id &&
        [server.LocalAddress, server.ManualAddress, server.RemoteAddress].some(
          (address) => normalize(address) === normalize(baseUrl),
        ),
    )
  );
}

export function saveLogin(
  credentials,
  info,
  result,
  baseUrl,
  storage = localStorage,
) {
  if (result.ServerId !== info.Id || !result.AccessToken || !result.User?.Id)
    throw new Error("Jellyfin returned an unexpected login response.");
  let server = serverEntry(credentials, info, baseUrl);
  if (!server) {
    server = {};
    credentials.Servers.push(server);
  }
  Object.assign(server, {
    Id: info.Id,
    Name: info.ServerName,
    LocalAddress: baseUrl,
    ManualAddress: baseUrl,
    UserId: result.User.Id,
    AccessToken: result.AccessToken,
    DateLastAccessed: Date.now(),
  });
  storage.setItem("jellyfin_credentials", JSON.stringify(credentials));
}

export async function request(url, options = {}) {
  const response = await fetch(url, {
    credentials: "same-origin",
    cache: "no-store",
    ...options,
    signal: AbortSignal.timeout(35000),
  });
  if (!response.ok) {
    const body = await response.json().catch(() => ({}));
    const message =
      body && typeof body === "object" && !Array.isArray(body)
        ? [body.Error, body.error].find(
            (value) => typeof value === "string" && value.trim(),
          )
        : undefined;
    throw new Error(
      message ||
        `Request failed (${response.status}). Sign in again or review the provider configuration.`,
    );
  }
  return response.status === 204 ? null : response.json();
}

export function authHeader(token) {
  if (!/^[A-Za-z0-9_-]+$/.test(token || ""))
    throw new Error(
      "Your Jellyfin session is missing or invalid. Sign in first.",
    );
  return { Authorization: `MediaBrowser Token="${token}"` };
}

function newDeviceId() {
  if (typeof crypto.randomUUID === "function") return crypto.randomUUID();
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = Array.from(bytes, (value) =>
    value.toString(16).padStart(2, "0"),
  ).join("");
  return [
    hex.slice(0, 8),
    hex.slice(8, 12),
    hex.slice(12, 16),
    hex.slice(16, 20),
    hex.slice(20),
  ].join("-");
}

export function device(storage = localStorage) {
  let id = storage.getItem("_deviceId2");
  if (!id) {
    id = newDeviceId();
    storage.setItem("_deviceId2", id);
  }
  return {
    DeviceID: id,
    DeviceName: "SSO Web",
    AppName: "Jellyfin Web",
    AppVersion: "12",
  };
}

export function sameUser(left, right) {
  return (
    String(left).replaceAll("-", "").toLowerCase() ===
    String(right).replaceAll("-", "").toLowerCase()
  );
}
