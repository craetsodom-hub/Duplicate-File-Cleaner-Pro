# Filesystem discovery

Phase 2 discovery is read-only and local-only. Roots are normalized to absolute paths, compared case-insensitively with path boundaries, deduplicated, and reduced so nested selections are not enumerated twice. UNC and network-backed roots are rejected.

Only ordinary files are emitted. Reparse points are never followed, and offline, hidden/system-by-default, encrypted, inaccessible, unstable, unsupported, or identity-unavailable objects are skipped with a local reason. Cancellation stops enumeration without treating partial results as complete.

Windows identity is the 64-bit volume serial plus the complete 128-bit `FILE_ID_INFO` file ID acquired from a scoped native handle. The same handle supplies length, write time, change time, attributes, and named-stream state. Reparse points are opened as reparse points rather than followed. Hard-link paths therefore retain one physical identity and are not independent reclaimable copies.

File policy is applied to handle-derived attributes. Extended local path syntax is normalized; UNC, device, and network-backed roots fail closed. Files with an additional named NTFS stream are skipped and stream contents are never read. No discovery code changes contents, attributes, ACLs, ownership, names, locations, or Recycle Bin state.

Phase 14 adds immutable discovery criteria without changing those safety boundaries. Ready fixed, removable, and RAM drives may be selected alongside local folders; the existing normalizer remains authoritative and still rejects network, unavailable, reparse, and unsupported roots. Include-subfolders is explicit. Reusable folder exclusions stop traversal at exact path boundaries, and extension exclusions override every included type or custom extension.

File-type categories, normalized custom extensions, and inclusive minimum/maximum byte bounds are evaluated only after a safe handle snapshot has established that an entry is an eligible ordinary file. Criteria rejection is reported factually as subfolder, folder, extension, file-type, below-minimum, or above-maximum exclusion. Only accepted snapshots reach exact-content detection. Filtering never establishes duplicate identity and never authorizes cleanup.
