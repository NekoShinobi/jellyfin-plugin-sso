import assert from "node:assert/strict";
import { unavailableUserPreviewChecks } from "./preview-unavailable.mjs";

export async function permissionChecks(page, state) {
  await unavailableUserPreviewChecks(
    page,
    await page.evaluate(() =>
      ApiClient.getUrl("web/configurationpage", {
        name: "SSO-Auth.permissions.js",
      }),
    ),
  );
  const panel = page.locator("#sso-panel-permissions");
  const matrix = panel.locator(".sso-permission-rules");
  const grant = (key) =>
    matrix
      .locator("label")
      .filter({ has: page.locator(`[data-permission="${key}"]`) })
      .click();
  await page.getByRole("tab", { name: "Permissions", exact: true }).click();
  await page
    .locator("label")
    .filter({ has: page.locator("#EnableAuthorization") })
    .click();
  await page.locator("#sso-permission-scope").selectOption("groups");
  // "family" already exists: the dashboard fixture's AdminRoles became that group.
  await page.locator("#sso-permission-new-group").fill("downloaders");
  await page.getByRole("button", { name: "Add group", exact: true }).click();
  await grant("EnableContentDownloading");
  await page.locator("#sso-permission-new-group").fill("tv");
  await page.getByRole("button", { name: "Add group", exact: true }).click();
  await grant("EnableLiveTvAccess");
  await page.locator("#sso-preview-roles").fill("family\ndownloaders\ntv");
  const preview = async () => {
    const response = page.waitForResponse((r) =>
      r.url().endsWith("/sso/Permissions/Preview"),
    );
    await page.locator("#sso-preview-permissions").click();
    const result = await response;
    assert.equal(result.status(), 200, await result.text());
    await panel.locator(".sso-permission-table").waitFor();
    return result.json();
  };
  let result = await preview();
  const download = () =>
    result.Permissions.find((p) => p.Key === "EnableContentDownloading");
  assert.equal(download().Effective, true);
  assert.equal(download().Source, "Group: downloaders");
  assert.equal(result.Permissions.length, 24);
  // Groups only add: without the downloaders group the Everyone baseline applies.
  // "family" is the admitted sign-in role set by the dashboard checks.
  await page.locator("#sso-preview-roles").fill("family\ntv");
  result = await preview();
  assert.equal(download().Effective, false);
  assert.equal(download().Source, "Everyone");
  assert.equal(
    result.Permissions.find((p) => p.Key === "IsDisabled").Managed,
    false,
  );
  // Actual user overrides take precedence, and preview does not mutate their policy.
  await page.locator("#sso-permission-scope").selectOption("users");
  const target = await page
    .locator("#sso-permission-new-user option")
    .evaluateAll(
      (nodes, id) =>
        nodes.find(
          (n) => n.value.replaceAll("-", "") === id.replaceAll("-", ""),
        )?.value,
      state.userId,
    );
  assert.ok(target);
  await page.locator("#sso-permission-new-user").selectOption(target);
  await page
    .getByRole("button", { name: "Add user override", exact: true })
    .click();
  await matrix
    .locator('[data-permission="EnableContentDownloading"]')
    .selectOption("false");
  await page.locator("#sso-preview-user").selectOption(target);
  await page.locator("#sso-preview-roles").fill("family\ndownloaders\ntv");
  result = await preview();
  assert.equal(download().Effective, false);
  assert.equal(download().Source, "User override");
  await page.locator("#sso-permission-preserve-user").selectOption("true");
  result = await preview();
  assert.equal(result.Synchronize, false);
  assert.ok(result.Permissions.every((p) => !p.Managed));
  await page.locator("#sso-permission-preserve-user").selectOption("false");
  await page.getByRole("button", { name: "Save changes", exact: true }).click();
  await page
    .getByRole("status")
    .filter({ hasText: "Saved “Household”" })
    .waitFor();
  // Reopening after a server save retains the new rule structures and their baseline.
  await page.locator("#sso-permission-scope").selectOption("defaults");
  assert.equal(
    await matrix
      .locator('[data-permission="EnableContentDownloading"]')
      .inputValue(),
    "false",
  );
  await page.locator("#sso-preview-user").selectOption(target);
  await page.locator("#sso-preview-roles").fill("family\ndownloaders\ntv");
  result = await preview();
  assert.equal(download().Effective, false);
  await page.setViewportSize({ width: 390, height: 844 });
  assert.equal(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
    true,
  );
  await page.setViewportSize({ width: 1440, height: 1000 });
  console.log(
    "Permissions: additive group grants, user overrides, exemptions, full preview, save round-trip, and mobile layout passed",
  );
}
