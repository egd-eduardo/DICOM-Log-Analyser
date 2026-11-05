# Third-party notices

The distributed application includes the following third-party components. Full licence texts are available from
the linked projects.

| Component | Version | Licence | Project |
|---|---|---|---|
| .NET runtime and ASP.NET Core | 10.0.14 | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/aspnetcore |
| Microsoft.Data.Sqlite | 10.0.14 | MIT | https://github.com/dotnet/efcore |
| SQLitePCLRaw | 2.1.13 | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| SQLite (e_sqlite3 build) | bundled with SQLitePCLRaw | Public domain | https://sqlite.org |

Central mode additionally runs, as a separate container that is not part of this application:

| Component | Version | Licence | Project |
|---|---|---|---|
| oauth2-proxy | 7.15.5 | MIT | https://github.com/oauth2-proxy/oauth2-proxy |
| Ubuntu chiseled .NET base image | 10.0.14-noble-chiseled-extra | Various (see image) | https://github.com/dotnet/dotnet-docker |

Development-only tools (xUnit, Microsoft.NET.Test.Sdk, TypeScript) are not distributed.
