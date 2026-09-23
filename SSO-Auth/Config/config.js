const pluginUniqueId = "505ce9d1-d916-42fa-86ca-673ef241d7df";

function element(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (tag === "button") {
    node.classList.add("emby-button");
    node.classList.add(
      className?.includes("sso-button-quiet") ? "button-flat" : "raised",
    );
  }
  if (tag === "input") node.classList.add("emby-input");
  if (text !== undefined) node.textContent = text;
  return node;
}

function iconButton(icon, label, extraClass = "") {
  const button = element(
    "button",
    "sso-button sso-button-quiet sso-icon-button " + extraClass,
  );
  button.type = "button";
  button.title = label;
  button.setAttribute("aria-label", label);
  const glyph = element("span", "material-icons", icon);
  glyph.setAttribute("aria-hidden", "true");
  button.append(glyph);
  return button;
}

export default function (view) {
  const $ = (selector) => view.querySelector(selector);
  const form = $("#sso-new-oidc-provider");
  const list = $("#sso-provider-list");
  let configuration;
  let folders = [];
  let selected = null;
  let pendingDelete = null;
  let dirty = false;
  let busy = false;
  let permissionEditor;
  let copyFeedbackTimer;

  const stylesheet = element("link");
  stylesheet.rel = "stylesheet";
  stylesheet.href = ApiClient.getUrl("web/configurationpage", {
    name: "SSO-Auth.css",
  });
  view.append(stylesheet);
  view.querySelectorAll(".sso-self-service-link").forEach((link) => {
    link.href = ApiClient.getUrl("SSOViews/linking");
  });

  function status(message = "", error = false) {
    const node = $("#sso-status");
    node.hidden = !message;
    node.textContent = message;
    node.classList.toggle("sso-status-error", error);
    node.setAttribute("role", error ? "alert" : "status");
  }

  function setBusy(value) {
    busy = value;
    $("#sso-editor-fields").disabled = value;
    view.querySelectorAll("button").forEach((button) => {
      button.disabled = value || button.dataset.unavailableUser === "true";
    });
    $("#AddProvider").disabled = value || !configuration;
    list.setAttribute("aria-busy", String(value));
    form.setAttribute("aria-busy", String(value));
  }

  async function run(action) {
    if (busy) return;
    setBusy(true);
    status();
    try {
      await action();
    } catch (error) {
      status(
        error?.message ||
          "Unable to save or load settings. Check your connection and try again.",
        true,
      );
    } finally {
      setBusy(false);
    }
  }

  function markDirty(value) {
    dirty = value;
    $("#sso-save-hint").textContent = value
      ? "You have unsaved changes."
      : "Changes apply after saving.";
  }

  function canLeave() {
    return !dirty || window.confirm("Discard your unsaved provider changes?");
  }

  function tab(key, focus = false) {
    view.querySelectorAll(".sso-tab").forEach((button) => {
      const active = button.dataset.panel === key;
      button.setAttribute("aria-selected", String(active));
      button.tabIndex = active ? 0 : -1;
      $("#sso-panel-" + button.dataset.panel).hidden = !active;
      if (active && focus) button.focus();
    });
  }

  function resetCopyFeedback() {
    clearTimeout(copyFeedbackTimer);
    const button = $("#CopyCallbackUrl");
    button.querySelector(".material-icons").textContent = "content_copy";
    button.title = "Copy callback URL";
    button.setAttribute("aria-label", "Copy callback URL");
  }

  function callback() {
    const name = $("#OidProviderName").value || "your-provider";
    const url = new URL(
      ApiClient.getUrl("sso/OID/redirect/" + encodeURIComponent(name)),
      location.href,
    );
    const scheme = $("#SchemeOverride").value.trim();
    const port = $("#PortOverride").value;
    if (scheme === "https" || scheme === "http") url.protocol = scheme + ":";
    if (port && Number(port) >= 1 && Number(port) <= 65535) url.port = port;
    if ($("#sso-callback-url").textContent !== url.href) {
      resetCopyFeedback();
      $("#sso-copy-status").textContent = "";
      $("#sso-copy-status").hidden = true;
    }
    $("#sso-callback-url").textContent = url.href;
  }

  function folderChoices(container, values = []) {
    container.replaceChildren();
    // Keep IDs for unavailable libraries visible so editing cannot silently remove them.
    const choices = [...folders];
    for (const id of values) {
      if (!choices.some((folder) => folder.Id === id))
        choices.push({ Id: id, Name: "Unavailable library (" + id + ")" });
    }
    if (!choices.length)
      container.append(
        element(
          "p",
          "sso-muted",
          "No libraries yet. Add a library in Jellyfin to select it here.",
        ),
      );
    for (const folder of choices) {
      const label = element("label", "sso-folder-choice emby-checkbox-label");
      const input = element("input", "folder-checkbox");
      input.type = "checkbox";
      input.classList.remove("emby-input");
      input.classList.add("emby-checkbox");
      input.dataset.id = folder.Id;
      input.checked = values.includes(folder.Id);
      label.append(input, element("span", "checkboxLabel", folder.Name));
      const outline = element("span", "checkboxOutline");
      const check = element(
        "span",
        "material-icons checkboxIcon checkboxIcon-checked",
        "check",
      );
      check.setAttribute("aria-hidden", "true");
      outline.append(check);
      label.append(outline);
      container.append(label);
    }
  }

  function chosenFolders(container) {
    return [...container.querySelectorAll(".folder-checkbox:checked")].map(
      (input) => input.dataset.id,
    );
  }

  function renderList() {
    list.replaceChildren();
    const providers = configuration.OidConfigs || {};
    const names = Object.keys(providers).sort((a, b) => a.localeCompare(b));
    $("#sso-provider-count").textContent = String(names.length);
    $("#sso-saml-note").hidden = !Object.keys(configuration.SamlConfigs || {})
      .length;
    if (!names.length) {
      const empty = element("div", "sso-empty");
      empty.append(
        element("h3", "", "Add your first provider"),
        element(
          "p",
          "sso-muted",
          "Connect an OpenID Connect service such as authentik, Authelia, or Keycloak to get started.",
        ),
      );
      list.append(empty);
    }
    for (const name of names) {
      const provider = providers[name];
      const row = element(
        "article",
        "sso-provider-row listItem listItem-border",
      );
      row.classList.toggle("is-selected", selected === name && !form.hidden);
      row.dataset.provider = name;
      const badge = element(
        "span",
        "material-icons listItemIcon listItemIcon-transparent",
        "account_circle",
      );
      badge.setAttribute("aria-hidden", "true");
      const description = element(
        "div",
        "sso-provider-description listItemBody",
      );
      const heading = element("div", "sso-provider-title");
      heading.append(
        element("h3", "listItemBodyText", name),
        element(
          "span",
          "sso-badge" + (provider.Enabled ? " sso-badge-active" : ""),
          provider.Enabled ? "Enabled" : "Disabled",
        ),
      );
      description.append(
        heading,
        element(
          "p",
          "sso-muted",
          provider.OidEndpoint || "Issuer URL not configured",
        ),
      );
      const actions = element("div", "sso-actions");
      const edit = iconButton("edit", "Edit " + name);
      edit.setAttribute("aria-controls", form.id);
      edit.setAttribute(
        "aria-expanded",
        String(selected === name && !form.hidden),
      );
      edit.addEventListener("click", () => {
        if (!busy && canLeave()) {
          openEditor(name);
          $("#sso-editor-title").scrollIntoView({ block: "nearest" });
          $("#sso-tab-connection").focus();
        }
      });
      const remove = iconButton("delete", "Delete " + name, "sso-danger-text");
      remove.addEventListener("click", () => {
        if (busy) return;
        pendingDelete = name;
        $("#sso-delete-message").textContent =
          `Delete “${name}”? Its settings and identity links will be removed. Jellyfin accounts and watch history will remain.`;
        $("#sso-delete-confirm").hidden = false;
        $("#CancelDelete").focus();
      });
      actions.append(edit, remove);
      row.append(badge, description, actions);
      list.append(row);
    }
  }

  function openEditor(name = null) {
    selected = name;
    const provider =
      name === null
        ? { Enabled: true, OidScopes: ["profile", "email"] }
        : configuration.OidConfigs[name];
    form.reset();
    $("#OidProviderName").value = name ?? "";
    $("#OidProviderName").readOnly = name !== null;
    form.querySelectorAll(".sso-text, .sso-number").forEach((input) => {
      input.value = provider[input.id] ?? "";
    });
    form.querySelectorAll(".sso-toggle").forEach((input) => {
      input.checked = Boolean(provider[input.id]);
    });
    form.querySelectorAll(".sso-line-list").forEach((input) => {
      input.value = (provider[input.id] || []).join("\n");
    });
    permissionEditor?.load(provider);
    $("#sso-editor-title").textContent = name === null ? "New provider" : name;
    $("#SaveProvider").textContent =
      name === null ? "Create provider" : "Save changes";
    $("#sso-delete-confirm").hidden = true;
    form.hidden = false;
    tab("connection");
    callback();
    markDirty(false);
    renderList();
  }

  function closeEditor() {
    selected = null;
    form.hidden = true;
    markDirty(false);
    renderList();
  }

  function validate() {
    const name = $("#OidProviderName");
    name.setCustomValidity(!name.value.trim() ? "Enter a provider name." : "");
    const scheme = $("#SchemeOverride");
    scheme.setCustomValidity(
      !scheme.value || ["https", "http"].includes(scheme.value.trim())
        ? ""
        : "Enter https or http, or leave this blank.",
    );
    const invalid = [...form.querySelectorAll("input, textarea")].find(
      (input) => !input.validity.valid,
    );
    if (!invalid) return true;
    const panel = invalid.closest("[role=tabpanel]");
    if (panel) tab(panel.id.replace("sso-panel-", ""));
    invalid.reportValidity();
    invalid.focus();
    return false;
  }

  function readProvider(
    existing = configuration?.OidConfigs?.[selected] || {},
  ) {
    const provider = { ...existing };
    form.querySelectorAll(".sso-text").forEach((input) => {
      provider[input.id] =
        input.id === "OidSecret" ? input.value : input.value.trim();
    });
    form.querySelectorAll(".sso-number").forEach((input) => {
      provider[input.id] = input.value ? Number(input.value) : null;
    });
    form.querySelectorAll(".sso-toggle").forEach((input) => {
      provider[input.id] = input.checked;
    });
    form.querySelectorAll(".sso-line-list").forEach((input) => {
      provider[input.id] = input.value
        .split("\n")
        .map((line) => line.trim())
        .filter(Boolean);
    });
    Object.assign(provider, permissionEditor?.read());
    return provider;
  }

  form.addEventListener("submit", (event) => {
    event.preventDefault();
    if (busy || !validate()) return;
    const name = selected ?? $("#OidProviderName").value.trim();
    run(async () => {
      const latest = await ApiClient.getPluginConfiguration(pluginUniqueId);
      latest.OidConfigs ||= {};
      if (selected === null && Object.hasOwn(latest.OidConfigs, name))
        throw new Error(
          "A provider with this name already exists. Choose another name or edit the existing provider.",
        );
      if (selected !== null && !Object.hasOwn(latest.OidConfigs, name))
        throw new Error(
          "This provider was removed elsewhere. Reload the page before making changes.",
        );
      const provider = readProvider(latest.OidConfigs[name] || {});
      Object.defineProperty(latest.OidConfigs, name, {
        value: provider,
        enumerable: true,
        configurable: true,
        writable: true,
      });
      await ApiClient.updatePluginConfiguration(pluginUniqueId, latest);
      configuration = latest;
      const activeTab = view.querySelector('.sso-tab[aria-selected="true"]')
        .dataset.panel;
      openEditor(name);
      tab(activeTab);
      status(`Saved “${name}”. New sign-ins will use these settings.`);
    });
  });

  form.addEventListener("input", (event) => {
    if (event.target.closest("#sso-permission-editor")) return;
    markDirty(true);
    callback();
    permissionEditor?.invalidate();
  });
  form.addEventListener("change", (event) => {
    if (event.target.closest("#sso-permission-editor")) return;
    markDirty(true);
    permissionEditor?.invalidate();
  });
  $("#AddProvider").addEventListener("click", () => {
    if (canLeave()) {
      openEditor();
      $("#OidProviderName").focus();
    }
  });
  $("#CloseEditor").addEventListener("click", () => {
    if (canLeave()) {
      closeEditor();
      $("#AddProvider").focus();
    }
  });
  $("#CopyCallbackUrl").addEventListener("click", async () => {
    if (busy) return;
    const button = $("#CopyCallbackUrl");
    const message = $("#sso-copy-status");
    const value = $("#sso-callback-url").textContent;
    button.disabled = true;
    resetCopyFeedback();
    message.textContent = "";
    message.hidden = true;
    try {
      try {
        await navigator.clipboard.writeText(value);
      } catch {
        // Jellyfin is also used over HTTP, where the Clipboard API is unavailable.
        const field = element("textarea", "sso-clipboard-field");
        field.value = value;
        field.readOnly = true;
        view.append(field);
        try {
          field.select();
          if (!document.execCommand("copy"))
            throw new Error("Copy unavailable");
        } finally {
          field.remove();
        }
      }
      if ($("#sso-callback-url").textContent === value) {
        button.querySelector(".material-icons").textContent = "check";
        button.title = "Callback URL copied";
        button.setAttribute("aria-label", "Callback URL copied");
        copyFeedbackTimer = setTimeout(resetCopyFeedback, 2000);
      }
    } catch {
      message.hidden = false;
      message.textContent =
        "Unable to copy. Select the callback URL and copy it manually.";
    } finally {
      button.disabled = false;
      if (view.isConnected) button.focus({ preventScroll: true });
    }
  });
  $("#CancelDelete").addEventListener("click", () => {
    $("#sso-delete-confirm").hidden = true;
    pendingDelete = null;
    $("#AddProvider").focus();
  });
  $("#ConfirmDelete").addEventListener("click", () => {
    if (!pendingDelete || !canLeave()) return;
    const name = pendingDelete;
    run(async () => {
      const latest = await ApiClient.getPluginConfiguration(pluginUniqueId);
      delete latest.OidConfigs[name];
      await ApiClient.updatePluginConfiguration(pluginUniqueId, latest);
      configuration = latest;
      pendingDelete = null;
      $("#sso-delete-confirm").hidden = true;
      const remaining = Object.keys(configuration.OidConfigs);
      if (selected === name || form.hidden) {
        closeEditor();
        if (remaining.length === 1) openEditor(remaining[0]);
      } else renderList();
      status(`Deleted “${name}”. Jellyfin accounts were preserved.`);
      $("#AddProvider").focus();
    });
  });
  view.querySelectorAll(".sso-tab").forEach((button, index, buttons) => {
    button.addEventListener("click", () => tab(button.dataset.panel));
    button.addEventListener("keydown", (event) => {
      const targets = {
        ArrowRight: (index + 1) % buttons.length,
        ArrowLeft: (index + buttons.length - 1) % buttons.length,
        Home: 0,
        End: buttons.length - 1,
      };
      if (Object.hasOwn(targets, event.key)) {
        event.preventDefault();
        tab(buttons[targets[event.key]].dataset.panel, true);
      }
    });
  });
  const beforeUnload = (event) => {
    if (dirty && view.isConnected) {
      event.preventDefault();
      event.returnValue = "";
    }
  };
  window.addEventListener("beforeunload", beforeUnload);
  view.addEventListener(
    "viewdestroy",
    () => {
      clearTimeout(copyFeedbackTimer);
      window.removeEventListener("beforeunload", beforeUnload);
    },
    { once: true },
  );

  async function load() {
    const [loaded, libraryResult, definitions, users, permissionModule] =
      await Promise.all([
        ApiClient.getPluginConfiguration(pluginUniqueId),
        ApiClient.getJSON(
          ApiClient.getUrl("Library/MediaFolders", { IsHidden: false }),
        ),
        ApiClient.getJSON(ApiClient.getUrl("sso/Permissions")),
        ApiClient.getJSON(ApiClient.getUrl("Users")),
        import(
          ApiClient.getUrl("web/configurationpage", {
            name: "SSO-Auth.permissions.js",
          })
        ),
      ]);
    configuration = loaded;
    folders = libraryResult.Items || [];
    permissionEditor = permissionModule.createPermissionEditor(
      $("#sso-permission-editor"),
      {
        element,
        definitions,
        users,
        folders,
        folderChoices,
        chosenFolders,
        changed: () => markDirty(true),
        readProvider,
        preview: (request) =>
          ApiClient.ajax({
            type: "POST",
            url: ApiClient.getUrl("sso/Permissions/Preview"),
            data: JSON.stringify(request),
            contentType: "application/json",
            dataType: "json",
          }),
      },
    );
    configuration.OidConfigs ||= {};
    renderList();
    const names = Object.keys(configuration.OidConfigs);
    if (names.length === 1) openEditor(names[0]);
  }
  async function initialize() {
    await run(load);
    if (!configuration) {
      const retry = element("button", "sso-button", "Try again");
      retry.type = "button";
      retry.addEventListener("click", initialize);
      const empty = element("div", "sso-empty");
      empty.append(
        element("p", "sso-muted", "Provider settings could not be loaded."),
        retry,
      );
      list.replaceChildren(empty);
    }
  }
  initialize();
}
