# Duplicate File Cleaner Pro

Duplicate File Cleaner Pro is a Windows desktop application being engineered for safe, exact local-file duplicate cleanup.

Status: **Phase 4 — real read-only scan workflow and exact duplicate detection**. Results review and cleanup are not implemented; the application is not production-ready.

## Technology

C#, .NET 10, WinUI 3, Windows App SDK 2.3.1 Stable, packaged MSIX-ready desktop architecture, and MSTest.

## Structure

- `src/DuplicateFileCleanerPro.App` — packaged WinUI application shell.
- `src/DuplicateFileCleanerPro.Core` — platform-independent domain and safety logic.
- `src/DuplicateFileCleanerPro.Infrastructure.Windows` — Windows filesystem identity, discovery, and read-only content analysis.
- `tests/DuplicateFileCleanerPro.Core.Tests` — Core safety and architecture tests.
- `docs` — governing engineering and safety documentation.

## Safety first

User-file safety takes priority over performance and convenience. The governing [Product Constitution](docs/PRODUCT_CONSTITUTION.md) and [Safety Model](docs/SAFETY_MODEL.md) define the permanent product constraints.

## Development

Install the SDK pinned in `global.json`, then run:

```powershell
./scripts/verify.ps1
```

The gate restores and builds Release x64, runs Core and Windows integration suites (including the generated safety corpus), and audits architecture, safety APIs, privacy, QA-hook leakage, reference integrity, and whitespace.
