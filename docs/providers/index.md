# Identity providers

!!! note "Recipe validation status"

    These recipes preserve useful upstream setup information. None is yet a
    Jellyfin 12 certification; record the plugin/server/provider/client versions
    and test date when validating a recipe.

| Provider                  | Protocol    | Role mapping                                             |
| ------------------------- | ----------- | -------------------------------------------------------- |
| [Authelia](authelia.md)   | OIDC        | `groups`, with the groups scope                          |
| [authentik](authentik.md) | OIDC        | `groups`, with a scope/property mapping                  |
| [Keycloak](keycloak.md)   | OIDC / SAML | Match the configured claim path or SAML `Role` attribute |
| [Pocket ID](pocket-id.md) | OIDC        | `groups`, if exposed by the provider                     |
| [Kanidm](kanidm.md)       | OIDC        | `groups`, with scope mapping                             |
| [Google](google.md)       | OIDC        | No role recipe maintained here                           |

All providers need a stable plugin provider name, an exact callback URL, and a
separate recovery administrator. Configure admitted groups and permission
synchronization explicitly; copying credentials alone is not a complete policy.

For a new provider, document its issuer, callbacks, scopes, role claim shape,
login/denial behavior, and tested versions. Use a non-administrator test account.

## Other OIDC providers

GitLab, ZITADEL, and tsidp have appeared in upstream setup requests. They have no
validated Jellyfin 12 recipe in this repository; the links below are starting
points, not a compatibility claim.

| Provider | Starting point                                                                                         | Plugin configuration to verify                                                                                         |
| -------- | ------------------------------------------------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------------- |
| GitLab   | [GitLab's OIDC provider documentation](https://docs.gitlab.com/integration/openid_connect_provider/)   | Use the GitLab instance authority and enable `openid` for the application; confirm the requested profile/group claims. |
| ZITADEL  | [ZITADEL's authorization-code guide](https://zitadel.com/docs/guides/integrate/login/oidc/login-users) | Register a server-side web application and the exact Jellyfin callback, including case, port, and base path.           |
| tsidp    | [The tsidp project](https://github.com/tailscale/tsidp)                                                | Verify the deployed issuer, client registration, and reachability from both Jellyfin's runtime and the browser.        |

Use [the generic setup flow](../getting-started.md), map the client credentials
to `OidClientId`/`OidSecret`, and configure only scopes and role claims actually
provided by that deployment. Keep discovery validation enabled. Do not assume
an unlisted provider emits `groups` or that leaving `Roles` empty restricts
admission. Before contributing a recipe, record exact versions and test allowed
and denied users, linking an existing account, and a restart. GitLab and tsidp
recipes remain pending that evidence.
