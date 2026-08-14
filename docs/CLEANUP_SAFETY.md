# Cleanup Safety Engine

Phase 6 treats review selection as immutable intent, never as authorization. `CleanupPlanner` accepts one verified completed scan plus selected physical identities, rejects malformed or overlapping group membership, and emits an in-memory immutable plan. `CleanupEngine` validates that plan again and executes at most one cleanup session at a time.

Before every candidate operation, at least one explicitly unselected, independent keeper must still match its scan snapshot. Windows execution then reopens candidate and keeper, verifies full volume plus 128-bit file identity, length, last-write time, change time, regular-file policy, local storage, and absence of reparse points, cloud/offline state, and alternate streams. SHA-256 is a filter only; a current streaming byte-for-byte comparison is mandatory.

Hard-link aliases remain one physical member. A candidate whose current link count is not exactly one is skipped, so removing one pathname cannot be misreported as reclaiming the physical file's bytes. A hard-linked keeper may still protect a group, but its aliases never increase the independent-survivor count.

Immediately around the Shell call, Infrastructure.Windows holds an identity-verified keeper handle that denies write and delete sharing. This prevents ordinary mutation, rename, or disappearance of the keeper until the candidate operation returns. The candidate pathname is checked again immediately before `IFileOperation`, but the Shell API remains pathname based. A replace-at-path race between that final check and the Shell consuming the pathname cannot be eliminated without bypassing the Recycle Bin contract; this residual race is documented and fails closed in every testable window.

The only production destructive boundary is `IFileOperation::DeleteItem` configured with `FOFX_RECYCLEONDELETE`, `FOFX_ADDUNDORECORD`, silent/no-error UI, no recursion, and early failure. Failure or abortion returns a structured failure. There is no permanent-delete fallback, elevation, ACL change, move, rename, or overwrite path.

Cleanup is intentionally partially successful. Every candidate has a precise immutable outcome. Bytes count as actually reclaimed only after the Recycle Bin operation succeeds. Cancellation stops before the next safe boundary and never attempts to roll back earlier successful Recycle Bin operations.
