<p align="center">
  <img src="img/sso-icon.png" alt="Jellyfin SSO icon" width="240">
</p>

<h1 align="center">Jellyfin SSO</h1>

<p align="center">
  Single sign-on for Jellyfin with OpenID Connect and SAML.
</p>

<p align="center">
  <a href="https://github.com/NekoShinobi/jellyfin-plugin-sso/actions/workflows/build.yml"><img src="https://github.com/NekoShinobi/jellyfin-plugin-sso/actions/workflows/build.yml/badge.svg?branch=main" alt="Build and release status"></a>
  <a href="https://github.com/NekoShinobi/jellyfin-plugin-sso/releases/latest"><img src="https://img.shields.io/github/v/release/NekoShinobi/jellyfin-plugin-sso?label=release&amp;color=00a4dc" alt="Latest release"></a>
  <a href="docs/installation.md"><img src="https://img.shields.io/badge/Jellyfin-12.0%2B-00a4dc" alt="Jellyfin 12.0 or later"></a>
  <a href="docs/development.md"><img src="https://img.shields.io/badge/.NET-10-512bd4" alt=".NET 10"></a>
  <a href="LICENSE.txt"><img src="https://img.shields.io/badge/license-GPL--3.0-a87bdb" alt="License: GPL-3.0"></a>
</p>

<p align="center">
  <a href="docs/installation.md">Installation</a> &middot;
  <a href="https://nekoshinobi.github.io/jellyfin-plugin-sso/">Documentation</a> &middot;
  <a href="docs/migration.md">Migration guide</a>
</p>

## Purpose of this Fork

Hello, this is the fork's maintainer notes:

- This is simply a fork to maintain the state of how the fork was left, with the assistance of AI to avoid migrating over to a new unknown plugin.
- The goal is mostly for stability until there is a better, well established solution in the wild (and if Jellyfin implements real SSO support).
- I may make some small visual improvements and documentation improvements, but I don't plan on adding new features unless they're something I can easily test or someone is able to responsibly maintain.
- I dogfood my own plugins, and plugins are built automatically, so use at your own risk. I may consider changing this if there is a genuine interest for more stable line vs a dev line
- I only use Authentik + OIDC, so you can be certain that at least this flow should always work. All other combinations will require some effort by the reporter to provide the necessary details to fix the issue.

## Intro

Sign in to Jellyfin through an OpenID Connect or SAML identity provider, link
existing accounts, and manage access through provider roles.

This fork continues [9p4/jellyfin-plugin-sso](https://github.com/9p4/jellyfin-plugin-sso).
It targets **Jellyfin 12.0+** (the release originally called 10.12) and .NET 10.
Installation, browser login/linking, and migration are tested on Jellyfin 12.0
and 12.1. Read the migration guide before upgrading an existing installation.

## Install

Requires Jellyfin 12.0 or later.

1. In Jellyfin, open **Dashboard → Plugins → Repositories** and add this repository:

   ```text
   https://raw.githubusercontent.com/NekoShinobi/jellyfin-plugin-sso/main/manifest.json
   ```

2. Open the **Catalog**, install **SSO Authentication**, and restart Jellyfin.
3. Open **Dashboard → SSO** to add your identity provider. See the
   [provider guides](docs/providers/index.md) and the
   [setup guide](docs/getting-started.md).

> [!IMPORTANT]
> Upgrading from the upstream plugin? Remove its old repository
> (`https://raw.githubusercontent.com/9p4/jellyfin-plugin-sso/manifest-release/manifest.json`)
> so Jellyfin only sees one source for this plugin. Your settings, account links,
> and permissions migrate automatically; see the [migration guide](docs/migration.md).

For manual installation, see the [installation guide](docs/installation.md).

## Documentation

[Read the documentation](https://nekoshinobi.github.io/jellyfin-plugin-sso/) on **GitHub Pages**,
or browse the [Markdown guides](docs/index.md) in this repository.
The Pages workflow publishes the site after GitHub Pages is enabled.

- [Configuration and permissions](docs/configuration.md)
- [Account linking and client support](docs/accounts-and-clients.md)
- [Reverse proxies and troubleshooting](docs/troubleshooting.md)
- [Development](docs/development.md) and [releases](docs/releases.md)

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Report security issues through
[SECURITY.md](SECURITY.md).

[GPL-3.0](LICENSE.txt). [Credits](docs/about.md).
