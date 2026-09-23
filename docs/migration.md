# Upgrading from 4.x

Upgrading from the upstream 4.x plugin is automatic. Your providers, secrets,
callback URLs, account links, and permission settings carry over, and every
user signs in to the same Jellyfin account with the same permissions as before.
OpenID Connect providers need no changes. SAML providers need
[one setting](#saml-providers-set-the-issuer) that earlier releases did not have.

## Upgrade

1. **Back up** Jellyfin's data and configuration directories, as you would before
   any server upgrade. Keep a local administrator account that can sign in
   without SSO.
2. **Upgrade Jellyfin to 12.0 or later.** The 5.x plugin requires it, and the
   4.x plugin does not run on Jellyfin 12.
3. **Add this fork's repository** under Dashboard → Plugins → Repositories:

   ```text
   https://raw.githubusercontent.com/NekoShinobi/jellyfin-plugin-sso/main/manifest.json
   ```

   Then remove the archived upstream repository, so Jellyfin only sees one
   source for this plugin:

   ```text
   https://raw.githubusercontent.com/9p4/jellyfin-plugin-sso/manifest-release/manifest.json
   ```

4. **Install SSO Authentication** from the catalog and restart Jellyfin.

That is the whole upgrade. The first start converts the configuration; see
[what happens automatically](#what-happens-automatically).

## What happens automatically

- **Backup.** The original configuration is saved next to it as
  `SSO-Auth.xml.pre-v1.bak` before anything is changed. Later starts leave the
  backup alone.
- **Settings.** Provider names, endpoints, client IDs, secrets, role claims, and
  admission roles are kept as they are. Existing callback URLs, including the
  older aliases, keep working.
- **Accounts.** Saved username links continue to select the same Jellyfin user,
  even if the local account was renamed, so nobody needs to relink. At each
  user's next sign-in the plugin also records a stable link to the provider
  identity, which survives later username changes.
- **Permissions.** Administrator roles, library-role mappings, and Live TV
  settings become equivalent **Everyone** defaults and group rules. Each user
  gets the same administrator status, libraries, and Live TV access as before.
  See [how earlier settings are converted](permissions.md#settings-from-earlier-releases).
- **Authentication provider IDs.** Users created by 4.x referenced a login
  provider that no longer exists; they are switched to the SSO provider.
  Local and LDAP password logins are left unchanged.

In the dashboard, the former **Access** tab is now two tabs: **Sign-in** (who can
sign in) and **Permissions** (what they can do). One behavior changes: with
**Manage Jellyfin permissions** off, Live TV permissions are now left alone as
well, instead of being reset at every sign-in.

## SAML providers: set the issuer

For security, SAML sign-in now checks which identity provider issued each
response and requires a stable user identifier. Until a SAML provider has these
settings, its sign-in stops with "Configure the SAML HTTPS endpoint, issuer,
client ID, and stable NameID format". Nothing else is lost in the meantime.

| Setting            | Value                                                                                                           |
| ------------------ | --------------------------------------------------------------------------------------------------------------- |
| `SamlIssuer`       | Your identity provider's entity ID, exactly as it appears in its metadata.                                      |
| `SamlNameIdFormat` | Leave the default (`persistent`) unless your provider sends another stable format. `transient` is not accepted. |
| `SamlEndpoint`     | Must use `https://`.                                                                                            |

SAML settings are edited through the provider API. Create an API key under
Dashboard → API Keys, then update the provider while keeping every other field
and its account links:

```sh
curl -s -H "Authorization: MediaBrowser Token=\"$JELLYFIN_TOKEN\"" \
  https://jellyfin.example.com/sso/SAML/Get \
  | jq '.["PROVIDER_NAME"] | .SamlIssuer = "https://idp.example.com/entity-id"' \
  > provider.json
curl --fail-with-body -H "Authorization: MediaBrowser Token=\"$JELLYFIN_TOKEN\"" \
  -H 'Content-Type: application/json' --data-binary @provider.json \
  https://jellyfin.example.com/sso/SAML/Add/PROVIDER_NAME
```

Unsolicited sign-ins started from the identity provider's portal are no longer
accepted; users start sign-in from Jellyfin. See [SAML settings](configuration.md#saml-settings).

## Verify

After the first restart:

1. Check that **SSO-Auth** 5.x shows as Active under Dashboard → Plugins.
2. Sign in through SSO as a regular user and as an administrator, and confirm
   they land in their existing accounts with their usual libraries.
3. Optionally, open Dashboard → SSO → **Permissions** and preview a user to see
   the converted rules and where each permission comes from.

## If something goes wrong

**The plugin does not load, or the log shows `AmbiguousMatchException`.** More than
one SSO plugin version is installed. Stop Jellyfin, remove every older `SSO-Auth_*`
folder from the plugin directory (check mounted volumes for containers), keep the
newest one, and start Jellyfin again.

**Sign-in fails with a provider error.** Check the message on the sign-in page
and the Jellyfin log. SAML providers need the [issuer](#saml-providers-set-the-issuer).
For URL or proxy problems, see [proxy troubleshooting](troubleshooting.md#public-urls-and-proxies).
The upgraded plugin validates OIDC tokens more strictly (signed ID token, nonce,
issuer, and audience); standard providers meet these requirements.

**The plugin reports an unreadable configuration.** It stops instead of replacing
your settings with defaults. Restore `SSO-Auth.xml` from `SSO-Auth.xml.pre-v1.bak`
or your backup and restart.

### Repairing links

A user who reaches the wrong account, or whose linked account was deleted, needs
a link repair. Never delete and recreate the user; that loses their watch
history and settings.

1. As an administrator, export the provider with `GET /sso/OID/Get` or
   `/sso/SAML/Get` and keep a copy.
2. Compare the user IDs in `CanonicalLinks` and `SubjectLinks` with the account
   the person should use, and confirm ownership independently of the username.
3. Remove only the incorrect entry and save the provider through its `Add` API,
   keeping every other field and link.
4. Have the user sign in to their Jellyfin account and link the provider from
   **Account connections**.

See [account matching](accounts-and-clients.md#automatic-account-matching) for
how new sign-ins choose an account.

### Rolling back

Stop Jellyfin and restore the backup you took before upgrading: server data,
configuration, and the 4.x plugin together. Jellyfin 12 upgrades its own database,
and the 4.x plugin does not run on Jellyfin 12, so they go back as a set.
