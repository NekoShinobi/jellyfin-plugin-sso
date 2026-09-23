# Installation and compatibility

## Supported baseline

| Build                      | Server target  | Runtime | Status                                                   |
| -------------------------- | -------------- | ------- | -------------------------------------------------------- |
| This fork, 5.x             | Jellyfin 12.0+ | .NET 10 | Development; live login and migration validation pending |
| Archived upstream, 4.0.0.4 | Jellyfin 10.11 | .NET 9  | Historical; outside this fork's support scope            |

Jellyfin renamed the planned 10.12 release to 12.0. See the
[12.0 announcement](https://jellyfin.org/posts/jellyfin-release-12.0/).
Targeting 12.0 does not guarantee compatibility with every later server version;
release notes should identify versions actually tested.

## Plugin catalog

The release workflow maintains `manifest.json` on this fork's `main` branch.
After its first successful release, add this URL under Jellyfin's dashboard →
Plugins → Repositories, using this fork's catalog:

```text
https://raw.githubusercontent.com/NekoShinobi/jellyfin-plugin-sso/main/manifest.json
```

Install **SSO Authentication** from the catalog and restart Jellyfin. Confirm
that it loads without errors before configuring a provider.

!!! note "Catalog availability"

    The initial catalog has no versions; it does not point to an untested or
    nonexistent release. The archived upstream's `manifest-release` URL will not
    receive this fork's updates. See [migration](migration.md).

## Manual installation

Download `SSO-Auth_<version>.zip` from this fork's GitHub Releases. Stop Jellyfin,
extract the archive into a dedicated directory under its plugin directory,
and restart. Extract the entire archive, including `SSO-Auth.dll`, its bundled
protocol/support assemblies, and `meta.json`; copying only the plugin DLL omits
required dependencies. The package allowlist is maintained in `scripts/release.py`.
Do not leave another SSO version installed in a second directory; follow
[troubleshooting after an upgrade](migration.md#if-something-goes-wrong).

For a local development package, follow [development](development.md).

## Updates and release channels

This fork has one catalog and no separate nightly channel. Every successful
publishing build on `main` can add a release, including a documentation-only
change. Review [release behavior](releases.md#release-workflow) before subscribing.

Automatic updates and repository selection are controlled by Jellyfin; this
plugin has no per-plugin update lock. For a controlled manual deployment, keep
the chosen ZIP/version and backups, and review the host's update settings and
configured catalogs so they cannot silently replace it. Avoid subscribing to
both archived upstream and this fork for the same plugin GUID. If a release is
missing from the catalog, verify its server target and the configured repository
URL before reinstalling; an empty initial catalog has no package to offer.
