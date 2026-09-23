# Configuration

The dashboard edits OIDC settings stored in Jellyfin's plugin configuration.
SAML configuration currently requires the administrative API.

!!! note "Configuration formats"

    Provider recipes show field values; their YAML examples are **not an import
    format**. The administrative API accepts JSON. Its `Add` operation replaces the
    provider configuration, so preserve account links when editing through the API.

Provider names are case-sensitive configuration keys: `authentik` and `Authentik`
are separate entries. Use the same spelling in every login/callback URL.

## Provider dashboard

The provider list shows each OIDC provider's issuer and enabled status. Choose
**Edit** to open its settings or **Add provider** to create another. If only one
OIDC provider is configured, its settings open automatically. Existing provider
names are read-only because they are part of sign-in URLs and account links.

The editor groups settings into **Connection**, **Sign-in**, **Permissions**,
**Profile & accounts**, and **Advanced**. Connection includes a callback URL preview; register the exact
public URL with your identity provider. Enter roles and scopes one per line.
**Save changes** applies settings to new sign-ins without restarting Jellyfin.
A failed save leaves your draft available to retry.

**Delete** asks for confirmation and removes that provider's settings and links.
It does not delete Jellyfin accounts or watch history. Turn off **Allow sign-in**
instead if you only want to pause the provider. **Account connections** opens the
self-service page for the currently signed-in Jellyfin user.

## Permissions

| Setting                               | Current behavior                                                                          |
| ------------------------------------- | ----------------------------------------------------------------------------------------- |
| `Roles`                               | Admission filter: at least one listed role must match. An empty list disables this check. |
| `EnableAuthorization`                 | Applies the permission settings below at each sign-in.                                    |
| `EnableAllFolders` / `EnabledFolders` | Library access for everyone: all libraries, or only the listed library IDs.               |
| `PermissionDefaults`                  | Permission values for everyone. See [permission rules](permissions.md).                   |
| `GroupPermissions`                    | Permissions and libraries that a matching group adds.                                     |
| `UserPermissions`                     | Per-user overrides, or an exemption from synchronization.                                 |

!!! warning "SSO can replace existing permissions"

    Admission is separate from permission synchronization. Include administrator
    users in an admitted role as well. Enabling synchronization overwrites every
    permission it manages, including manually assigned administrator status and
    library access.

With `EnableAuthorization` disabled, all existing permissions remain
Jellyfin-managed. New users start with Jellyfin's normal defaults. With
synchronization enabled, library access and every managed permission are updated
on each login; removing a matching group revokes what it granted. Unmanaged
permissions remain unchanged. Admission is checked for both protocols
immediately before issuing credentials, even when synchronization is disabled.

### Entering roles and libraries

In dashboard list fields, enter **one value per line**, without commas, quotes,
or JSON brackets. For example, the admitted roles field can contain:

```text
jellyfin_users
jellyfin_admins
```

In the JSON API, the same setting is `"roles": ["jellyfin_users", "jellyfin_admins"]`.
Role and group names must match the claim values exactly, including case. A group
rule does not bypass `Roles`; include administrators in the admission list too.

Use the dashboard library picker for library selections. The stored values are
Jellyfin library IDs, not display names or filesystem paths. API callers can
obtain IDs from authenticated `GET /Library/MediaFolders` under their Jellyfin base
URL. With synchronization on, an empty library list does not mean “preserve
existing access”; disable `EnableAuthorization` to leave libraries under
Jellyfin's control.

## Group and user permissions

The **Permissions** tab sets what everyone gets, what matching groups add, and
per-user overrides, with a complete preview of the values SSO would apply. See
[permission rules and previews](permissions.md) for precedence, revocation,
library selection, and how settings from earlier releases are converted.

## OIDC settings

| Field                        | Type         | Meaning                                                                                |
| ---------------------------- | ------------ | -------------------------------------------------------------------------------------- |
| `OidEndpoint`                | string       | Provider authority used for discovery; follow the provider's issuer URL.               |
| `OidClientId`, `OidSecret`   | string       | Application ID and client secret.                                                      |
| `OidScopes`                  | string array | Additional scopes; `openid profile` are already requested.                             |
| `RoleClaim`                  | string       | Role claim path, such as `groups` or `realm_access.roles`.                             |
| `DefaultUsernameClaim`       | string       | Username claim; falls back to `preferred_username` when unset.                         |
| `AvatarUrlFormat`            | string       | Optional avatar URL, with substitutions such as `@{picture}`.                          |
| `DisablePushedAuthorization` | boolean      | Disables PAR where provider compatibility requires it.                                 |
| `DoNotLoadProfile`           | boolean      | Skips fetching the UserInfo/profile response.                                          |
| `DisableHttps`               | boolean      | Allows HTTP discovery; leave false for deployed providers.                             |
| `DoNotValidateEndpoints`     | boolean      | Relaxes discovery endpoint validation.                                                 |
| `DoNotValidateIssuerName`    | boolean      | Relaxes discovery authority/issuer matching; token issuer validation remains required. |

Role claims must resolve to the expected list of strings. In a nested path,
use `\.` for a literal dot in a claim name. Inspect redacted claim **names and
shapes**, not full tokens, when diagnosing a mapping.

For example, `{"groups": ["jellyfin_users", "jellyfin_admins"]}` uses
`RoleClaim=groups`, while `{"realm_access": {"roles": ["jellyfin_users"]}}`
uses `RoleClaim=realm_access.roles`. A comma-separated string is one role,
not two. Request the provider's scope that supplies these claims. Claims must
be present in the validated ID token or UserInfo response; an access-token-only
mapper is insufficient. With `DoNotLoadProfile=true`, required claims must be
in the ID token. Request `email` in `OidScopes` if using an email username claim
that requires that scope; a missing username claim falls back to `sub`.

The ID token must be a signed JWT using RS256/384/512, PS256/384/512, or
ES256/384/512. Encrypted ID tokens (JWE) are unsupported: the plugin has no
decryption-key setting. Configure signing rather than token encryption for this
client; disabling discovery checks does not enable encrypted or unsigned tokens.

Leave discovery checks enabled unless you have verified a provider-specific
requirement. Disabling all validation is not a general solution to TLS or proxy
errors. Avatar retrieval is performed by the server; leave it unset until the
provider-controlled image source and its behavior have been validated.

## SAML settings

| Field             | Type   | Meaning                                                |
| ----------------- | ------ | ------------------------------------------------------ |
| `SamlEndpoint`    | string | Identity provider's SAML login endpoint.               |
| `SamlClientId`    | string | Service provider entity/client ID.                     |
| `SamlCertificate` | string | Base64 certificate used to validate the IdP signature. |

Also configure `SamlIssuer` to the exact IdP entity ID and `SamlNameIdFormat` to
a stable format (default: `urn:oasis:names:tc:SAML:2.0:nameid-format:persistent`).
Transient NameIDs are rejected. Existing configurations need these settings
reviewed before SAML sign-in can resume.

Register the ACS at `/sso/SAML/post/PROVIDER_NAME`, not `/sso/SAML/start/`.
A signed response or signed assertion is required, using the pinned certificate.
Issuer, audience, recipient, destination, request correlation, bearer confirmation,
and finite validity are checked. Replay is rejected. Assertion lifetimes over one
hour are rejected; allow one minute of clock skew for not-before checks. Use
SHA-256 or stronger signatures. Unsolicited IdP-initiated responses are rejected:
start login from Jellyfin so the response has a browser-bound request.

## Shared settings and stored state

| Field                     | Type            | Meaning                                                                                                   |
| ------------------------- | --------------- | --------------------------------------------------------------------------------------------------------- |
| `Enabled`                 | boolean         | Allows sign-in through this provider.                                                                     |
| `EnabledFolders`, `Roles` | string arrays   | Library IDs for everyone, admitted roles.                                                                 |
| `DefaultProvider`         | string          | Jellyfin authentication provider assigned after SSO, for fallback login.                                  |
| `SchemeOverride`          | string          | Scheme only, such as `https`; never a full URL or `https://`. Leave empty to use the host request scheme. |
| `PortOverride`            | integer or null | Overrides generated URL port.                                                                             |
| `NewPath`                 | boolean         | Legacy field retained; the start route now selects its matching callback.                                 |
| `CanonicalLinks`          | dictionary      | Saved provider username → Jellyfin GUID mappings, honored when no stable identity link exists.            |

`SubjectLinks` stores hashed stable identity keys mapped to Jellyfin GUIDs. Preserve
both link dictionaries when replacing a provider through the API. Dashboard
settings saves preserve the latest server-side links automatically.

Missing/null legacy collections normalize to empty collections during migration.
Booleans default to false; API callers should provide all intended policy values.
Role normalization accepts repeated string claims, JSON string arrays, and nested
objects. Malformed role values reject login instead of retaining old privileges.
A missing configured username claim falls back to the stable subject for new users.

Avatar downloads are optional HTTPS requests with a five-second deadline, a 2 MiB
body limit, and a 4096 × 4096 image limit. Only public addresses and PNG/JPEG/WebP
images are accepted; redirects, proxies, and private/loopback addresses are rejected.
Image failures do not prevent login.
