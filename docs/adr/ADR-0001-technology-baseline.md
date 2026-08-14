# ADR-0001: Technology Baseline

Status: Accepted for Phase 0 and Phase 1 implementation.

Use C# on .NET 10, WinUI 3 through the stable Windows App SDK 2.3.1, x64-first, MSIX-packaged desktop deployment, MVVM, DI, and asynchronous cancellation. Local evidence: .NET SDK 10.0.201 and Windows SDK 10.0.26100 are installed; a self-contained x64 WinUI package built and launched under a development-only identity.

The development identity is `DuplicateFileCleanerPro.Dev` with a development publisher string. It must never be used for Store submission.
