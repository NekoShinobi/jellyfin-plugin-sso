import { readCredentials, serverEntry, request, authHeader } from "./web.js";

const status = document.querySelector("#status");
const container = document.querySelector("#providers");
const basePath = location.pathname.slice(
  0,
  location.pathname.toLowerCase().lastIndexOf("/ssoviews/"),
);
const baseUrl = location.origin + basePath;
document.querySelector("#home").href = `${baseUrl}/web/`;

function node(tag, className, text) {
  const element = document.createElement(tag);
  element.className = className;
  if (tag === "button") {
    element.classList.add("emby-button");
    element.classList.add(
      className.includes("sso-button-quiet") ? "button-flat" : "raised",
    );
    if (className.includes("sso-button-primary"))
      element.classList.add("button-submit");
    if (className.includes("sso-button-danger"))
      element.classList.add("button-delete");
  }
  if (text !== undefined) element.textContent = text;
  return element;
}
const formatDate = (value) =>
  value
    ? new Date(value).toLocaleString(undefined, {
        dateStyle: "medium",
        timeStyle: "short",
      })
    : null;
function host(issuer) {
  try {
    return new URL(issuer).host;
  } catch {
    return issuer;
  }
}
// Material icon paths, inline because standalone pages cannot load the web client's icon font.
const icons = {
  verified:
    "M12 1 3 5v6c0 5.55 3.84 10.74 9 12 5.16-1.26 9-6.45 9-12V5l-9-4zm-2 16-4-4 1.41-1.41L10 14.17l6.59-6.59L18 9l-8 8z",
  person:
    "M12 12c2.21 0 4-1.79 4-4s-1.79-4-4-4-4 1.79-4 4 1.79 4 4 4zm0 2c-2.67 0-8 1.34-8 4v2h16v-2c0-2.66-5.33-4-8-4z",
};
function icon(name) {
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("class", "sso-identity-icon");
  svg.setAttribute("aria-hidden", "true");
  const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
  path.setAttribute("d", icons[name]);
  svg.append(path);
  return svg;
}
function chip(text) {
  return node("span", "sso-chip", text);
}
function message(text, error = false) {
  status.hidden = !text;
  status.textContent = text;
  status.classList.toggle("sso-status-error", error);
  status.setAttribute("role", error ? "alert" : "status");
}

try {
  const info = await request(`${baseUrl}/System/Info/Public`);
  const session = serverEntry(readCredentials(), info, baseUrl);
  const headers = authHeader(session?.AccessToken);
  const userId = session?.UserId;
  if (!userId)
    throw new Error(
      "Sign in to Jellyfin, then return here to connect an account.",
    );
  const currentUser = await request(`${baseUrl}/Users/Me`, { headers });
  document.querySelector("#account-name").textContent = currentUser.Name;
  document.querySelector("#account-avatar").textContent =
    currentUser.Name.slice(0, 1).toUpperCase();
  document.querySelector("#account").hidden = false;
  const providers = await Promise.all(
    ["OID", "SAML"].map(async (mode) => {
      const [names, links] = await Promise.all([
        request(`${baseUrl}/sso/${mode}/GetNames`),
        request(
          `${baseUrl}/sso/${mode}/links/${encodeURIComponent(userId)}?details=true`,
          { headers },
        ),
      ]);
      return { mode, names, links };
    }),
  );

  for (const { mode, names, links } of providers) {
    // Disabled providers with existing links must remain available for unlinking.
    const visible = [
      ...new Set([
        ...names,
        ...Object.keys(links).filter((name) => links[name]?.Identities?.length),
      ]),
    ].sort((a, b) => a.localeCompare(b));
    for (const provider of visible) {
      const enabled = names.includes(provider);
      const detail = links[provider] || {};
      let identities = [...(detail.Identities || [])];
      const section = node("section", "sso-connection");
      section.dataset.provider = provider;
      const heading = node("div", "sso-connection-header");
      const title = node("div", "");
      title.append(
        node("p", "sso-muted", mode === "OID" ? "OpenID Connect" : "SAML"),
        node("h2", "", provider),
      );
      const badge = node("span", "sso-badge");
      heading.append(title, badge);
      const description = node("p", "sso-muted");
      const connections = node("div", "");
      const actions = node("div", "sso-actions");
      const link = node(
        "button",
        "sso-button sso-button-primary",
        "Link account",
      );
      link.type = "button";
      link.hidden = !enabled;
      link.addEventListener("click", async () => {
        link.disabled = true;
        link.textContent = "Opening provider…";
        try {
          const result = await request(
            `${baseUrl}/sso/${mode}/start/${encodeURIComponent(provider)}`,
            { method: "POST", headers },
          );
          location.assign(result.Url || result.url);
        } catch (error) {
          message(
            error.message || "Unable to open this provider. Try again.",
            true,
          );
          link.disabled = false;
          link.textContent = "Link account";
        }
      });
      actions.append(link);
      function updateState() {
        badge.textContent = !enabled
          ? "Provider disabled"
          : identities.length
            ? "Connected"
            : "Not connected";
        badge.classList.toggle(
          "sso-badge-active",
          enabled && identities.length > 0,
        );
        description.textContent = !enabled
          ? "New connections are paused. You can still remove an existing link."
          : identities.length
            ? "You can use this provider to sign in to your Jellyfin account."
            : "You’ll be asked to sign in with this provider to confirm the connection.";
        link.classList.toggle("sso-button-primary", identities.length === 0);
        link.classList.toggle("button-submit", identities.length === 0);
      }
      for (const identity of identities) {
        const group = node("div", "");
        const row = node("div", "sso-identity-row");
        const label = node("div", "sso-identity");
        const name = identity.Username || "Verified account";
        label.append(
          icon(identity.Verified ? "verified" : "person"),
          node("div", ""),
        );
        const text = label.lastChild;
        text.append(node("strong", "sso-identity-name", name));
        const facts = [
          identity.Issuer ? `From ${host(identity.Issuer)}` : null,
          formatDate(identity.LinkedAt)
            ? `Linked ${formatDate(identity.LinkedAt)}`
            : null,
          formatDate(identity.LastSignInAt)
            ? `Last sign-in ${formatDate(identity.LastSignInAt)}`
            : null,
        ].filter(Boolean);
        if (!identity.Verified)
          facts.unshift(
            "Older username link. Signing in with this provider adds a verified link.",
          );
        else if (!facts.length)
          facts.push(
            "Details appear after your next sign-in with this provider.",
          );
        for (const fact of facts)
          text.append(node("span", "sso-muted sso-identity-fact", fact));
        const remove = node(
          "button",
          "sso-button sso-button-quiet sso-danger-text",
          "Unlink",
        );
        remove.type = "button";
        const confirmation = node("div", "sso-confirm");
        confirmation.hidden = true;
        const help = node(
          "p",
          "",
          `Unlink this account from ${provider}? Make sure you have another way to sign in. Your Jellyfin account and watch history will stay.`,
        );
        const confirmationActions = node("div", "sso-actions");
        const confirm = node(
          "button",
          "sso-button sso-button-danger",
          "Confirm unlink",
        );
        const cancel = node("button", "sso-button", "Keep connection");
        confirm.type = cancel.type = "button";
        remove.addEventListener("click", () => {
          confirmation.hidden = false;
          remove.hidden = true;
          cancel.focus();
        });
        cancel.addEventListener("click", () => {
          confirmation.hidden = true;
          remove.hidden = false;
          remove.focus();
        });
        confirm.addEventListener("click", async () => {
          confirm.disabled = cancel.disabled = true;
          try {
            await request(
              `${baseUrl}/sso/${mode}/Link/${encodeURIComponent(provider)}/${encodeURIComponent(userId)}/${encodeURIComponent(identity.Key)}`,
              { method: "DELETE", headers },
            );
            identities = identities.filter((item) => item !== identity);
            if (!identities.length) groups.remove();
            group.remove();
            updateState();
            message(
              "Link removed. Your Jellyfin account and watch history are unchanged.",
            );
            (enabled ? link : document.querySelector("#home")).focus();
          } catch (error) {
            message(
              error.message || "Unable to remove this connection. Try again.",
              true,
            );
            confirm.disabled = cancel.disabled = false;
          }
        });
        confirmationActions.append(confirm, cancel);
        confirmation.append(help, confirmationActions);
        row.append(label, remove);
        group.append(row, confirmation);
        connections.append(group);
      }
      // Groups the provider sent at this user's last sign-in, as permission rules see them.
      const groups = node("div", "sso-identity-groups");
      if (identities.length && detail.Groups?.length) {
        const observed = formatDate(detail.GroupsObservedAt);
        groups.append(
          node(
            "p",
            "sso-muted",
            observed
              ? `Groups at your last sign-in (${observed})`
              : "Groups at your last sign-in",
          ),
          node("div", "sso-chips"),
        );
        groups.lastChild.append(...detail.Groups.map(chip));
      }
      updateState();
      section.append(heading, description, connections, groups, actions);
      container.append(section);
    }
  }
  if (!container.children.length) {
    const empty = node("div", "sso-empty");
    empty.append(
      node("h2", "", "No sign-in providers available"),
      node(
        "p",
        "sso-muted",
        "Your Jellyfin administrator hasn’t enabled a provider yet. You can keep using your current sign-in method.",
      ),
    );
    container.append(empty);
  }
  message();
} catch (error) {
  message(
    error.message ||
      "Unable to load account connections. Return to Jellyfin and try again.",
    true,
  );
} finally {
  container.setAttribute("aria-busy", "false");
}
