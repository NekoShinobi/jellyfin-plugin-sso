# Reverse proxies and troubleshooting

## Public URLs and proxies

Use one canonical public Jellyfin URL. Preserve the public host and protocol
through the proxy, and configure Jellyfin's trusted/known proxies. Follow the
[Jellyfin reverse proxy guide](https://jellyfin.org/docs/general/post-install/networking/reverse-proxy/).
Include a configured base path in both login and callback URLs.

For example, with Jellyfin at `https://media.example.com/jellyfin`, an OIDC
callback is `https://media.example.com/jellyfin/sso/OID/redirect/authentik`.
A nonstandard public port is also part of the callback URL.

`SchemeOverride` and `PortOverride` affect generated URLs; they do not configure
TLS, repair a certificate chain, or establish trust in forwarded headers.
Register exact callbacks in the IdP. Avoid wildcard redirects.

Compare the callback's case and trailing slash too:
`/sso/OID/redirect/authentik` differs from `/sso/OID/redirect/Authentik` and
`/sso/OID/redirect/authentik/`. Register the callback, then start login from
`/sso/OID/start/authentik`; opening the callback directly has no login transaction.
See the [route table](reference/api.md#browser-flow) for legacy aliases and SAML.

Set `SchemeOverride` to `https`, not `https://` or a complete public URL, only
when an override is needed. Prefer correcting the proxy's host/protocol forwarding
and Jellyfin's Known Proxies configuration. For a `/jellyfin` deployment, configure
Jellyfin's Base URL and the proxy path handling together; stripping the prefix
without a matching host configuration produces incorrect callbacks. After a
restore, recheck these settings as well as DNS, certificates, and the public port.

## Discovery and claims diagnostics

!!! tip "Check connectivity from Jellyfin's runtime"

    Discovery is a server-to-provider request; reaching the IdP from your browser
    does not establish that Jellyfin's container can reach it.

Run the following from the Jellyfin runtime's network environment, substituting
the provider's actual discovery URL (the issuer can include a realm or application
path):

```sh
curl --silent --show-error --fail --max-time 15 --dump-header - \
  'https://identity.example.com/.well-known/openid-configuration'
```

Expect HTTP 200 with a JSON discovery document. A 302 or HTML login page usually
means the wrong endpoint or a proxy/access gateway intercepting discovery; do
not hide that by following redirects during diagnosis. Check DNS, outbound
connectivity, certificate hostname/chain, and the container's trusted CA store.
Do not use `curl -k` as a fix.

Compare the document's `issuer`, `authorization_endpoint`, `token_endpoint`,
`userinfo_endpoint`, and `jwks_uri` with the intended provider deployment.
`OidEndpoint` is its authority, not the browser login page or Jellyfin callback.
An issuer mismatch requires checking the provider's issuer mode and exact URL.
An endpoint-host mismatch is a separate discovery check: this plugin exposes
`DoNotValidateEndpoints` but no per-host endpoint allowlist setting. Only consider
that exception after verifying every advertised endpoint belongs to the provider;
keep issuer, signature, and HTTPS checks enabled.

`DisableHttps` permits HTTP discovery and does not fix certificate trust or
choose the browser callback scheme. `SchemeOverride` controls the callback
scheme and does not relax discovery HTTPS. Neither setting repairs DNS errors.

When discovery succeeds but admission fails, check the selected plugin provider,
requested scopes, ID-token/UserInfo claim shape, and exact role spelling using
the [role examples](configuration.md#entering-roles-and-libraries). Administrators
also need an admitted role. Check whether the linked Jellyfin account is disabled
or restricted by its access schedule or remote-access policy. Share only redacted
claim names/shapes and error categories, never full tokens or assertions.

## Browser assets and branding

If the direct start URL works but the login button is absent, inspect the branding
HTML and the active Modern/Legacy layout. Check browser cosmetic filters and
extensions for hidden elements. Test in a clean browser profile before changing
the identity provider configuration.

For `Failed to import` errors in the dashboard, inspect the browser console's
blocked URL and CSP directive. The host dashboard may load plugin configuration
modules through `blob:` URLs; its module loader and the proxy's CSP must agree.
The standalone SSO pages use same-origin scripts and no hidden iframe. Check that
their scripts load under the configured base path. Avoid copying a blanket CSP
exception from an old report; adjust only the directive required by the verified
host loader and retest the affected page.

## Symptoms

| Symptom                                                | Check                                                                                                                         |
| ------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------- |
| Redirect URI mismatch                                  | Compare actual and registered scheme, host, port, base path, provider name, and `/redirect/` versus `/r/`.                    |
| Provider does not exist                                | Confirm provider name and enabled status, then reopen settings to verify persistence.                                         |
| Login denied despite group membership                  | Check requested scopes, role claim shape/path, and that admin users also match an admission role.                             |
| Administrator or library permissions change            | Review permission synchronization and the group rules; preview the user; avoid the recovery administrator for tests.          |
| Live TV permissions change with authorization disabled | Check the installed version; 5.x preserves these permissions when synchronization is disabled.                                |
| Callback remains on a loading screen                   | Capture redacted browser errors; check storage restrictions, expired browser cookies, CSP, and public base path.              |
| Linking creates a different account                    | Stop and inspect the existing user ID and mapping; do not delete users to retry.                                              |
| Provider B retains Provider A's settings               | 5.x resets false/empty fields on provider changes. Verify the installed version and saved values.                             |
| Discovery or TLS error                                 | Verify the issuer/discovery document, DNS, server trust store and certificate chain before considering validation exceptions. |
| Works until restart                                    | Check persisted provider settings and mappings, exact versions, and startup logs.                                             |

## Reporting a problem

Include server/plugin/provider versions, web or native client, proxy/base-path
setup, reproduction steps, and expected versus actual behavior.

!!! warning "Redact credentials before sharing logs"

    Redact secrets, cookies, tokens, authorization codes, SAML assertions, and
    credentials stored in the browser. The state endpoint exposes only a count.
    Do not enable or share full token logging.

Use the maintained fork's issue tracker. Historical upstream issue references
in provider guides explain prior behavior and are not an active support channel.

## Host logout logs

Jellyfin 12's own `SessionManager` currently writes the access token when logging
out a session. The plugin does not emit tokens or raw assertions. Treat full host
logs as sensitive and redact them before sharing. This host behavior is outside
the plugin's logger; see [the host implementation](https://github.com/jellyfin/jellyfin/blob/v12.0/Emby.Server.Implementations/Session/SessionManager.cs).

## Avatar error mentioning `profilepng`

An error such as `Unable to encode image due to unsupported format: .../profilepng`
can come from an older SSO version that omitted the dot in the filename. The
file may contain a valid PNG; its stored path is the problem.

The plugin repairs these known legacy names on the next successful SSO sign-in,
including when avatar downloading is disabled. It validates a copy, updates
Jellyfin's stored reference, and preserves the original file. A concurrent avatar
change is preserved. The first login may still display the old avatar briefly
before repair completes; reload the page afterwards.

If repair cannot validate the image or access its file, it leaves the reference
unchanged and logs a redacted recovery warning. A successful configured avatar
download can replace it, or re-upload the avatar through Jellyfin's profile-image
editor. That updates both the filename and the stored reference.
