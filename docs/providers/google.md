# Google OIDC

!!! note "Recipe validation status"

    Upstream reported Google sign-in working with limitations, including numeric
    usernames and an endpoint-validation exception. This has not been revalidated
    on Jellyfin 12, and no group/administrator mapping recipe is maintained here.

Use Google's [OpenID Connect documentation](https://developers.google.com/identity/openid-connect)
to register a confidential web client and the plugin's exact
`/sso/OID/redirect/PROVIDER_NAME` callback. Do not assume a Google account's email
or display name safely proves ownership of an existing local Jellyfin user.

Keep issuer and HTTPS validation enabled. If discovery uses multiple endpoint
hosts, inspect the provider metadata before considering an endpoint validation
exception. Review [OIDC settings](../configuration.md#oidc-settings) and
[account linking limitations](../accounts-and-clients.md) before testing.
