# Jellyfin SSO

Jellyfin SSO connects Jellyfin accounts to an OpenID Connect (OIDC) or SAML
identity provider. It includes provider configuration, account linking, and
optional role-based permissions.

!!! info "Documentation version"

    This documentation follows the development branch. The build targets Jellyfin
    12.0 and .NET 10, with package and browser tests on 12.0 and 12.1.
    Provider-shaped protocol fixtures cover the documented integrations; inherited
    provider UI instructions still need verification against your provider version.
    Consult the documentation at a release's tag when using that release.

## Start here

1. Check [installation and compatibility](installation.md).
2. Follow [initial setup](getting-started.md) and choose a
   [provider recipe](providers/index.md).
3. Review [permissions](configuration.md#permissions) before signing in.
4. Read [migration](migration.md) before replacing an existing installation.

For a failed login, start with [troubleshooting](troubleshooting.md).
[Account linking and client support](accounts-and-clients.md) explain the
browser flow and alternatives for other clients.

Maintainers can find build commands in [development](development.md) and the
publication process in [releases](releases.md).
