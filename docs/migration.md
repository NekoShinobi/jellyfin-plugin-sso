# Migration from upstream

The 5.x plugin targets Jellyfin 12.0 and later 12.x releases. It retains the plugin
GUID `505ce9d1-d916-42fa-86ca-673ef241d7df`, `SSO-Auth.xml` configuration path,
provider names, secrets, route aliases, and existing Jellyfin user GUIDs.

## Before upgrading

!!! warning "Back up before upgrading"

    Back up Jellyfin's database, configuration, and plugin files while Jellyfin is
    stopped. Record the installed versions, provider names, and exact callback URLs.
    Keep a working local administrator account and test against a copy of server data.
    Treat configuration exports and backups as secrets.

Install only one SSO plugin version; duplicate plugin directories can register
ambiguous routes. Use this fork's catalog once a suitable release exists.
An upstream .NET 9 plugin cannot run on a Jellyfin 12 host.

## Replacing installed plugin files

1. With Jellyfin stopped and the backup complete, inspect the actual plugin
   directory used by this installation or container volume.
2. Locate every directory containing an SSO plugin assembly. Move old SSO plugin
   directories outside the plugin tree into the backup; merely renaming a
   directory inside the tree can leave it discoverable.
3. Preserve `SSO-Auth.xml` and its link dictionaries in the configuration location.
   Do not remove Jellyfin users, the database, or unrelated plugins.
4. Extract the complete new package into one dedicated plugin directory, then
   start Jellyfin and check that only one SSO version loads.
5. If `AmbiguousMatchException` remains, inspect startup logs and mounted volumes
   for another SSO copy. Recheck the effective plugin path before trying again.

After restoring server data, review Known Proxies, Base URL, the public scheme
and port, and registered callbacks. A restored database does not restore an
external reverse proxy's settings. See [proxy troubleshooting](troubleshooting.md#public-urls-and-proxies).

## Configuration migration

On first import, the plugin saves the original configuration as
`SSO-Auth.xml.pre-v1.bak`, then normalizes missing/null collections and writes
schema version 1 atomically. Subsequent starts leave that backup intact.
Unreadable XML or a newer unsupported schema stops plugin initialization instead
of replacing the configuration with defaults. An incomplete disabled provider
remains stored and does not erase other providers.

Existing users do not need to relink solely because of the upgrade. Saved
`CanonicalLinks` username mappings continue to select the same Jellyfin GUID,
even if the local account has been renamed. If neither a stable identity link
nor a saved username mapping exists, login automatically matches an existing
Jellyfin username before creating a user, as upstream did.

After provider admission and Jellyfin account-policy checks pass, the plugin saves
a `SubjectLinks` entry for the validated provider identity. That entry takes
precedence on subsequent logins, so external or local username changes retain
the same account, watch history, and preferences. Legacy mappings remain stored
and usable; a mapping to a deleted user requires repair instead of falling back
to another account or creating a replacement.

Username matching trusts the configured identity provider to control its username
namespace, including names that match Jellyfin administrators. This compatibility
behavior does not require proof of the existing account's local password. See
[account matching](accounts-and-clients.md#automatic-account-matching).

Old invalid `SSOController` authentication-provider IDs are changed through
Jellyfin's user API to the registered SSO provider. Valid local/LDAP provider IDs
remain unchanged. The SSO provider rejects password login; configure a registered
fallback and password separately when needed. Startup logs report counts only.

SAML administrators must set the exact `SamlIssuer` and verify a stable
`SamlNameIdFormat`. Transient identities and unsolicited IdP-initiated responses
are no longer accepted. OIDC requires a signed ID token and matching nonce;
discovery exceptions do not disable token signature, issuer, or audience checks.

## Repairing links

!!! warning "Preserve the existing user"

    Never delete/recreate a user to repair authentication. Sign in as a separate
    administrator, export the provider with `GET /sso/OID/Get` or `SAML/Get`, and
    compare its `CanonicalLinks`/`SubjectLinks` GUIDs with the intended existing user.

For a stale or conflicting mapping, verify ownership outside the failed login,
back up the export, remove only the incorrect mapping, and replace that provider
through its `Add` API while preserving every other field and link. Then have the
intended user sign into Jellyfin and link explicitly. An administrator can also
correct a GUID in the exported mapping after independently establishing ownership.
For repairs, verify ownership independently of matching usernames.

## Verify and roll back

Test allowed and denied users, both callback aliases in use, permissions, linking,
unlinking, fallback login, and a server restart. Compare existing user IDs and data.
`EnableAuthorization=false` now preserves Live TV permissions as well as the
other managed fields; admission roles still apply.

To roll back, stop Jellyfin and restore the matching server data, configuration,
and plugin backup. Restore a compatible server/plugin pair together; copying an
old plugin DLL into an upgraded server is not a supported database rollback.
