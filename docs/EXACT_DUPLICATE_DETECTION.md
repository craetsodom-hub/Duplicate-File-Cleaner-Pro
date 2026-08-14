# Exact duplicate detection

Phase 3 adds a read-only duplicate engine; it neither selects nor deletes files.

The detector accepts the Phase 2 discovery snapshot and first retains one deterministic pathname for each distinct `PhysicalFileIdentity`. This makes Windows hard-link aliases one file for grouping and reclaimable-space accounting.

Candidates are grouped by length. Files in a unique length group are never opened for hashing. This phase intentionally omits an optional sample fingerprint: the extra reads add complexity without changing the mandatory full-hash and byte-comparison proof. Remaining candidates are hashed with SHA-256 through a 64 KiB streaming buffer, then files sharing a digest are compared byte-for-byte through two bounded buffers. A digest alone is never a duplicate decision.

The Windows content adapter opens input only for reading and allows concurrent sharing. It validates full physical identity, length, write time, change time, and absence of extra named streams before and after hashing or comparison. Every proposed member is reopened for a final lightweight snapshot validation before a group is published. A comparison or validation failure invalidates the whole affected equivalence class; uncertain earlier comparisons cannot survive as a group.

Groups, members, and structured skips use deterministic normalized-path ordering (ordinal, case-insensitive) and read-only owned collections. Reclaimable bytes are calculated only for independent physical files in a confirmed group as `checked((N - 1) * size)` and summed with checked arithmetic. Cancellation or overflow cannot return partial success. Detection results are snapshots, never cleanup authorization; later cleanup must independently reopen, identify, hash, compare, and preserve a keeper.
