# Filesystem discovery

Phase 2 discovery is read-only and local-only. Roots are normalized to absolute paths, compared case-insensitively with path boundaries, deduplicated, and reduced so nested selections are not enumerated twice. UNC and network-backed roots are rejected.

Only ordinary files are emitted. Reparse points are never followed, and offline, hidden/system-by-default, encrypted, inaccessible, unstable, unsupported, or identity-unavailable objects are skipped with a local reason. Cancellation stops enumeration without treating partial results as complete.

Windows identity is `(volume serial number, file ID)` acquired from a scoped native file handle. Hard-link paths therefore retain the same physical identity and are not independent reclaimable copies. Files with an additional named NTFS stream are skipped; stream contents are never read. No discovery code changes file contents, attributes, ACLs, ownership, names, locations, or recycle-bin state.
