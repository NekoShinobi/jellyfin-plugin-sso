# Authelia

!!! note "Recipe validation status"

    This recipe is inherited from upstream and has **not been revalidated on
    Jellyfin 12**. Provider UI labels may differ by version. Use the
    [provider's documentation](https://www.authelia.com/integration/openid-connect/clients/jellyfin/) for current administration steps.

Follow [initial setup](../getting-started.md) and review
[permissions](../configuration.md#permissions) first. YAML blocks below describe
plugin field values; the plugin does not import YAML. Include explicit shared
policy fields when using the JSON API.

Authelia is simple to configure, and RBAC is straightforward.

### Authelia's Config

Below is the `identity_providers` section of an Authelia config:

### Authelia v4.38 and above

```yaml
identity_providers:
  oidc:
    # hmac secret and private key given by env variables
    clients:
      - client_id: jellyfin
        client_name: My media server
        # Client secret should be randomly generated
        client_secret: <redacted>
        token_endpoint_auth_method: client_secret_post
        authorization_policy: one_factor
        redirect_uris:
          - https://jellyfin.example.com/sso/OID/redirect/authelia
```

### Jellyfin's Config

On Jellyfin's end, we need to configure an Authelia provider as follows:

In order to test group membership, we need to request Authelia's `groups` OIDC scope, which we will use to check user roles.

```yaml
authelia:
  OidEndpoint: https://authelia.example.com
  OidClientId: jellyfin
  OidSecret: <redacted>
  RoleClaim: groups
  OidScopes: ["groups"]
  DisablePushedAuthorization: true
```

The excerpt omits signing-key/JWKS setup. Configure that using
[Authelia's maintained Jellyfin integration guide](https://www.authelia.com/integration/openid-connect/clients/jellyfin/)
for the deployed Authelia version. The discovered `jwks_uri` must be reachable
from Jellyfin's runtime. `DisablePushedAuthorization` addresses PAR compatibility;
it does not repair signing keys, TLS, issuer, or callback mismatches.
