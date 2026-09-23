# API and callback reference

These routes describe the current controller, including legacy aliases retained
for migration. Prefix each route with the public Jellyfin base path. `PROVIDER`
is the configured plugin provider name, not the IdP's client ID.

## Browser flow

| Method     | Route below `/sso/`                       | Purpose                       |
| ---------- | ----------------------------------------- | ----------------------------- |
| GET        | `OID/start/PROVIDER`                      | Start OIDC login              |
| GET        | `OID/redirect/PROVIDER`                   | OIDC callback                 |
| GET        | `OID/p/PROVIDER`                          | Legacy OIDC start             |
| GET        | `OID/r/PROVIDER`                          | Legacy OIDC callback          |
| GET        | `SAML/start/PROVIDER`                     | Start SAML login              |
| POST       | `SAML/post/PROVIDER`                      | SAML assertion consumer (ACS) |
| GET / POST | `SAML/p/PROVIDER`                         | Legacy SAML start / ACS       |
| POST       | `OID/Auth/PROVIDER`, `SAML/Auth/PROVIDER` | Internal browser completion   |
| GET        | `OID/GetNames`, `SAML/GetNames`           | Available provider names      |

!!! note "Pair the start route with its callback"

    `POST /sso/SAML/start/PROVIDER` is **not** the configured ACS route. Older docs
    incorrectly used that path. For OIDC, pair `/start/` with `/redirect/`, or the
    legacy `/p/` with `/r/`. Do not mix the two families during a login.

The internal completion body currently contains `deviceId`, `deviceName`,
`appName`, `appVersion`, and `data`. `data` is a one-use opaque completion code valid for two minutes. The matching
HttpOnly browser cookie is required; raw SAML or unverified tokens are never
accepted by this endpoint. Login requests expire after ten minutes.

## Administration

The following routes require Jellyfin's elevation policy. Use an authorized
administrative token in the authorization header, not in a URL query string.

| Method       | Route below `/sso/`                     | Behavior                                            |
| ------------ | --------------------------------------- | --------------------------------------------------- |
| POST         | `OID/Add/PROVIDER`, `SAML/Add/PROVIDER` | Replace that provider's configuration               |
| GET          | `OID/Get`, `SAML/Get`                   | Return configurations, including sensitive values   |
| DELETE / GET | `OID/Del/PROVIDER`, `SAML/Del/PROVIDER` | Delete the provider configuration                   |
| GET          | `OID/States`                            | Redacted active transaction count only              |
| POST         | `Unregister/USERNAME`                   | Assign a different Jellyfin authentication provider |

Use DELETE for `Del`; the legacy GET alias still mutates configuration.
`Unregister` validates the replacement authentication provider and persists it.
Its JSON request body is the registered provider ID as a string. It does not
set a password or remove links. Keep a working local administrator account.

Example for a **new** OIDC provider: save the following as `provider.json`,
replace placeholders, and protect it as a secret.

```json
{
  "oidEndpoint": "https://identity.example.com/realms/media",
  "oidClientId": "jellyfin",
  "oidSecret": "REPLACE_WITH_CLIENT_SECRET",
  "enabled": true,
  "enableAuthorization": true,
  "enableAllFolders": false,
  "enabledFolders": [],
  "roles": ["jellyfin_users"],
  "permissionDefaults": {
    "IsAdministrator": false,
    "EnableLiveTvAccess": false,
    "EnableLiveTvManagement": false
  },
  "groupPermissions": [],
  "roleClaim": "groups",
  "oidScopes": ["groups"]
}
```

```sh
curl --fail-with-body \
  -H "Authorization: MediaBrowser Token=\"$JELLYFIN_TOKEN\"" \
  -H 'Content-Type: application/json' \
  --data-binary @provider.json \
  'https://jellyfin.example.com/sso/OID/Add/example'
```

!!! warning "The Add API replaces the entire provider"

    This example intentionally grants no libraries or administrator role. Add your
    intended library policy and [group rules](../permissions.md) before using the
    account. Payloads from earlier releases, with `adminRoles` and the Live TV and
    folder-role fields, are still accepted and converted. `Add` replaces the whole
    configuration: do not apply this minimal payload to an existing provider with
    stored `CanonicalLinks` or `SubjectLinks`. Prefer the dashboard when maintaining existing data.

For a new SAML provider, use the same explicit shared policy fields, replace
`oidEndpoint`, `oidClientId`, and `oidSecret` with `samlEndpoint`, `samlClientId`,
`samlCertificate`, `samlIssuer`, and `samlNameIdFormat`, and omit `roleClaim` and `oidScopes`. Send the JSON to
`/sso/SAML/Add/PROVIDER_NAME`. Roles come from the SAML `Role` attribute.
The certificate value is the base64 content from the IdP certificate/metadata.
See the [Keycloak SAML recipe](../providers/keycloak.md#keycloak-saml) and the
[SAML requirements](../configuration.md#saml-settings) before testing.

## Account links

Begin linking with authenticated `POST /sso/{mode}/start/{provider}`. This returns
`Url` and binds the browser transaction to the current Jellyfin user. Legacy
`GET ?isLinking=true` bookmarks redirect to the self-service page.

Link operations require the initiating user's active local session and permission
to change their own preferences. An administrator cannot complete another user's
linking proof. Login and linking codes cannot be interchanged.

| Method | Route below `/sso/`                                       |
| ------ | --------------------------------------------------------- |
| POST   | `{mode}/Link/{provider}/{jellyfinUserId}`                 |
| DELETE | `{mode}/Link/{provider}/{jellyfinUserId}/{canonicalName}` |
| GET    | `oid/links/{jellyfinUserId}`                              |
| GET    | `saml/links/{jellyfinUserId}`                             |

`mode` is `OID` or `SAML`. URL-encode each path segment. See
[account linking](../accounts-and-clients.md) for the supported flow.
