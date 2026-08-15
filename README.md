# Duplicate File Cleaner Pro

Duplicate File Cleaner Pro is a Windows desktop application being engineered for safe, exact local-file duplicate cleanup.

Status: **Phase 14 — premium scan configuration and product UX**. The application provides local exact-duplicate discovery, review, Recycle Bin-only cleanup, reusable scan profiles, precise criteria, and persisted setup preferences.

## Technology

C#, .NET 10, WinUI 3, Windows App SDK 2.3.1 Stable, packaged MSIX-ready desktop architecture, and MSTest.

## Structure

- `src/DuplicateFileCleanerPro.App` — packaged WinUI application shell.
- `src/DuplicateFileCleanerPro.Core` — platform-independent domain and safety logic.
- `src/DuplicateFileCleanerPro.Infrastructure.Windows` — Windows filesystem identity, discovery, and read-only content analysis.
- `tests/DuplicateFileCleanerPro.Core.Tests` — deterministic Core, configuration, persistence, presentation, safety, localization, and accessibility tests.
- `tests/DuplicateFileCleanerPro.IntegrationTests` — real Windows filesystem, pipeline, cleanup, E2E, stress, and committed-corpus coverage.
- `docs` — governing engineering and safety documentation.

## Safety first

User-file safety takes priority over performance and convenience. The governing [Product Constitution](docs/PRODUCT_CONSTITUTION.md) and [Safety Model](docs/SAFETY_MODEL.md) define the permanent product constraints.

## Development

Install the SDK pinned in `global.json`, then run:

```powershell
./scripts/verify.ps1
```

The gate restores and builds Release x64, runs Core and Windows integration suites (including the generated safety corpus), and audits architecture, safety APIs, privacy, QA-hook leakage, reference integrity, and whitespace.
