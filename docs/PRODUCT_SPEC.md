# Product Specification

Duplicate File Cleaner Pro is a local-first Windows desktop application for finding exact duplicate file content, reviewing individual copies, and conservatively moving user-selected redundant copies to the Windows Recycle Bin. It is not a general disk cleaner, similar-file detector, cloud service, or automatic cleanup tool.

The Master Product Constitution is the complete authoritative specification. This document is a maintained project summary only.

## V1 boundaries

- Scan user-selected local folders, drives, and removable drives after Phase 0 access feasibility is proven.
- Duplicate classification requires size grouping, deterministic sampling, full SHA-256, and byte-for-byte verification.
- Network storage, cloud-placeholder hydration, similar-file matching, shell extensions, background scanning, telemetry, and automatic cleanup are out of scope.
- Initial results select nothing. Automatic selection helpers must retain one verified physical file per group.
- Default removal is Recycle Bin only; no permanent-delete fallback is permitted.
