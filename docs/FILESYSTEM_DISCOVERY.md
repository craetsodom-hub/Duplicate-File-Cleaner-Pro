# Filesystem discovery

Phase 2 discovery is read-only and local-only. Roots are normalized to absolute paths, compared case-insensitively with path boundaries, deduplicated, and reduced so nested selections are not enumerated twice. UNC and network-backed roots are rejected.

Only ordinary files are emitted. Reparse points are never followed, and offline, hidden/system-by-default, encrypted, inaccessible, unstable, unsupported, or identity-unavailable objects are skipped with a local reason. Cancellation stops enumeration without treating partial results as complete.

Windows identity is the 64-bit volume serial plus the complete 128-bit `FILE_ID_INFO` file ID acquired from a scoped native handle. The same handle supplies length, write time, change time, attributes, and named-stream state. Reparse points are opened as reparse points rather than followed. Hard-link paths therefore retain one physical identity and are not independent reclaimable copies.

File policy is applied to handle-derived attributes. Extended local path syntax is normalized; UNC, device, and network-backed roots fail closed. Files with an additional named NTFS stream are skipped and stream contents are never read. No discovery code changes contents, attributes, ACLs, ownership, names, locations, or Recycle Bin state.
