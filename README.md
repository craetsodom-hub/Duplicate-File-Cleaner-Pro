# Duplicate File Cleaner Pro

Duplicate File Cleaner Pro is a Windows desktop application being engineered for safe, exact local-file duplicate cleanup.

Status: **Phase 0 — Engineering foundation**. It is not production-ready and does not yet scan, compare, or clean files.

## Technology

C#, .NET 10, WinUI 3, Windows App SDK 2.3.1 Stable, packaged MSIX-ready desktop architecture, and MSTest.

## Structure

- `src/DuplicateFileCleanerPro.App` — packaged WinUI application shell.
- `src/DuplicateFileCleanerPro.Core` — platform-independent domain and safety logic.
- `src/DuplicateFileCleanerPro.Infrastructure.Windows` — future Windows-specific implementations.
- `tests/DuplicateFileCleanerPro.Core.Tests` — Core safety and architecture tests.
- `docs` — governing engineering and safety documentation.

## Safety first

User-file safety takes priority over performance and convenience. The governing [Product Constitution](docs/PRODUCT_CONSTITUTION.md) and [Safety Model](docs/SAFETY_MODEL.md) define the permanent product constraints.

## Development

Install the SDK pinned in `global.json`, then run:

```powershell
./scripts/verify.ps1
```

The Phase 0 CI baseline builds the packaged app for x64 and runs the Core tests.
