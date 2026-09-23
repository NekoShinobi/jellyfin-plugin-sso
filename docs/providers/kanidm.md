# Kanidm

!!! note "Recipe validation status"

    This recipe is inherited from upstream and has **not been revalidated on
    Jellyfin 12**. Provider UI labels may differ by version. Use the
    [provider's documentation](https://kanidm.github.io/kanidm/stable/integrations/oauth2.html) for current administration steps.

Follow [initial setup](../getting-started.md) and review
[permissions](../configuration.md#permissions) first. YAML blocks below describe
plugin field values; the plugin does not import YAML. Include explicit shared
policy fields when using the JSON API.

Kanidm is a modern and simple identity management platform written in rust.

### Kanidm Config

```shell
kanidm system oauth2 create jellyfin "Jellyfin" https://jellyfin.example.com/

# Set this to drop the trailing @idm.example.com in usernames
kanidm system oauth2 prefer-short-username jellyfin

kanidm system oauth2 add-redirect-url jellyfin https://jellyfin.example.com/sso/OID/redirect/kanidm
kanidm system oauth2 add-redirect-url jellyfin https://jellyfin.example.com/sso/OID/r/kanidm

# Optionally setup groups for Jellyfin
kanidm group create jellyfin_admins
kanidm group create jellyfin_users

kanidm system oauth2 update-scope-map jellyfin jellyfin_admins openid profile groups
kanidm system oauth2 update-scope-map jellyfin jellyfin_users openid profile groups
```

Get the secret used in the Jellyfin config with `kanidm system oauth2 show-basic-secret jellyfin`.

### Jellyfin's Config

```yaml
kanidm:
  OidEndpoint: https://idm.example.com/oauth2/openid/jellyfin/
  OidClientId: jellyfin
  OidSecret: <kanidm-secret>
  # (optional) If you want Jellyfin to read group permissions from kanidm
  EnableAuthorization: true
  OidScopes:
    - groups
  RoleClaim: groups
  AdminRoles:
    - jellyfin_admins@idm.example.com
  Roles:
    - jellyfin_users@idm.example.com
    # If in your setup admin accounts aren't members of the users group you need to add the admins group to roles as well
    - jellyfin_admins@idm.example.com
  # (optional) If you want the name attribute instead of the spn attribute as username
  DefaultUsernameClaim: preferred_username
```
