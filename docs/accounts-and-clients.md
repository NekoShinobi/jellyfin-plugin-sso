# Accounts and clients

## Automatic account matching

After authenticating with an enabled provider and passing its admission rules,
login resolves the Jellyfin account in this order:

1. The saved stable provider identity in `SubjectLinks`.
2. The provider's saved username mapping in `CanonicalLinks`.
3. An existing Jellyfin account with the provider-supplied username.
4. A new Jellyfin account if none of those match.

Existing username mappings work after an upgrade without relinking. A successful
match saves the stable provider identity against the same Jellyfin user ID, so
later username changes preserve the account. Existing passwords are not reset.
A saved mapping to a deleted user stops login for administrator repair; it never
falls through to a same-name account. See [migration and repair](migration.md#repairing-links).

This behavior trusts the identity provider's username assignments. Anyone allowed
to sign in with a matching provider username can access that Jellyfin account,
including an administrator account. Restrict who can register, rename, or reassign
usernames at the provider, and configure admission roles accordingly. Saved legacy
username mappings remain usable even if the provider assigns that name a new subject.

## Linking existing accounts

For different local and external usernames, sign in to the existing Jellyfin account using its local password or configured
fallback, then open **Account connections** at `/SSOViews/linking` under the same
Jellyfin base URL. Choose **Link account**, authenticate with the provider, and
wait for confirmation.

For each provider, the page shows every identity connected to your account: the
provider username, the identity provider's address, when it was linked, and when
it was last used to sign in, plus the groups the provider sent at your last
sign-in. Older username-based links are labelled as such; signing in with the
provider adds a verified link. Details for links made before this version appear
after the next sign-in.

!!! tip "Open it from your settings"

    With the [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation)
    plugin installed, **Account connections** also appears in each user's
    **Settings** menu in the Jellyfin web client, next to **Profile**. Without it,
    share the `/SSOViews/linking` link with your users. The entry is shown only
    while a provider is enabled.

Local and external usernames may differ. The flow preserves the local session
and does not change permissions or provision a new account.

OIDC links identify an account by the validated issuer and subject. SAML links
use the configured issuer, stable NameID format, qualifiers, and NameID value.
Explicit linking rejects an identity already mapped to another Jellyfin account.
It is optional when automatic account matching already selects the intended user.

Choose **Unlink**, then **Confirm unlink** to remove a connection. Links to disabled
providers remain visible so they can still be removed.

Unlinking removes a mapping; it does not delete the Jellyfin user, revoke existing
sessions, or create a local password. Configure and test fallback access first.
The next SSO login applies the provider's configured permission policy. Unlinking
is not a sign-in ban: a remaining legacy mapping or matching local username can
connect the account again. To deny access, use provider admission rules or disable
the Jellyfin account.

## Permission management

Administrators can assign permissions by group and override individual Jellyfin
users, including exempting a user from SSO permission updates. The
[permission preview](permissions.md#preview-a-group-or-user) compares the current
account with the provider's proposed policy and shows which rule controls each
permission. Updates apply at the next SSO sign-in.

## Password and LDAP fallback

!!! note "SSO does not create a usable local password"

    New SSO users receive an unknown random password and the registered
    **SSO (browser sign-in)** authentication provider, which rejects password login
    and password changes. The IdP password is not copied into Jellyfin.

1. Keep a separate local administrator signed in while configuring recovery.
2. Choose a password-capable authentication provider registered in this Jellyfin
   installation. Use its actual provider ID, not a display name or the old
   `SSOController` class name. For LDAP, install/configure that provider and
   establish its account mapping first.
3. Set `DefaultProvider` in the SSO provider if all its users should receive that
   fallback on subsequent SSO logins. To change one existing user immediately,
   an administrator can use [the `Unregister` API](reference/api.md#administration).
   Despite its name, this operation only changes the authentication provider.
4. For local authentication, set a nonempty password through Jellyfin after
   assigning the password-capable provider. For LDAP, credentials are managed
   by the directory and its plugin; SSO does not create or reset them.
5. Test password/LDAP login in a separate browser session, repeat after an SSO
   login and a server restart, then test the intended native client. Keep the
   recovery administrator available until all paths work.

A configured `DefaultProvider` is reapplied at SSO login, so align it with any
per-user change. Leaving it empty preserves a valid existing provider but does
not turn new SSO users into password users. Unlinking or changing the provider
does not reset credentials, delete watch history, or revoke existing sessions.

## Clients

The integration completes authentication through Jellyfin Web. The browser
adapter preserves other saved servers, supports a Jellyfin base path, and reports
storage and authentication failures directly. It does not embed Jellyfin in an iframe.

!!! tip "Signing in on a native client"

    Native applications do not receive this browser session automatically. For clients
    that support it, use Jellyfin's [Quick Connect](https://jellyfin.org/docs/general/server/quick-connect/)
    after signing in through the browser. Other clients need a separately configured
    Jellyfin password or authentication provider. `DefaultProvider` selects a
    registered provider; it does not set a password or guarantee fallback access.

Do not add a native application's custom callback URI to an IdP client. This
plugin's callback is on the Jellyfin server. Native device flows are outside the
current supported integration.

For Kodi and TV clients, check whether the specific client/version offers Quick
Connect. If it does, obtain its code and approve it from an authenticated Jellyfin
Web session. Otherwise use the tested password/LDAP fallback above. Browser
branding buttons do not add SSO support to a native client.

Jellyfin enforces account schedules, disabled status, remote access, and session
limits. Its device allowlist applies to clients reporting persistent device IDs;
Jellyfin exempts other clients and administrators. The plugin retains those host
semantics.

## Logout

Signing out of Jellyfin ends that Jellyfin session. Provider-wide single logout
is not implemented, so a subsequent SSO attempt may reuse the provider's session.
