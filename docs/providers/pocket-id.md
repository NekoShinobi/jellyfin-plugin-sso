# Pocket ID

!!! note "Recipe validation status"

    This recipe is inherited from upstream and has **not been revalidated on
    Jellyfin 12**. Provider UI labels may differ by version. Use the
    [provider's documentation](https://pocket-id.org/docs) for current administration steps.

Follow [initial setup](../getting-started.md) and review
[permissions](../configuration.md#permissions) first. YAML blocks below describe
plugin field values; the plugin does not import YAML. Include explicit shared
policy fields when using the JSON API.

A simple and easy-to-use OIDC provider that allows users to authenticate with their passkeys to your services.

### Pocket ID Config

1. Login to you Pocket ID admin account
1. Go to `Administration -> OIDC Clients`
1. Click `Add OIDC Client`
1. Give the client a name e.g. `Jellyfin`
1. Set the `Client Launch URL` to your Jellyfin endpoint
1. Set the callback url to `https://jellyfin.example.com/sso/OID/redirect/pocketid`. The `pocketid` part must match the `Name of OpenID Provider` in the Jellyfin SSO provider
1. Keep PKCE enabled where supported and use HTTPS for the public endpoint
1. (optional) Set a logo
1. (optional) Set `Allowed User Groups`

### Jellyfin's Config

```yaml
pocketid:
  OidEndpoint: https://pocketid.example.com
  OidClientId: <pocket-id-client-id>
  OidSecret: <pocket-id-secret>
  EnableAuthorization: true # (optional) If you want Jellyfin to read group permissions from pocket id
  OidScopes: ["groups"] # Request the group claim where supported
  RoleClaim: groups # (optional) If you want Jellyfin to be able to read group assignments from pocket id
  AdminRoles: ["admin"] # (optional) The pocket id group which will give a user Jellyfin admin privileges
  Roles: ["users"] # (optional) The pocket id group which will give a user Jellyfin access
  AvatarUrlFormat: "@{picture}" # (optional) This will pull each users pocket id photo into Jellyfin
```
