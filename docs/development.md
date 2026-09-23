# Development

## Prerequisites

- .NET 10 SDK; `global.json` permits the latest installed stable 10.0 feature band.
- Python 3.12 or later for release tooling; the docs workflow uses Python 3.13
  from `.python-version`.
- [uv](https://docs.astral.sh/uv/getting-started/installation/) for documentation
  dependencies and commands. uv can install the configured Python version.
- Node.js for JavaScript syntax checks and Prettier.

The plugin targets Jellyfin 12.0. `Directory.Build.props` is the source of the
plugin version and Jellyfin dependency version. The assembly and namespace stay
`SSO-Auth` and `Jellyfin.Plugin.SSO_Auth` for compatibility with existing settings
and embedded resources.

## Editor setup

VS Code, Visual Studio, Rider, and terminal editors are optional choices. Open
`SSO-Auth.sln` and use the build/check commands below as the shared workflow.
Local editor launch/tasks settings may wrap those commands; no separate workflow
repository or particular IDE is required.

## Build and check

```sh
dotnet build SSO-Auth.sln -c Release --warnaserror
dotnet test SSO-Auth.sln -c Release --no-build
python3 -m unittest discover -s tests -v
node --check SSO-Auth/Config/config.js
node --check SSO-Auth/Config/linking.js
node --check SSO-Auth/Views/web.js
node --check SSO-Auth/Views/complete.js
node --test tests/*.test.mjs
```

The Python tests exercise packaging, dependency inclusion, checksums, and catalog
updates. They do not establish runtime authentication correctness. The prior
audit probes under `audits/` reproduce inherited defects; they are not a passing
login test suite and are not published as documentation.

## Build a local package

From the repository root:

```sh
version="$(python3 scripts/release.py version)"
dotnet build SSO-Auth.sln -c Release --warnaserror -p:Version="$version"
printf '%s\n' 'Local development build' > /tmp/sso-release-notes.md
python3 scripts/release.py package --version "$version" \
  --repository NekoShinobi/jellyfin-plugin-sso --notes-file /tmp/sso-release-notes.md
```

When building another fork, replace the repository argument with its GitHub owner/name. `dist/` contains the ZIP,
MD5, and SHA-256 files. The allowlist in `scripts/release.py` includes the plugin, Duende, ITfoxtec,
Microsoft identity-model, and required support assemblies. Jellyfin and core
.NET framework assemblies are supplied by the host.
Install into an isolated Jellyfin instance using the [manual procedure](installation.md#manual-installation).

## Documentation

```sh
uv sync --locked
uv run --locked zensical build --config-file zensical.yml --clean --strict
uv run --locked python scripts/check_docs.py
uv run --locked zensical serve --config-file zensical.yml
```

Open the local URL printed by Zensical. Configuration lives in `zensical.yml`;
pass it explicitly with `--config-file` for both build and preview. YAML retains
the environment-variable defaults used by the Pages workflow. Set `DOCS_SITE_URL` to test a project
subpath; CI obtains the production URL from GitHub Pages. Pass the same URL to
`uv run --locked python scripts/check_docs.py --site-url "$DOCS_SITE_URL"` after a custom-URL build. `DOCS_REPO_URL`
controls the repository link; set `DOCS_EDIT_URI=edit/main/docs/` to enable edit links.
Only the generated `site/` is published. Content and the Jellyfin palette stylesheet
live in `docs/`; `docs-theme/` contains the custom 404 template. The palette uses
[Jellyfin's purple/cyan colors](https://github.com/jellyfin/jellyfin-ux/tree/master/logos/SVG),
with contrasting link shades for light and dark modes. Zensical's cache is ignored
under `.cache/`; use `--clean` for a reproducible full rebuild.

Format edited Markdown, YAML, HTML, CSS, and JavaScript with
`npx --yes prettier@3.6.2 --write <files>`. Do not format vendored minified code.
Documentation dependencies live in the default `docs` dependency group in
`pyproject.toml`; `uv.lock` records the resolved versions and artifact hashes.
`uv sync` creates and manages `.venv`, so no activation or separate pip install is
needed. `--locked` rejects a stale lockfile rather than updating it during a build.

To change the Zensical version, run
`uv add --group docs 'zensical==NEW_VERSION'` with the intended version. To refresh
transitive dependencies within the declared constraints, run `uv lock --upgrade`.
Commit `pyproject.toml` and `uv.lock` together, then rerun the build and link checks
above. The project is tooling-only and does not build or install a Python package.

## Before changing authentication

Add tests for provider and browser binding, one-time completion, denied roles,
existing-user ownership, persistence of links, and preservation of unmanaged
permissions. Exercise migration with existing Jellyfin GUIDs and callback URLs.
Run live flows against the supported web clients and a configured reverse proxy;
a green build alone cannot validate those behaviors.

## Isolated host and browser verification

The .NET tests cover signed OIDC/SAML fixtures, protocol rejection, legacy XML,
concurrency, linking, account restrictions, and managed policy. The browser adapter
also has Node regression tests. Provider-shaped fixtures do not certify a specific
live provider deployment or its current administration UI.

With Docker available, run the produced ZIP through Jellyfin 12.0 and 12.1:

```sh
npm install --prefix /tmp/sso-browser playwright@1.56.0
python3 tests/host_smoke.py --archive dist/SSO-Auth_5.0.0.0.zip --playwright /tmp/sso-browser
```

The harness creates isolated containers and synthetic credentials, tests browser
flows, and removes the containers afterwards. Failure evidence stays under `/tmp`
and can contain synthetic session tokens; do not publish the whole directory.
