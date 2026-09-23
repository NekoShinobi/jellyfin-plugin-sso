export function createPermissionEditor(root, options) {
  const {
    element: el,
    iconButton,
    checkbox,
    definitions,
    users,
    folders,
    folderChoices,
    chosenFolders,
  } = options;
  let draft,
    snapshots,
    scope = "defaults",
    editing = null,
    revision = 0;
  let uid = 0;
  const normalizeId = (value) =>
    (value || "").replaceAll("-", "").toLowerCase();
  const userName = (id) =>
    users.find((u) => normalizeId(u.Id) === normalizeId(id))?.Name ||
    `Unavailable user (${id})`;
  const editable = definitions.filter((p) => p.Editable);
  // New providers start from the release defaults: no administrator or Live TV access.
  const newProviderDefaults = {
    IsAdministrator: false,
    EnableLiveTvAccess: false,
    EnableLiveTvManagement: false,
  };
  const lines = (value) => [
    ...new Set(
      value
        .split("\n")
        .map((v) => v.trim())
        .filter(Boolean),
    ),
  ];
  function button(text, action) {
    const node = el("button", "sso-button", text);
    node.type = "button";
    node.addEventListener("click", action);
    return node;
  }
  function field(labelText, input) {
    const isSelect = input.tagName === "SELECT";
    const box = el(
      "div",
      "sso-field " + (isSelect ? "selectContainer" : "inputContainer"),
    );
    input.id ||= "sso-permission-field-" + ++uid;
    const label = el(
      "label",
      isSelect ? "selectLabel" : "inputLabel inputLabelUnfocused",
      labelText,
    );
    label.htmlFor = input.id;
    box.append(label, input);
    if (isSelect) {
      const arrow = el("span", "selectArrowContainer");
      arrow.setAttribute("aria-hidden", "true");
      arrow.append(
        el("span", "material-icons selectArrow", "keyboard_arrow_down"),
      );
      box.append(arrow);
    }
    return box;
  }
  function select(choices, value = "") {
    const node = el("select", "emby-select emby-select-withcolor");
    for (const [key, label] of choices) {
      const option = el("option", "", label);
      option.value = key;
      node.append(option);
    }
    node.value = value;
    return node;
  }
  function input(value = "") {
    const node = el("input");
    node.type = "text";
    node.value = value;
    return node;
  }
  function changed() {
    options.changed();
    invalidate();
  }
  function invalidate() {
    revision++;
    result.replaceChildren();
    previewStatus.textContent =
      "Run preview to see the effect of the current draft.";
  }
  const note = el(
    "p",
    "sso-note",
    "Everyone sets the starting point. Matching groups can only add permissions and libraries. A user override wins last. Changes apply at the next sign-in.",
  );
  const scopeSelect = select([
    ["defaults", "Everyone"],
    ["groups", "Groups"],
    ["users", "User overrides"],
  ]);
  scopeSelect.id = "sso-permission-scope";
  const scopeField = field("Edit permissions for", scopeSelect);
  const editor = el("div", "sso-permission-rules");
  const error = el("p", "sso-status sso-status-error");
  error.setAttribute("role", "alert");
  error.hidden = true;
  function showError(message = "") {
    error.textContent = message;
    error.hidden = !message;
  }
  scopeSelect.addEventListener("change", () => {
    scope = scopeSelect.value;
    editing = null;
    renderEditor();
  });

  // kind is "defaults" (Keep/On/Off), "group" (grant checkboxes) or "user" (Inherit/On/Off).
  function matrix(container, values, kind) {
    const isDefault = kind === "defaults";
    const search = input();
    search.placeholder = "Filter permissions";
    container.append(field("Find a permission", search));
    const grid = el("div", "sso-permission-grid");
    // Rules require a managed baseline so a removed group cannot leave grants behind.
    function ensureBaseline(key) {
      if (!Object.hasOwn(draft.PermissionDefaults, key))
        draft.PermissionDefaults[key] = false;
    }
    for (const category of [...new Set(editable.map((p) => p.Category))]) {
      const section = el("fieldset", "sso-permission-category");
      section.append(el("legend", "", category));
      for (const definition of editable.filter(
        (p) => p.Category === category,
      )) {
        const searchText = (definition.Name + " " + category).toLowerCase();
        if (kind === "group") {
          const { label, box } = checkbox(
            definition.Name,
            values[definition.Key] === true,
            "sso-permission-grant",
          );
          box.dataset.permission = definition.Key;
          label.dataset.search = searchText;
          box.addEventListener("change", () => {
            if (box.checked) {
              values[definition.Key] = true;
              ensureBaseline(definition.Key);
            } else delete values[definition.Key];
            changed();
          });
          section.append(label);
          continue;
        }
        const control = select(
          [
            ["", isDefault ? "Keep Jellyfin setting" : "Inherit"],
            ["true", "On"],
            ["false", "Off"],
          ],
          Object.hasOwn(values, definition.Key)
            ? String(values[definition.Key])
            : "",
        );
        control.dataset.permission = definition.Key;
        const row = field(definition.Name, control);
        row.dataset.search = searchText;
        control.addEventListener("change", () => {
          if (control.value === "") delete values[definition.Key];
          else {
            values[definition.Key] = control.value === "true";
            if (!isDefault) ensureBaseline(definition.Key);
          }
          if (
            isDefault &&
            !Object.hasOwn(values, definition.Key) &&
            [...draft.GroupPermissions, ...draft.UserPermissions].some((r) =>
              Object.hasOwn(r.Permissions, definition.Key),
            )
          ) {
            values[definition.Key] = false;
            control.value = "false";
            showError(
              "This permission is used by a rule. Its default stays Off until you remove that permission from every rule.",
            );
          } else showError();
          changed();
        });
        section.append(row);
      }
      grid.append(section);
    }
    search.addEventListener("input", () => {
      const query = search.value.trim().toLowerCase();
      grid
        .querySelectorAll("[data-search]")
        .forEach((row) => (row.hidden = !row.dataset.search.includes(query)));
      grid
        .querySelectorAll("fieldset")
        .forEach(
          (section) =>
            (section.hidden = ![
              ...section.querySelectorAll("[data-search]"),
            ].some((row) => !row.hidden)),
        );
    });
    container.append(grid);
  }

  function libraries(container, rule, kind) {
    const everyone = kind === "defaults";
    const isUser = kind === "user";
    const mode = select(
      [
        ...(everyone
          ? []
          : [
              [
                "Inherit",
                isUser ? "Inherit library access" : "No additional libraries",
              ],
            ]),
        ["All", "All libraries"],
        [
          "Selected",
          everyone || isUser
            ? "Selected libraries only"
            : "Add selected libraries",
        ],
      ],
      everyone
        ? rule.EnableAllFolders
          ? "All"
          : "Selected"
        : rule.LibraryMode || "Inherit",
    );
    if (everyone) mode.id = "sso-everyone-libraries";
    else mode.dataset.libraryMode = "true";
    container.append(field("Library access", mode));
    const choices = el("div", "sso-folder-list");
    if (everyone) choices.id = "sso-everyone-folders";
    const folderKey = everyone ? "EnabledFolders" : "Folders";
    folderChoices(choices, rule[folderKey] || []);
    choices.hidden = mode.value !== "Selected";
    choices.addEventListener("change", () => {
      rule[folderKey] = chosenFolders(choices);
      changed();
    });
    mode.addEventListener("change", () => {
      if (everyone) rule.EnableAllFolders = mode.value === "All";
      else rule.LibraryMode = mode.value;
      choices.hidden = mode.value !== "Selected";
      changed();
    });
    container.append(
      choices,
      el(
        "p",
        "fieldDescription",
        everyone
          ? "All libraries includes libraries added later. Groups can add libraries to a selection."
          : isUser
            ? "Replaces the libraries from Everyone and groups. Selecting no libraries grants none."
            : "Adds to the libraries for everyone and from other matching groups. All libraries wins over any selection.",
      ),
    );
  }

  function renderEditor() {
    editor.replaceChildren();
    showError();
    if (scope === "defaults") {
      editor.append(
        el("h3", "", "Everyone"),
        el(
          "p",
          "fieldDescription",
          "Applies to every user of this provider. Keep a permission with Jellyfin, or set it On or Off. A permission that a group or user rule uses stays managed, Off unless you choose On.",
        ),
      );
      libraries(editor, draft, "defaults");
      matrix(editor, draft.PermissionDefaults, "defaults");
      return;
    }
    const isUser = scope === "users";
    const rules = isUser ? draft.UserPermissions : draft.GroupPermissions;
    const title = isUser ? "User overrides" : "Groups";
    editor.append(el("h3", "", title));
    const target = isUser
      ? select([
          ["", "Choose a Jellyfin user"],
          ...users.map((u) => [u.Id, u.Name]),
        ])
      : input();
    target.id = isUser ? "sso-permission-new-user" : "sso-permission-new-group";
    if (!isUser) target.placeholder = "e.g. family";
    const add = button(isUser ? "Add user override" : "Add group", () => {
      const value = target.value.trim();
      if (!value)
        return showError(
          isUser
            ? "Choose a user."
            : "Enter a group name from the provider's role claim.",
        );
      if (
        rules.some((r) =>
          isUser
            ? normalizeId(r.UserId) === normalizeId(value)
            : r.Role === value,
        )
      )
        return showError(
          "A rule for this group or user already exists. Edit it below.",
        );
      const rule = {
        Permissions: {},
        LibraryMode: "Inherit",
        Folders: [],
        ...(isUser
          ? { UserId: value, PreservePermissions: false }
          : { Role: value }),
      };
      rules.push(rule);
      editing = rule;
      changed();
      renderEditor();
    });
    const addRow = el("div", "sso-rule-add");
    addRow.append(
      field(isUser ? "Jellyfin user" : "Group / role name", target),
      add,
    );
    editor.append(addRow);
    const ruleList = el("div", "paperList");
    if (!rules.length)
      ruleList.append(
        el(
          "p",
          "sso-empty",
          isUser
            ? "No user overrides. Users get the Everyone and group settings."
            : "No groups. Add a group to grant its members permissions or libraries.",
        ),
      );
    for (const rule of rules) {
      const name = isUser ? userName(rule.UserId) : rule.Role;
      const row = el("div", "sso-provider-row listItem listItem-border");
      const body = el("div", "listItemBody");
      body.append(
        el("strong", "listItemBodyText", name),
        el(
          "p",
          "sso-muted",
          rule.PreservePermissions
            ? "Keep this user's Jellyfin permissions"
            : isUser
              ? `${Object.keys(rule.Permissions).length} permission overrides · ${rule.LibraryMode === "Inherit" ? "Inherited libraries" : rule.LibraryMode === "All" ? "All libraries" : `${rule.Folders.length} selected libraries`}`
              : `${Object.keys(rule.Permissions).length} permissions granted · ${rule.LibraryMode === "Inherit" ? "No additional libraries" : rule.LibraryMode === "All" ? "All libraries" : `${rule.Folders.length} libraries added`}`,
        ),
      );
      const actions = el("div", "sso-actions");
      actions.append(
        iconButton("edit", `Edit ${name}`, () => {
          editing = rule;
          renderEditor();
        }),
        iconButton("delete", `Remove ${name}`, () => {
          rules.splice(rules.indexOf(rule), 1);
          editing = null;
          changed();
          renderEditor();
        }),
      );
      row.append(body, actions);
      ruleList.append(row);
    }
    editor.append(ruleList);
    if (editing && rules.includes(editing)) {
      const rule = editing;
      const name = isUser ? userName(rule.UserId) : rule.Role;
      const details = el("div", "sso-rule-editor");
      details.append(
        el("h4", "", isUser ? `Overrides for ${name}` : `Granted to ${name}`),
      );
      const ruleFields = el("div");
      if (isUser) {
        const preserve = select(
          [
            ["false", "Apply Everyone, group, and user rules"],
            ["true", "Keep this user's Jellyfin permissions"],
          ],
          String(Boolean(rule.PreservePermissions)),
        );
        preserve.id = "sso-permission-preserve-user";
        preserve.addEventListener("change", () => {
          rule.PreservePermissions = preserve.value === "true";
          ruleFields.hidden = rule.PreservePermissions;
          changed();
        });
        details.append(field("Permission management", preserve));
        ruleFields.hidden = Boolean(rule.PreservePermissions);
      }
      libraries(ruleFields, rule, isUser ? "user" : "group");
      matrix(ruleFields, rule.Permissions, isUser ? "user" : "group");
      const targetUser = isUser
        ? users.find((u) => normalizeId(u.Id) === normalizeId(rule.UserId))
        : null;
      const unavailableUser = isUser && !targetUser;
      const preview = button(
        "Preview this " + (isUser ? "user" : "group"),
        () => {
          if (unavailableUser) return;
          if (isUser) {
            previewUser.value = targetUser.Id;
            useSnapshot();
          } else {
            previewUser.value = "";
            previewRoles.value = rule.Role;
            membership.textContent = "Testing the selected group.";
          }
          runPreview();
          previewHeading.scrollIntoView({ block: "start" });
        },
      );
      preview.disabled = unavailableUser;
      preview.dataset.unavailableUser = String(unavailableUser);
      details.append(ruleFields, preview);
      if (unavailableUser) {
        const explanation = el(
          "p",
          "fieldDescription",
          "Preview is unavailable because this Jellyfin user is not in the user list. Reload the page to refresh the list.",
        );
        explanation.id = "sso-preview-unavailable-user";
        preview.setAttribute("aria-describedby", explanation.id);
        details.append(explanation);
      }
      editor.append(details);
    }
  }

  const previewSection = el("section", "sso-permission-preview");
  const previewHeading = el("h3", "", "Preview effective permissions");
  const previewUser = select([
    ["", "Role-only preview (no existing user)"],
    ...users.map((u) => [u.Id, u.Name]),
  ]);
  previewUser.id = "sso-preview-user";
  const previewRoles = el("textarea", "emby-textarea");
  previewRoles.rows = 3;
  previewRoles.id = "sso-preview-roles";
  previewRoles.placeholder = "One group / role per line";
  const membership = el(
    "p",
    "fieldDescription",
    "Enter groups to test. This does not query or change membership at your identity provider.",
  );
  function useSnapshot() {
    const snapshot = snapshots[normalizeId(previewUser.value)];
    previewRoles.value = (snapshot?.Roles || []).join("\n");
    membership.textContent = snapshot
      ? `Groups last seen ${new Date(snapshot.ObservedAt).toLocaleString()}. Membership may have changed; edit the groups to test another scenario.`
      : "No SSO group history for this user and provider. Enter their groups to preview the result.";
    invalidate();
  }
  previewUser.addEventListener("change", useSnapshot);
  previewRoles.addEventListener("input", invalidate);
  const previewStatus = el("p", "sso-status");
  previewStatus.setAttribute("role", "status");
  const result = el("div");
  const previewButton = button("Preview permissions", runPreview);
  previewButton.id = "sso-preview-permissions";
  previewSection.append(
    previewHeading,
    field("Preview for user", previewUser),
    field("Groups / roles to test", previewRoles),
    membership,
    previewButton,
    previewStatus,
    result,
  );

  async function runPreview() {
    const ticket = ++revision;
    result.replaceChildren();
    previewStatus.textContent = "Calculating permissions…";
    previewButton.disabled = true;
    try {
      const response = await options.preview({
        Configuration: options.readProvider(),
        UserId: previewUser.value || null,
        Roles: lines(previewRoles.value),
      });
      if (ticket !== revision) return;
      previewStatus.textContent = !response.Admitted
        ? `Sign-in blocked: ${response.BlockedReason}. No permissions would change.`
        : !response.Synchronize
          ? "SSO will leave this user's Jellyfin permissions unchanged."
          : "Preview only. Save the provider to apply this policy at the next sign-in.";
      const table = el("table", "sso-permission-table");
      const caption = el(
        "caption",
        "",
        "All Jellyfin permission flags and the source of each result",
      );
      const head = el("thead");
      const headRow = el("tr");
      for (const title of [
        "Permission",
        "Current",
        "After SSO",
        "Change",
        "Source",
      ]) {
        const th = el("th", "", title);
        th.scope = "col";
        headRow.append(th);
      }
      head.append(headRow);
      table.append(caption, head);
      const body = el("tbody");
      const state = (value) =>
        value == null ? "Jellyfin default" : value ? "On" : "Off";
      for (const permission of response.Permissions) {
        const row = el("tr");
        row.dataset.permission = permission.Key;
        const heading = el("th", "", permission.Name);
        heading.scope = "row";
        row.append(
          heading,
          el("td", "", state(permission.Current)),
          el("td", "", state(permission.Effective)),
          el(
            "td",
            "",
            permission.Change === "Unmanaged" ? "Keep" : permission.Change,
          ),
          el("td", "", permission.Source),
        );
        body.append(row);
      }
      table.append(body);
      const scroll = el("div", "sso-table-scroll");
      scroll.tabIndex = 0;
      scroll.setAttribute("role", "region");
      scroll.setAttribute("aria-label", "Permission preview table");
      scroll.append(table);
      const library = response.Libraries;
      const names = (ids) =>
        ids == null
          ? "Jellyfin defaults"
          : ids.length
            ? ids
                .map(
                  (id) =>
                    folders.find((f) => f.Id === id)?.Name ||
                    `Unavailable library (${id})`,
                )
                .join(", ")
            : "No selected libraries";
      result.append(
        scroll,
        el("h4", "", "Library access"),
        el(
          "p",
          "",
          library.All
            ? "All libraries, including libraries added later."
            : names(library.Effective),
        ),
        el("p", "fieldDescription", library.Source),
        el("p", "fieldDescription", response.Note),
      );
    } catch (error) {
      if (ticket === revision)
        previewStatus.textContent =
          error?.message ||
          "Unable to preview permissions. Check the rules and try again.";
    } finally {
      previewButton.disabled = false;
    }
  }
  root.replaceChildren(note, scopeField, error, editor, previewSection);
  return {
    load(provider) {
      draft = structuredClone({
        PermissionDefaults: provider.PermissionDefaults || newProviderDefaults,
        GroupPermissions: provider.GroupPermissions || [],
        UserPermissions: provider.UserPermissions || [],
        EnableAllFolders: Boolean(provider.EnableAllFolders),
        EnabledFolders: provider.EnabledFolders || [],
      });
      for (const rule of [
        ...draft.GroupPermissions,
        ...draft.UserPermissions,
      ]) {
        rule.Permissions ||= {};
        rule.LibraryMode ||= "Inherit";
        rule.Folders ||= [];
      }
      snapshots = Object.fromEntries(
        Object.entries(provider.UserRoleSnapshots || {}).map(([key, value]) => [
          normalizeId(key),
          value,
        ]),
      );
      scope = "defaults";
      scopeSelect.value = scope;
      editing = null;
      previewUser.value = "";
      previewRoles.value = "";
      membership.textContent =
        "Enter groups to test. This does not query or change membership at your identity provider.";
      renderEditor();
      invalidate();
    },
    read() {
      return structuredClone(draft);
    },
    invalidate,
  };
}
