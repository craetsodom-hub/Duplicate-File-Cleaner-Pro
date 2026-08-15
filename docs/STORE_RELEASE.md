# Store release preparation

Run `./scripts/release.ps1` from a PowerShell 7 (`pwsh`) session at the repository root to build and package the
x64, x86, and ARM64 release candidates under `artifacts/store/`. The command
does not submit anything to Partner Center and does not alter package identity
or version.

The script restores the declared runtime identifiers, builds each architecture,
runs the established x64 verification, creates architecture-specific MSIX
packages, creates a multi-architecture MSIX bundle, creates an MSIX upload
container from those packages, checks package manifests and contents, and writes
SHA-256 values to `artifacts/store/RELEASE-MANIFEST.md`.

`./scripts/release.ps1 -RunWack` runs the locally installed Windows App
Certification Kit after packaging. WACK must be started from an elevated,
interactive Windows session. It writes its HTML/XML report below
`artifacts/store/wack/`.

The source of the release version is the `Identity/@Version` value in
`src/DuplicateFileCleanerPro.App/Package.appxmanifest`. It is currently
`1.0.0.0`, a valid initial Windows package version. A Partner Center association
may require replacing the development identity and publisher before upload; see
`store/PARTNER_CENTER_IDENTITY.md`.

ARM64 is compile/package validated on this x64 machine, but needs a native ARM64
device for runtime certification. x86 is additionally suitable for local x64
smoke testing.

The repository contains a 1x1 transparent `StoreLogo.png` placeholder. It is
intentionally not replaced in Phase 13: a real approved application icon must be
provided before Store submission. This is an artwork handoff, not a reason to
invent a new brand in source.
