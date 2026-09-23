# Contributing

Use the .NET 10 SDK and follow the [development guide](docs/development.md).
The supported server baseline is Jellyfin 12.0; older server builds are outside
this fork's scope.

Before opening a pull request, build with warnings treated as errors, run the
release-tool tests, and build the documentation with strict validation. The
commands are in the development guide. Include a focused regression test for
changes to authentication, account migration, or permissions.

Bug reports should include plugin and server versions, identity provider and
version, client, public URL/base path, expected behavior, reproduction steps,
and redacted logs. Remove secrets, tokens, authorization codes, and assertions.
Use this fork's issue tracker; the upstream repository is archived.

Documentation lives in `docs/`. Keep provider instructions tied to the versions
actually tested and distinguish inherited examples from verified behavior.
For release changes, see the [release guide](docs/releases.md).

Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md).
Contributions must be compatible with the [project license](LICENSE.txt).

Use clear, descriptive commit messages. Conventional Commit prefixes are
optional; this repository does not configure commitlint or enforce that format.
