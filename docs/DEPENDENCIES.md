# Dependencies and costs

Project rule: **the product never depends on anything paid.** Only open-source components under permissive
licences (MIT, Apache-2.0, BSD, public domain); no paid or external services at runtime; no telemetry.
Users run AI tools on their own subscriptions; the project never pays for, proxies or stores them.

## Runtime

| Component | Licence |
|-----------|---------|
| .NET runtime, ASP.NET Core, Microsoft.Extensions.* | MIT |
| Microsoft.Data.Sqlite | MIT |
| SQLitePCLRaw (bundle_e_sqlite3) | Apache-2.0 |
| SQLite | Public domain |
| xterm.js, @xterm/addon-fit (vendored in `wwwroot/vendor`) | MIT |
| git, curl, systemd (system tools, not bundled) | GPL-2.0 / curl licence / LGPL — used as external programs |

## Development and tests

xUnit (Apache-2.0), Microsoft.NET.Test.Sdk (MIT), coverlet (MIT), Microsoft.AspNetCore.Mvc.Testing (MIT),
Newtonsoft.Json (MIT, transitive in tests).

## Release and project infrastructure (free tiers only)

| Need | Free option |
|------|-------------|
| Code hosting, CI, releases, website | GitHub repository, Actions, Releases, Pages (free for public repos) |
| Security scanning | GitHub CodeQL code scanning and Dependabot (free for public repos) |
| Linux release integrity | GPG or minisign signatures + SHA-256 checksums |
| macOS distribution | Homebrew formula (no paid Apple notarisation required) |
| Windows distribution | winget / Scoop with checksums; SignPath Foundation (free code signing for OSS) if signing is wanted |
| Trademark check | Self-search in the free USPTO / EUIPO databases |

Adding a dependency: it must be open source with a permissive licence, work offline, and be listed here.
