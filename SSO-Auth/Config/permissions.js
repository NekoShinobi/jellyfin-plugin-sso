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
    // The rule being edited; null means Everyone.
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
    previewStatus.hidden = true;
  }
  // The rule list shows Everyone first, then groups, then per-user overrides.
  const list = el("div", "sso-rule-list");
  const editor = el("div", "sso-permission-rules");
  const error = el("p", "sso-status sso-status-error");
  error.setAttribute("role", "alert");
  error.hidden = true;
  function showError(message = "") {
    error.textContent = message;
    error.hidden = !message;
  }

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

  const folderName = (id) =>
    folders.find((f) => f.Id === id)?.Name || `Unavailable library (${id})`;
  const permissionName = (key) =>
    definitions.find((p) => p.Key === key)?.Name || key;
  function shortList(items, empty = "") {
    if (!items.length) return empty;
    return items.length > 3
      ? `${items.slice(0, 3).join(", ")} +${items.length - 3} more`
      : items.join(", ");
  }
  function summary(rule, kind) {
    const parts = [];
    const folderList = (ids) => shortList(ids.map(folderName), "no libraries");
    if (kind === "defaults") {
      parts.push(
        rule.EnableAllFolders
          ? "All libraries"
          : folderList(rule.EnabledFolders),
      );
      const managed = Object.keys(rule.PermissionDefaults).length;
      parts.push(`${managed} permission${managed === 1 ? "" : "s"} managed`);
      return parts.join(" · ");
    }
    if (kind === "user" && rule.PreservePermissions)
      return "Keeps their Jellyfin permissions";
    const keys = Object.keys(rule.Permissions);
    if (kind === "group") {
      if (keys.length) parts.push(shortList(keys.map(permissionName)));
      if (rule.LibraryMode === "All") parts.push("All libraries");
      if (rule.LibraryMode === "Selected")
        parts.push("+ " + folderList(rule.Folders));
      return parts.join(" · ") || "Grants nothing yet";
    }
    if (keys.length)
      parts.push(
        shortList(
          keys.map(
            (k) => `${permissionName(k)} ${rule.Permissions[k] ? "on" : "off"}`,
          ),
        ),
      );
    if (rule.LibraryMode === "All") parts.push("All libraries");
    if (rule.LibraryMode === "Selected")
      parts.push("Only " + folderList(rule.Folders));
    return parts.join(" · ") || "No overrides yet";
  }

  function ruleRow(icon, name, detail, target, onRemove) {
    const row = el("div", "sso-rule-row");
    row.classList.toggle("is-selected", editing === target);
    const open = el("button", "sso-rule-open");
    // The shared element helper styles every button as a raised Jellyfin button.
    open.classList.remove("emby-button", "raised", "button-flat");
    open.type = "button";
    open.setAttribute("aria-label", `Edit ${name}`);
    open.setAttribute("aria-pressed", String(editing === target));
    const glyph = el("span", "material-icons sso-rule-icon", icon);
    glyph.setAttribute("aria-hidden", "true");
    const body = el("span", "sso-rule-text");
    body.append(
      el("span", "sso-rule-name", name),
      el("span", "sso-rule-detail sso-muted", detail),
    );
    open.append(glyph, body);
    open.addEventListener("click", () => {
      editing = target;
      renderEditor();
      details.scrollIntoView({ block: "nearest" });
    });
    row.append(open);
    if (onRemove) row.append(iconButton("delete", `Remove ${name}`, onRemove));
    return row;
  }

  function addRow(isUser, rules) {
    const target = isUser
      ? select([
          ["", "Choose a Jellyfin user"],
          ...users.map((u) => [u.Id, u.Name]),
        ])
      : input();
    target.id = isUser ? "sso-permission-new-user" : "sso-permission-new-group";
    if (!isUser) target.placeholder = "Group name from the role claim";
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
          "A rule for this group or user already exists. Select it in the list.",
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
    const row = el("div", "sso-rule-add");
    row.append(
      field(isUser ? "Jellyfin user" : "Group / role name", target),
      add,
    );
    return row;
  }

  function section(title, description, rows, empty, adder) {
    const box = el("section", "sso-rule-section");
    box.append(el("h4", "sso-rule-section-title", title));
    if (description)
      box.append(
        el("p", "sso-muted sso-rule-section-description", description),
      );
    const items = el("div", "sso-rule-items");
    if (rows.length) items.append(...rows);
    else items.append(el("p", "sso-empty sso-muted", empty));
    box.append(items);
    if (adder) box.append(adder);
    return box;
  }

  function renderList() {
    const remove = (rules, rule) => () => {
      rules.splice(rules.indexOf(rule), 1);
      if (editing === rule) editing = null;
      changed();
      renderEditor();
    };
    list.replaceChildren(
      el("div", "sso-rule-items sso-rule-everyone"),
      section(
        "Groups",
        "Members get everything their groups grant, on top of Everyone.",
        draft.GroupPermissions.map((rule) =>
          ruleRow(
            "group",
            rule.Role,
            summary(rule, "group"),
            rule,
            remove(draft.GroupPermissions, rule),
          ),
        ),
        "No groups yet.",
        addRow(false, draft.GroupPermissions),
      ),
      el("hr", "sso-rule-divider"),
      section(
        "User overrides",
        "Exceptions for one account. They win over Everyone and groups.",
        draft.UserPermissions.map((rule) =>
          ruleRow(
            "person",
            userName(rule.UserId),
            summary(rule, "user"),
            rule,
            remove(draft.UserPermissions, rule),
          ),
        ),
        "No user overrides.",
        addRow(true, draft.UserPermissions),
      ),
    );
    list.firstChild.append(
      ruleRow("public", "Everyone", summary(draft, "defaults"), null),
    );
  }

  const details = el("section", "sso-rule-editor");
  function renderEditor() {
    showError();
    if (
      editing &&
      !draft.GroupPermissions.includes(editing) &&
      !draft.UserPermissions.includes(editing)
    )
      editing = null;
    renderList();
    details.replaceChildren();
    const isUser = draft.UserPermissions.includes(editing);
    const kind = !editing ? "defaults" : isUser ? "user" : "group";
    const name = !editing
      ? "Everyone"
      : isUser
        ? userName(editing.UserId)
        : editing.Role;
    const heading = el("div", "sso-rule-editor-heading");
    const glyph = el(
      "span",
      "material-icons sso-rule-icon",
      { defaults: "public", group: "group", user: "person" }[kind],
    );
    glyph.setAttribute("aria-hidden", "true");
    const text = el("div");
    text.append(
      el("h3", "sso-rule-editor-title", name),
      el(
        "p",
        "sso-muted",
        {
          defaults:
            "Applies to every user of this provider. Leave a permission as Keep Jellyfin setting to let Jellyfin manage it.",
          group: "Tick what members of this group get.",
          user: "Set a permission On or Off for this account only.",
        }[kind],
      ),
    );
    heading.append(glyph, text);
    details.append(heading);
    if (kind === "defaults") {
      libraries(details, draft, "defaults");
      matrix(details, draft.PermissionDefaults, "defaults");
      editor.replaceChildren(list, details);
      return;
    }
    const rule = editing;
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
        renderList();
      });
      details.append(field("Permission management", preserve));
      ruleFields.hidden = Boolean(rule.PreservePermissions);
    }
    libraries(ruleFields, rule, kind);
    matrix(ruleFields, rule.Permissions, kind);
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
    editor.replaceChildren(list, details);
  }

  const previewSection = el("section", "sso-permission-preview");
  const previewHeading = el("h3", "", "Preview");
  const previewIntro = el(
    "p",
    "sso-muted",
    "See what a sign-in would do with the current, unsaved rules.",
  );
  const previewUser = select([
    ["", "A new user (no existing account)"],
    ...users.map((u) => [u.Id, u.Name]),
  ]);
  previewUser.id = "sso-preview-user";
  const previewRoles = el("textarea", "emby-textarea");
  previewRoles.rows = 3;
  previewRoles.id = "sso-preview-roles";
  previewRoles.placeholder = "One group / role per line";
  const membership = el("p", "fieldDescription");
  const defaultMembership =
    "Enter the groups the identity provider would send. Nothing is changed at the provider.";
  function useSnapshot() {
    const snapshot = snapshots[normalizeId(previewUser.value)];
    previewRoles.value = (snapshot?.Roles || []).join("\n");
    membership.textContent = snapshot
      ? `Groups from their last sign-in (${new Date(snapshot.ObservedAt).toLocaleString()}). Edit them to try another scenario.`
      : "This user hasn't signed in with this provider yet. Enter their groups.";
    invalidate();
  }
  previewUser.addEventListener("change", useSnapshot);
  previewRoles.addEventListener("input", invalidate);
  const previewStatus = el("p", "sso-status");
  previewStatus.setAttribute("role", "status");
  previewStatus.hidden = true;
  const result = el("div", "sso-preview-output");
  const previewButton = button("Preview sign-in", runPreview);
  previewButton.id = "sso-preview-permissions";
  previewButton.classList.add("sso-preview-button");
  const inputs = el("div", "sso-preview-inputs");
  inputs.append(
    field("User", previewUser),
    field("Groups", previewRoles),
    membership,
    previewButton,
  );
  previewSection.append(
    previewHeading,
    previewIntro,
    inputs,
    previewStatus,
    result,
  );

  // Server sources read "Group: a, b" and "Keep Jellyfin setting"; show plain labels.
  function from(source) {
    if (!source || source === "Keep Jellyfin setting") return "Not managed";
    return source
      .replace(/^Group: /, "")
      .replace(/, Group: /, " + ")
      .replace(/^Everyone, /, "Everyone + ");
  }
  const onOff = (value) => (value == null ? "—" : value ? "On" : "Off");

  function chip(text, variant = "") {
    return el("span", `sso-chip ${variant}`.trim(), text);
  }

  function renderResult(response, roles) {
    const who = previewUser.value ? userName(previewUser.value) : "a new user";
    const card = el("div", "sso-preview-result");
    const headline = el("div", "sso-preview-headline");
    const icon = el("span", "material-icons");
    icon.setAttribute("aria-hidden", "true");
    const title = el("h4");
    const subtitle = el("p", "sso-muted");
    headline.append(icon, el("div"));
    headline.lastChild.append(title, subtitle);
    card.append(headline);
    if (!response.Admitted) {
      card.classList.add("is-blocked");
      icon.textContent = "block";
      title.textContent = `Sign-in blocked for ${who}`;
      subtitle.textContent = response.BlockedReason;
      return card;
    }
    const matched = draft.GroupPermissions.filter((g) =>
      roles.includes(g.Role),
    ).map((g) => g.Role);
    const override = draft.UserPermissions.some(
      (r) => normalizeId(r.UserId) === normalizeId(previewUser.value),
    );
    if (!response.Synchronize) {
      icon.textContent = "lock";
      title.textContent = `${who[0].toUpperCase() + who.slice(1)} keeps their Jellyfin permissions`;
      subtitle.textContent = override
        ? "This user is exempt from permission management."
        : "Manage Jellyfin permissions is off for this provider.";
      return card;
    }
    icon.textContent = "how_to_reg";
    title.textContent = previewUser.value
      ? `When ${who} signs in`
      : "When a new user with these groups signs in";
    subtitle.textContent =
      [
        matched.length
          ? `Matching groups: ${matched.join(", ")}`
          : "No groups match",
        override ? "User override applies" : "",
      ]
        .filter(Boolean)
        .join(" · ") + ".";

    // Library access has its own section below.
    const managed = response.Permissions.filter(
      (p) => p.Managed && p.Key !== "EnableAllFolders",
    );
    const existing = Boolean(previewUser.value);
    const changes = existing
      ? managed.filter((p) => p.Change === "On" || p.Change === "Off")
      : managed;
    card.append(
      el("h5", "sso-preview-label", existing ? "Changes" : "Permissions set"),
    );
    if (!changes.length)
      card.append(
        el(
          "p",
          "sso-muted",
          existing
            ? "No permission changes. Everything SSO manages already matches."
            : "SSO doesn't manage any permissions for this user.",
        ),
      );
    else {
      const listing = el("ul", "sso-change-list");
      for (const p of changes) {
        const item = el("li");
        item.dataset.permission = p.Key;
        const value = el("span", "sso-change-value");
        if (existing) {
          value.append(
            el("span", "sso-muted", onOff(p.Current)),
            el("span", "material-icons sso-change-arrow", "arrow_forward"),
          );
          value.lastChild.setAttribute("aria-label", "becomes");
        }
        value.append(
          chip(onOff(p.Effective), p.Effective ? "is-on" : "is-off"),
        );
        item.append(
          el("span", "sso-change-name", p.Name),
          value,
          el("span", "sso-muted sso-change-source", from(p.Source)),
        );
        listing.append(item);
      }
      card.append(listing);
    }

    const library = response.Libraries;
    card.append(el("h5", "sso-preview-label", "Libraries"));
    const chips = el("div", "sso-chips");
    if (library.All) chips.append(chip("All libraries", "is-on"));
    else {
      const effective = library.Effective || [];
      const current = existing && !library.CurrentAll ? library.Current : null;
      for (const id of effective)
        chips.append(
          chip(
            folderName(id),
            current && !current.includes(id) ? "is-added" : "",
          ),
        );
      for (const id of current || [])
        if (!effective.includes(id))
          chips.append(chip(folderName(id), "is-removed"));
      if (existing && library.CurrentAll)
        chips.append(chip("All libraries", "is-removed"));
      if (!chips.childElementCount)
        chips.append(chip("No libraries", "is-off"));
    }
    card.append(
      chips,
      el("p", "sso-muted sso-preview-source", `From ${from(library.Source)}`),
    );

    const table = el("table", "sso-permission-table");
    const head = el("thead");
    const headRow = el("tr");
    for (const text of ["Permission", "Now", "After sign-in", "From"]) {
      const th = el("th", "", text);
      th.scope = "col";
      headRow.append(th);
    }
    head.append(headRow);
    const body = el("tbody");
    for (const p of response.Permissions) {
      const row = el("tr");
      row.dataset.permission = p.Key;
      row.classList.toggle(
        "is-changed",
        existing && (p.Change === "On" || p.Change === "Off"),
      );
      row.classList.toggle("is-unmanaged", !p.Managed);
      const th = el("th", "", p.Name);
      th.scope = "row";
      row.append(
        th,
        el("td", "", onOff(p.Current)),
        el("td", "", onOff(p.Effective)),
        el("td", "", from(p.Source)),
      );
      body.append(row);
    }
    table.append(head, body);
    const scroll = el("div", "sso-table-scroll");
    scroll.append(table);
    const all = el("details", "sso-preview-all");
    all.append(
      el("summary", "", `All permissions (${response.Permissions.length})`),
      scroll,
    );
    card.append(all);
    return card;
  }

  async function runPreview() {
    const ticket = ++revision;
    result.replaceChildren();
    previewStatus.hidden = false;
    previewStatus.classList.remove("sso-status-error");
    previewStatus.textContent = "Calculating…";
    previewButton.disabled = true;
    try {
      const roles = lines(previewRoles.value);
      const response = await options.preview({
        Configuration: options.readProvider(),
        UserId: previewUser.value || null,
        Roles: roles,
      });
      if (ticket !== revision) return;
      previewStatus.hidden = true;
      result.append(renderResult(response, roles));
    } catch (error) {
      if (ticket === revision) {
        previewStatus.classList.add("sso-status-error");
        previewStatus.textContent =
          error?.message ||
          "Unable to preview permissions. Check the rules and try again.";
      }
    } finally {
      previewButton.disabled = false;
    }
  }
  root.replaceChildren(error, editor, previewSection);
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
      editing = null;
      previewUser.value = "";
      previewRoles.value = "";
      membership.textContent = defaultMembership;
      renderEditor();
      invalidate();
    },
    read() {
      return structuredClone(draft);
    },
    invalidate,
  };
}
