# Dependencies

Verification date: **2026-11-04**. Sources: nuget.org package registry and vulnerability data (GitHub Advisory
Database through NuGet Audit), the official .NET release metadata (`release-notes/10.0/releases.json`), the
Microsoft Container Registry, the npm registry and the oauth2-proxy changelog and security advisories.

Selection rules: maintained stable releases only, the fewest components possible, exact versions, lock files
(`packages.lock.json`, `package-lock.json`), restore in locked mode in CI, NuGet Audit at level *low* with warnings as
errors (a known vulnerability fails the build).

## Runtime (shipped)

| Component | Version | Why | Notes |
|---|---|---|---|
| .NET runtime / ASP.NET Core | 10.0.14 (LTS) | Host, Kestrel, JSON | Released 2026-10-25 as a security release (CVE fixes listed in the release notes); LTS supported until November 2028 |
| Microsoft.Data.Sqlite | 10.0.14 | SQLite access | Same servicing train as the runtime |
| SQLitePCLRaw (bundle_e_sqlite3, core, provider) | 2.1.13 (transitive) | Native SQLite with FTS5 for Windows, macOS, Linux | Pulled by Microsoft.Data.Sqlite 10.0.14; newer 3.0.x exists but is not the version Microsoft ships and tests with this release, so it is not forced |

No other runtime package is used. No JavaScript library, web font or CDN is used by the UI.

## Container images and services (central mode)

| Image | Pin | Notes |
|---|---|---|
| `mcr.microsoft.com/dotnet/sdk` | `10.0.403-noble@sha256:de4fe90a…dc02b` | Build stage only |
| `mcr.microsoft.com/dotnet/aspnet` | `10.0.14-noble-chiseled-extra@sha256:9b755ea7…131d8` | Shell-less, non-root; `-extra` adds ICU and tzdata for time-zone conversion |
| `quay.io/oauth2-proxy/oauth2-proxy` | `v7.15.5` | Latest stable; fixes GHSA-63jm-59jj-478j, GHSA-wr5q-7wxw-x568, GHSA-hhqp-vx7f-5c6m. **Digest to be pinned on first pull** (registry not reachable from the build environment) |

## Development and test only (not shipped)

| Component | Version |
|---|---|
| .NET SDK | 10.0.1xx or later feature band (`global.json`: 10.0.100, roll forward to latest feature); verified with 10.0.114 and 10.0.403 |
| xunit | 2.9.3 |
| xunit.runner.visualstudio | 3.1.5 |
| Microsoft.NET.Test.Sdk | 18.11.0 |
| TypeScript (UI type check) | 7.0.2, `npm audit`: 0 vulnerabilities |
| Playwright / Chromium | Pre-installed in the build environment, used only for the manual UI run |

## Updating

1. Check the .NET release notes (monthly, Patch Tuesday) and update `Directory.Packages.props`, the Dockerfile tags
   and digests together.
2. `dotnet restore --force-evaluate` to refresh lock files; review the diff.
3. Run `scripts/check.sh`; CI repeats the vulnerability audit.
4. Record the new verification date here.
