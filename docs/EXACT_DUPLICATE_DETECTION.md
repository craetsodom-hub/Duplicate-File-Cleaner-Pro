# Exact duplicate detection

Phase 3 adds a read-only duplicate engine; it neither selects nor deletes files.

The detector accepts the Phase 2 discovery snapshot and first retains one deterministic pathname for each distinct `PhysicalFileIdentity`. This makes Windows hard-link aliases one file for grouping and reclaimable-space accounting.

Candidates are grouped by length. Files in a unique length group are never opened for hashing. This phase intentionally omits an optional sample fingerprint: the extra reads add complexity without changing the mandatory full-hash and byte-comparison proof. Remaining candidates are hashed with SHA-256 through a 64 KiB streaming buffer, then files sharing a digest are compared byte-for-byte through two bounded buffers. A digest alone is never a duplicate decision.

The Windows content adapter opens input only for reading and allows concurrent sharing. It validates the open handle's physical identity, length, timestamp, and absence of extra named streams against discovery both before and after hashing or comparison. Files that disappear, cannot be read, or change during verification are skipped with a typed reason; they never become a confirmed group. Cancellation returns a distinct, non-success result without reporting partial groups.

Groups, members, and structured skips use deterministic normalized-path ordering (ordinal, case-insensitive). Reclaimable bytes are calculated only for a confirmed group as `checked((N - 1) * size)` and summed with checked arithmetic. Detection results are snapshots, not cleanup authorization.
