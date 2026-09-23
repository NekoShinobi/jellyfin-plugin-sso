# Security reports

Report vulnerabilities privately to the maintainers of this fork using GitHub's
**Security → Advisories → [Report a vulnerability](https://github.com/NekoShinobi/jellyfin-plugin-sso/security/advisories/new)** feature when enabled. If that
option is unavailable, ask the fork maintainer for a private reporting channel
without posting vulnerability details publicly. Do not send fork-specific
reports to the archived upstream maintainer by default.

Include the affected commit/version, reproduction steps, impact, and any
proposed fix. Verify automated findings and remove real credentials, tokens,
and personal data from examples. Coordinate disclosure with the maintainers.

This fork targets Jellyfin 12.0+ on .NET 10. The 5.x authentication rewrite is
implemented; current account-matching and upgrade behavior is documented in the
[account guide](docs/accounts-and-clients.md) and [migration guide](docs/migration.md).
Report issues against the plugin and server versions you tested. Historical audit
results and passing automation do not establish that every deployment or finding
is covered.
