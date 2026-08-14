# Dependency Audit

This inventory is the Phase 0 candidate audit. A dependency is not approved for the production package merely because it appears here.

| Dependency | Version | Purpose | License | Native/package implications | Attribution |
| --- | --- | --- | --- | --- | --- |
| Microsoft.WindowsAppSDK | 2.3.1 | WinUI 3 and packaged desktop runtime probe | Microsoft Windows App SDK license terms | Self-contained x64 package probe succeeded; package payload is substantial | Review package `NOTICE.txt` before shipping |
| Microsoft.Data.Sqlite | 10.0.10 | Temporary disk-backed scan-session index probe | MIT | Managed provider; uses SQLitePCLRaw | Include applicable notice inventory at release |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.12 | SQLite native bundle for x64 local session index | Apache-2.0 | Ships native SQLite binary; 2.1.11 was rejected due to GHSA-2m69-gcr7-jv3q | Include Apache notice and SQLite notice as applicable |
| Microsoft.Extensions.DependencyInjection | 10.0.3 | Composition root / DI | MIT | Managed-only | Include only if shipped |
| xUnit + Microsoft.NET.Test.Sdk | test-only | Unit and architecture test host | MIT | Never shipped in app package | None in product notices |

The initial Microsoft.Data.Sqlite 10.0.3 resolution pulled vulnerable SQLitePCLRaw 2.1.11 and was rejected by the build's warning-as-error policy. The tested configuration explicitly resolves the maintained 2.1.12 bundle and restores/builds without vulnerability warnings. No GPL, AGPL, paid, proprietary runtime SDK, telemetry, analytics, or networking dependency is approved.
