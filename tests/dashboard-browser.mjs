// Exercise the embedded dashboard in the real Jellyfin web client.
import assert from "node:assert/strict";
import { permissionChecks } from "./permission-browser.mjs";

export async function dashboardChecks(browser, state, screenshots) {
  const { base, token, username = "local-admin", password } = state;
  const plugin = "505ce9d1-d916-42fa-86ca-673ef241d7df";
  const url = `${base}/Plugins/${plugin}/Configuration`;
  const headers = {
    Authorization: `MediaBrowser Token="${token}"`,
    "Content-Type": "application/json",
  };
  async function configuration(value) {
    const response = await fetch(url, {
      headers,
      method: value ? "POST" : "GET",
      body: value ? JSON.stringify(value) : undefined,
    });
    assert.ok(response.ok, `Dashboard configuration API: ${response.status}`);
    return value ? null : response.json();
  }
  const original = await configuration();
  const fixture = {
    Enabled: true,
    OidEndpoint: "https://accounts.example.test/",
    OidClientId: "jellyfin-test",
    OidSecret: "private-test-secret",
    OidScopes: ["profile", "email"],
    RoleClaim: "groups",
    Roles: ["family"],
    EnableAuthorization: false,
    EnableAllFolders: false,
    EnabledFolders: ["missing-library"],
    // Release-shaped setting: converted into a group grant when saved.
    AdminRoles: ["family"],
  };
  const context = await browser.newContext({
    ignoreHTTPSErrors: true,
    viewport: { width: 1440, height: 1000 },
  });
  const page = await context.newPage();
  const errors = [];
  page.on("pageerror", (error) => errors.push(error.message));
  page.on("dialog", (dialog) => dialog.accept());
  async function dashboard() {
    await page.goto(`${base}/web/#/configurationpage?name=SSO-Auth`);
    await page.locator('#sso-provider-list[aria-busy="false"]').waitFor();
  }
  try {
    await configuration({ ...original, OidConfigs: { Household: fixture } });
    await page.goto(base + "/web/");
    await page.locator("#txtManualName").fill(username);
    await page.locator("#txtManualPassword").fill(password);
    await page.getByRole("button", { name: "Sign In", exact: true }).click();
    await page.waitForURL((url) => !url.hash.includes("login"));
    await dashboard();
    assert.equal(
      await page.locator("#OidProviderName").inputValue(),
      "Household",
      "single provider is prefilled with its dictionary key",
    );
    assert.equal(
      await page.getByLabel("Additional scopes", { exact: true }).inputValue(),
      fixture.OidScopes.join("\n"),
    );
    assert.equal(
      await page.locator("#OidSecret").inputValue(),
      fixture.OidSecret,
    );
    assert.equal(
      await page.locator("#OidSecret").getAttribute("type"),
      "password",
    );
    assert.equal(
      await page.locator("#OidProviderName").getAttribute("readonly"),
      "",
    );
    await page.getByRole("tab", { name: "Permissions", exact: true }).click();
    assert.equal(await page.locator("#EnableAuthorization").isChecked(), false);
    assert.equal(
      await page.locator("#sso-everyone-folders input:checked").count(),
      1,
    );
    await page.getByRole("tab", { name: "Sign-in", exact: true }).click();
    await page.locator("#Roles").fill("family\nfriends");
    await page
      .getByRole("button", { name: "Save changes", exact: true })
      .click();
    await page
      .getByRole("status")
      .filter({ hasText: "Saved “Household”" })
      .waitFor();
    const saved = (await configuration()).OidConfigs.Household;
    assert.deepEqual(saved.Roles, ["family", "friends"]);
    assert.deepEqual(saved.EnabledFolders, ["missing-library"]);
    assert.deepEqual(saved.AdminRoles, []);
    assert.deepEqual(saved.GroupPermissions, [
      {
        Role: "family",
        Permissions: { IsAdministrator: true },
        LibraryMode: "Inherit",
        Folders: [],
      },
    ]);
    assert.equal(saved.PermissionDefaults.IsAdministrator, false);
    assert.equal(saved.EnableAuthorization, false);
    await permissionChecks(page, state);

    // Organization-scoped roles require an explicit allowlist, then survive reload.
    await page.getByRole("tab", { name: "Sign-in", exact: true }).click();
    await page
      .locator("label")
      .filter({ has: page.locator("#UseZitadelRoles") })
      .click();
    await page.locator("#SaveProvider").click();
    assert.equal(
      await page
        .locator("#ZitadelOrganizationIds")
        .evaluate((node) => node.validity.valid),
      false,
    );
    assert.equal(
      (await configuration()).OidConfigs.Household.UseZitadelRoles,
      false,
    );
    await page.locator("#ZitadelOrganizationIds").fill("org-a\norg-b");
    await page.locator("#SaveProvider").click();
    await page
      .getByRole("status")
      .filter({ hasText: "Saved “Household”" })
      .waitFor();
    const scoped = (await configuration()).OidConfigs.Household;
    assert.equal(scoped.UseZitadelRoles, true);
    assert.deepEqual(scoped.ZitadelOrganizationIds, ["org-a", "org-b"]);
    await page.reload();
    await page.locator('#sso-provider-list[aria-busy="false"]').waitFor();
    assert.equal(await page.locator("#UseZitadelRoles").isChecked(), true);
    assert.equal(
      await page.locator("#ZitadelOrganizationIds").inputValue(),
      "org-a\norg-b",
    );

    // New provider fields do not inherit secrets or permission toggles.
    await page
      .getByRole("button", { name: "Add provider", exact: true })
      .click();
    assert.equal(await page.locator("#OidSecret").inputValue(), "");
    await page
      .getByRole("button", { name: "Create provider", exact: true })
      .click();
    assert.equal(
      await page
        .locator("#OidProviderName")
        .evaluate((node) => node.validity.valid),
      false,
    );
    await page.locator("#OidProviderName").fill("Office");
    // An incomplete disabled provider is a saveable draft.
    await page.locator("#OidEndpoint").fill("");
    await page.locator("#OidClientId").fill("");
    await page
      .locator("label")
      .filter({ has: page.locator("#Enabled") })
      .click();
    await page
      .getByRole("button", { name: "Create provider", exact: true })
      .click();
    await page
      .getByRole("status")
      .filter({ hasText: "Saved “Office”" })
      .waitFor();
    assert.equal(await page.locator(".sso-provider-row").count(), 2);
    assert.equal(await page.locator("#OidProviderName").inputValue(), "Office");
    await page.reload();
    await page.locator('#sso-provider-list[aria-busy="false"]').waitFor();
    assert.equal(
      await page.locator("#sso-new-oidc-provider").isVisible(),
      false,
      "multiple providers start at the list",
    );
    await page
      .getByRole("button", { name: "Edit Household", exact: true })
      .click();
    assert.equal(
      await page.locator("#OidSecret").inputValue(),
      fixture.OidSecret,
    );
    await page
      .getByRole("button", { name: "Edit Office", exact: true })
      .click();
    assert.equal(await page.locator("#OidSecret").inputValue(), "");
    assert.equal(await page.locator("#Enabled").isChecked(), false);

    // Enabling the incomplete draft is rejected before a configuration write.
    await page
      .locator("label")
      .filter({ has: page.locator("#Enabled") })
      .click();
    await page.locator("#SaveProvider").click();
    assert.equal(
      await page
        .locator("#OidEndpoint")
        .evaluate((node) => node.validity.valid),
      false,
    );
    assert.equal((await configuration()).OidConfigs.Office.Enabled, false);
    await page
      .locator("label")
      .filter({ has: page.locator("#Enabled") })
      .click();

    // A failed write is visible and leaves the user's draft editable.
    await page.route("**/Plugins/*/Configuration", (route) =>
      route.request().method() === "POST"
        ? route.fulfill({
            status: 500,
            contentType: "application/json",
            body: "{}",
          })
        : route.continue(),
    );
    await page.locator("#OidClientId").fill("retry-client");
    await page
      .getByRole("button", { name: "Save changes", exact: true })
      .click();
    await page.locator("#sso-status[role=alert]").waitFor();
    assert.equal(
      await page.locator("#OidClientId").inputValue(),
      "retry-client",
    );
    assert.equal(await page.locator("#SaveProvider").isEnabled(), true);
    await page.unroute("**/Plugins/*/Configuration");
    await page
      .getByRole("button", { name: "Save changes", exact: true })
      .click();
    await page
      .getByRole("status")
      .filter({ hasText: "Saved “Office”" })
      .waitFor();
    await page
      .getByRole("button", { name: "Delete Office", exact: true })
      .click();
    await page.getByRole("button", { name: "Cancel", exact: true }).click();
    assert.equal(await page.locator(".sso-provider-row").count(), 2);
    if (screenshots)
      await page.screenshot({
        path: `${screenshots}/providers.png`,
        fullPage: true,
      });
    await page
      .getByRole("button", { name: "Delete Office", exact: true })
      .click();
    await page
      .getByRole("button", { name: "Delete provider", exact: true })
      .click();
    await page
      .getByRole("status")
      .filter({ hasText: "Deleted “Office”" })
      .waitFor();
    assert.equal(
      await page.locator("#OidProviderName").inputValue(),
      "Household",
    );
    assert.equal(await page.locator(".sso-provider-row").count(), 1);

    await page.setViewportSize({ width: 390, height: 844 });
    assert.ok(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
      "dashboard fits mobile width",
    );
    await page.getByRole("tab", { name: "Connection", exact: true }).focus();
    await page.keyboard.press("ArrowRight");
    assert.equal(
      await page
        .getByRole("tab", { name: "Sign-in", exact: true })
        .getAttribute("aria-selected"),
      "true",
    );
    if (screenshots)
      await page.screenshot({
        path: `${screenshots}/dashboard-mobile.png`,
        fullPage: true,
      });
    await page.goto(base + "/SSOViews/linking");
    await page.locator('#providers[aria-busy="false"]').waitFor();
    assert.equal(await page.locator("#account-name").textContent(), username);
    assert.ok(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
      "connections fit mobile width",
    );
    if (screenshots)
      await page.screenshot({
        path: `${screenshots}/connections-mobile.png`,
        fullPage: true,
      });
    await dashboard();
    await page
      .getByRole("button", { name: "Delete Household", exact: true })
      .click();
    await page
      .getByRole("button", { name: "Delete provider", exact: true })
      .click();
    await page.getByText("Add your first provider", { exact: true }).waitFor();
    assert.equal(
      await page.locator("#sso-new-oidc-provider").isVisible(),
      false,
    );
    assert.deepEqual(errors, []);
    console.log(
      "Dashboard: automatic loading, settings round-trip, list/edit/create/delete, failure recovery, keyboard tabs, empty state, and mobile layout passed",
    );
  } finally {
    await context.close();
    await configuration(original);
  }
}
