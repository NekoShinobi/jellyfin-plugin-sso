# Permission rules and previews

Open **Dashboard → SSO**, edit a provider, then choose **Permissions**. The editor
uses Jellyfin's native controls. Changes are saved with the provider and applied
at the user's next SSO sign-in; previews never update accounts or issue sessions.

## Choose what SSO manages

Turn on **Manage Jellyfin permissions** at the top of **Permissions** to apply
the settings below at each sign-in. When it is off, all existing Jellyfin
permissions stay unchanged. The roles in **Sign-in** still control who may sign in.

Choose what to edit under **Edit permissions for**:

- **Everyone**: library access for every user of the provider, and each
  permission set On or Off, or left as **Keep Jellyfin setting**.
- **Groups**: enter an exact role/group name from the provider's role claim. Tick
  the permissions the group grants, and optionally add libraries or grant all
  libraries. Groups can only add; they never turn a permission off.
- **User overrides**: select an existing Jellyfin user. Set individual
  permissions On or Off and replace their library access, or choose **Keep this
  user's Jellyfin permissions** to exempt the account from synchronization.
  Overrides use the user's ID and survive a local username change.

The list covers Jellyfin's permission flags for administration, visibility,
remote access, devices, playback, transcoding, downloads, deletion, collections,
subtitles, lyrics, Live TV, sharing, and remote control. Library access has its own
selector. **Account disabled** appears in the preview but remains managed by
Jellyfin; SSO cannot re-enable a disabled account.

## How rules combine

The order is Everyone, then matching groups, then the user override:

1. **Everyone** sets the starting value of each managed permission.
2. Each **matching group** turns on the permissions it grants. Adding a group to
   a user never removes anything, and rule order does not matter.
3. A **user override** wins last.

Group names are case-sensitive. Rules never bypass admission checks. To restrict
a permission, leave it Off for everyone and grant it only to the groups that
need it, or turn it off for one user with an override.

When a group or user rule first uses a permission, its Everyone value is set to
Off unless it already has one. That baseline remains after a rule is removed, so
a user who loses a group does not keep its grant. To stop managing the permission
entirely, remove it from every rule, then choose **Keep Jellyfin setting** for
Everyone. Stopping management preserves the value already stored on the account;
it does not reset it.

Each SSO provider applies its own policy at sign-in. Rules do not aggregate across
providers. Permission changes do not proactively update signed-in users.

### Library access

Library access is always managed while synchronization is on. Everyone gets all
libraries or a selection. A matching group can add libraries to that selection
or grant all libraries; selections from Everyone and every matching group are
combined, and all libraries wins over any selection. A user override replaces the
result; selecting no libraries grants none. Unavailable library IDs remain
visible and are preserved when editing.

Jellyfin administrators may have access beyond individual library flags. Device
and channel allowlists, schedules, parental controls, and playback limits remain
configured in Jellyfin. SSO does not bypass Jellyfin's sign-in checks, including
an existing remote-access restriction.

## Settings from earlier releases

Upgrading converts the administrator, library, and Live TV settings of earlier
releases automatically, with the same result for every user. The previous
configuration file is kept as `SSO-Auth.xml.pre-v1.bak` in Jellyfin's plugin
configuration directory.

| Earlier setting                              | Converted to                                                          |
| -------------------------------------------- | --------------------------------------------------------------------- |
| `AdminRoles`                                 | A group per role granting Administrator; Everyone: Off                |
| `EnableLiveTv`, `EnableLiveTvManagement`     | Everyone: On or Off for Live TV playback and recording management     |
| `EnableLiveTvRoles` with its two role lists  | Groups granting Live TV playback or recording management              |
| `EnableFolderRoles` with `FolderRoleMapping` | Groups adding their mapped libraries; Everyone: no libraries selected |
| Mappings while role-based libraries was off  | Removed, as they had no effect                                        |
| Mappings while all libraries was on          | Removed, as they had no effect                                        |

The provider API still accepts these fields, so existing scripts and
configuration-as-code keep working. A provider sent without `PermissionDefaults`
is treated as an earlier-release configuration and converted exactly as above.
When `PermissionDefaults` is present, the fields only add groups and turn Live TV
on for Everyone. The stored configuration never contains them after conversion.

Rolling back to an earlier release requires restoring the backup file. That
release does not read group rules and would remove administrator access at the
next sign-in.

## Preview a group or user

Enter one or more groups under **Groups / roles to test** and choose **Preview
permissions**, or use the preview button in a group/user rule. Select a Jellyfin
user to compare the result with their current settings and apply user overrides.
Leave the user blank for a role-only preview; unmanaged values then show
**Jellyfin default** because there is no existing account to compare.

The preview lists every permission flag, its current value, its value after SSO,
the change, and the winning rule. It also shows the resulting libraries and
whether admission or a Jellyfin account restriction blocks the sign-in. Turning
synchronization off or exempting a user makes the preview retain current values.

For a user, the editor starts with the groups observed at their last successful
SSO sign-in to this provider and displays the timestamp. This is a snapshot, not
a live directory lookup. If no snapshot exists, enter groups manually. You can
edit them to simulate membership changes without changing the provider or user.
Only administrators can access the permission catalogue, previews, or these
configuration snapshots.

## Configuration and API

These optional fields are supported by both OIDC and SAML providers. The dashboard
currently edits OIDC providers; SAML configurations can use the same rules through
the provider API.

| Field                | Contents                                                                                          |
| -------------------- | ------------------------------------------------------------------------------------------------- |
| `PermissionDefaults` | Everyone: dictionary of Jellyfin permission names to booleans. Missing keys remain unmanaged.     |
| `GroupPermissions`   | Rules with `Role`, `Permissions` (every value `true`), `LibraryMode`, and `Folders`.              |
| `UserPermissions`    | Rules with `UserId`, `Permissions`, `LibraryMode`, `Folders`, and optional `PreservePermissions`. |
| `UserRoleSnapshots`  | Server-maintained last successful sign-in groups and observation time, keyed by Jellyfin user ID. |

`LibraryMode` is `Inherit`, `All`, or `Selected`. For a group, `Inherit` adds no
libraries and `Selected` adds `Folders`; for a user, `Selected` replaces the
result. Everyone's libraries use `EnableAllFolders` and `EnabledFolders`.
Duplicate targets, unknown permissions, `false` values in group rules, and
attempts to manage `IsDisabled` are rejected. Use `LibraryMode` rather than
placing `EnableAllFolders` in a permission dictionary.

`GET /sso/Permissions` returns the catalogue. `POST /sso/Permissions/Preview`
accepts a `Configuration` object containing the shared provider fields, an
optional `UserId`, and a `Roles` array. It evaluates this draft without saving.
Both routes require administrator authorization. Preserve server-maintained links
and snapshots when replacing configurations through the provider API; dashboard
saves preserve the latest copies automatically.
