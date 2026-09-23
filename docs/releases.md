# Releases and GitHub Pages

## Release workflow

The release workflow builds, tests, and publishes the plugin:

1. Pull requests and pushes to `main` build the plugin and upload a package
   artifact retained for 30 days.
2. The version comes from `Directory.Build.props`. CI replaces its fourth
   component with `GITHUB_RUN_NUMBER`, for example `5.0.0.42`.
3. A successful `main` push creates a GitHub release and tag at the built commit,
   uploads the package/checksums, and adds it to `manifest.json` on `main`.
4. The bot commits only the updated catalog, with `[skip ci]`. Its normal
   `GITHUB_TOKEN` push does not trigger another release.

Manual dispatch on `main` follows the same flow and requires release notes.
Otherwise the triggering commit message supplies the notes. Pull requests and
manual runs on other branches cannot publish releases.

!!! warning "Main builds publish releases"

    Each main build is a normal release, including documentation-only changes,
    and there is no separate nightly channel.

Only one server target is built: Jellyfin 12.0, target ABI `12.0.0.0`, on .NET 10.
The version starts at 5.x so it sorts above upstream's 4.x packages. Keep all four
components numeric and below 65535. Before the workflow run counter reaches
that limit, revise the versioning scheme.

The archive contains the plugin, the protocol/support assemblies allowlisted in
`scripts/release.py`, and `meta.json`. Its filename
is `SSO-Auth_<version>.zip`. MD5 is required by Jellyfin's catalog format;
SHA-256 is retained as an additional downloadable checksum.

Release scripts use the actual `GITHUB_REPOSITORY` for ownership, image and
asset URLs, so forks do not publish links back to archived upstream. The initial
catalog is empty until the first successful release. Metadata and checksums are
generated from the package rather than copied from a separate `build.yaml`.

## Repository setup

Allow GitHub Actions to create releases and commit the catalog to `main` with
`contents: write`. Branch rules must allow this bot update. Restrictive branch
rules need a deliberate maintainer decision; the workflow does not bypass them.
A catalog push failure fails the release job and can be retried after resolving
the repository setting or conflict.

A rerun replaces assets for the same run-number version and updates that catalog
entry without duplicating it. The catalog is sorted numerically, so retrying an
older job cannot make it the first catalog version. Keep release artifacts and
their catalog entry consistent when rerunning a job.

## Documentation publication

The documentation URL is [https://nekoshinobi.github.io/jellyfin-plugin-sso/](https://nekoshinobi.github.io/jellyfin-plugin-sso/).

In **Settings → Pages → Build and deployment**, select **GitHub Actions**.
The `Documentation` workflow builds Zensical from `zensical.yml` with strict link/anchor validation
on pull requests and `main`, then deploys `main` to the `github-pages` environment.
It installs the Python version from `.python-version` and synchronizes the
`pyproject.toml` dependencies from `uv.lock` with `uv sync --locked`. Builds and
link checks run through `uv run --locked`. It can also be dispatched manually.
Pull requests upload a preview artifact
without deployment credentials.

GitHub supplies the Pages base URL, including the repository subpath or custom
domain. The workflow publishes only the generated `site/` directory. It never
copies source configuration or build output into the site. The plugin
catalog remains on `main`; it does not depend on a Pages deployment.

See [GitHub's custom Pages workflow documentation](https://docs.github.com/en/pages/getting-started-with-github-pages/using-custom-workflows-with-github-pages)
and the [Zensical configuration reference](https://zensical.org/docs/setup/basics/).

The repository's private vulnerability reporting should also be enabled before
inviting security reports. No release or Pages deployment is created merely by
running the local build commands.

## Verification record

Public checks on **2026-09-23** confirmed:

- [Private vulnerability reporting](https://api.github.com/repos/NekoShinobi/jellyfin-plugin-sso/private-vulnerability-reporting)
  is enabled for this fork.
- The `github-pages` environment exists and its deployment branch policy permits
  `main`. The latest documentation deployment succeeded, and the published site
  serves the SSO documentation.
- The build/release workflow requests `contents: write` only for publishing.
  A successful `main` run published
  [5.0.0.6](https://github.com/NekoShinobi/jellyfin-plugin-sso/releases/tag/5.0.0.6)
  and updated the public catalog, demonstrating effective publishing access.
- The downloaded ZIP's plugin identity, version, and target ABI match
  [the published catalog](https://raw.githubusercontent.com/NekoShinobi/jellyfin-plugin-sso/main/manifest.json).
  The catalog's MD5 and both downloadable checksum files match the ZIP bytes.
  Its SHA-256 is
  `FD377387897E032A5C70D970A235DDBEFF6768CBDFCBA3741B02F353F3BC2B26`.

The maintainer confirmed the remaining repository settings on **2026-09-23**:
Pages uses **GitHub Actions**, and default workflow permissions are **Read repository
contents and packages permissions**. These defaults are compatible with the
publishing job's explicit `contents: write` permission. No settings changes were
needed.

For future verification, an administrator can inspect these settings with
read-only requests:

```sh
gh api repos/NekoShinobi/jellyfin-plugin-sso/pages
gh api repos/NekoShinobi/jellyfin-plugin-sso/actions/permissions/workflow
```

Expect Pages `build_type` to be `workflow`. Check workflow defaults against the
explicit job permissions above; default `read` is compatible with an explicitly
granted publishing job. Repeat the public catalog/ZIP/checksum comparison after
release workflow changes; local packaging alone does not verify publication.
