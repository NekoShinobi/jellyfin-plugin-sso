// Adds "Account connections" to Jellyfin's user settings menu. The File
// Transformation plugin loads this script into the web client when installed.
(() => {
  const base = new URL(document.currentScript.src).pathname.replace(
    /\/SSOViews\/menu\.js$/i,
    "",
  );
  let available;
  // Show the entry only when a provider accepts sign-ins.
  function providersAvailable() {
    available ??= Promise.all(
      ["OID", "SAML"].map((mode) =>
        fetch(`${base}/sso/${mode}/GetNames`, { credentials: "same-origin" })
          .then((response) => (response.ok ? response.json() : []))
          .catch(() => []),
      ),
    ).then((lists) => lists.some((names) => names?.length));
    return available;
  }
  async function addLink(page) {
    const anchor =
      page.querySelector(".lnkQuickConnectPreferences") ||
      page.querySelector(".lnkUserProfile");
    if (!anchor || !(await providersAvailable())) return;
    if (page.querySelector(".lnkSsoConnections")) return;
    const link = document.createElement("a");
    link.className = "emby-button lnkSsoConnections listItem-border";
    link.href = `${base}/SSOViews/linking`;
    link.style.cssText = "display: block; margin: 0; padding: 0";
    const item = document.createElement("div");
    item.className = "listItem";
    const icon = document.createElement("span");
    icon.className = "material-icons listItemIcon listItemIcon-transparent";
    icon.setAttribute("aria-hidden", "true");
    icon.textContent = "link";
    const body = document.createElement("div");
    body.className = "listItemBody";
    const text = document.createElement("div");
    text.className = "listItemBodyText";
    text.textContent = "Account connections";
    body.append(text);
    item.append(icon, body);
    link.append(item);
    anchor.after(link);
  }
  new MutationObserver(() => {
    const page = document.querySelector("#myPreferencesMenuPage");
    if (page && !page.querySelector(".lnkSsoConnections")) addLink(page);
  }).observe(document.documentElement, { childList: true, subtree: true });
})();
