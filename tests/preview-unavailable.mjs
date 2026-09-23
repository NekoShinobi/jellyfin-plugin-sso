import assert from "node:assert/strict";

export async function unavailableUserPreviewChecks(page, moduleUrl) {
  const result = await page.evaluate(async (url) => {
    const { createPermissionEditor } = await import(url);
    const {
      element: el,
      iconButton,
      checkbox,
    } = await import(url.replace("SSO-Auth.permissions.js", "SSO-Auth.js"));
    const root = document.createElement("div");
    const known = "11111111-1111-1111-1111-111111111111";
    const missing = "22222222-2222-2222-2222-222222222222";
    const requests = [];
    const editor = createPermissionEditor(root, {
      element: el,
      iconButton,
      checkbox,
      definitions: [],
      users: [{ Id: known, Name: "Existing user" }],
      folders: [],
      folderChoices: () => {},
      chosenFolders: () => [],
      changed: () => {},
      readProvider: () => editor.read(),
      preview: async (request) => {
        requests.push(request);
        return {
          Admitted: true,
          Synchronize: false,
          Permissions: [],
          Libraries: { All: false, Effective: [], Source: "Test" },
        };
      },
    });
    const provider = {
      UserPermissions: [known, missing].map((UserId) => ({
        UserId,
        Permissions: {},
        LibraryMode: "Inherit",
        Folders: [],
      })),
    };
    editor.load(provider);
    const edit = (name) =>
      [...root.querySelectorAll("button")]
        .find((b) => b.getAttribute("aria-label") === "Edit " + name)
        .click();
    const preview = () =>
      [...root.querySelectorAll("button")].find(
        (b) => b.textContent === "Preview this user",
      );
    edit("Unavailable user (" + missing + ")");
    const disabled = preview().disabled;
    const persistentDisabled = preview().dataset.unavailableUser;
    const explanation = root.querySelector(
      "#" + preview().getAttribute("aria-describedby"),
    )?.textContent;
    preview().click();
    const callsForMissingUser = requests.length;
    edit("Existing user");
    const availableEnabled = !preview().disabled;
    preview().click();
    await Promise.resolve();
    return {
      disabled,
      persistentDisabled,
      explanation,
      callsForMissingUser,
      availableEnabled,
      submittedId: requests[0]?.UserId,
      known,
    };
  }, moduleUrl);
  assert.equal(result.disabled, true);
  assert.equal(result.persistentDisabled, "true");
  assert.match(result.explanation, /Reload the page/);
  assert.equal(result.callsForMissingUser, 0);
  assert.equal(result.availableEnabled, true);
  assert.equal(result.submittedId, result.known);
}
