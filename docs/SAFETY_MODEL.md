# Safety Model

The following invariants are permanent requirements for any future implementation:

- Fail closed whenever file state, identity, eligibility, or cleanup intent is uncertain.
- Metadata-only comparisons never establish duplicate status.
- Cleanup intent must be immutable and revalidated immediately before any operation.
- At least one independently stored, verified copy must survive every cleanup plan.
- v1 handles local regular files only. Ambiguous filesystem objects are skipped.
- A file changed between scan and cleanup is skipped.
- Detection snapshots are evidence for review, never deletion authorization. Cleanup must independently revalidate every member and keeper.
- Physical identity uses the full Windows volume/file identity; path equality never substitutes for unavailable identity.
- Any hash, comparison, final-snapshot, arithmetic, or mutation uncertainty invalidates the affected duplicate set.
- v1 uses the Windows Recycle Bin; permanent deletion is prohibited.
- The application must not request elevation or modify ACLs or ownership.
- Safety always takes priority over performance or convenience.

Phase 6 implements these rules through an immutable cleanup plan followed by independent execution-time proof. The executor holds a no-write/no-delete-share guard on a verified keeper across each candidate's Recycle Bin call. Candidate paths are rechecked immediately before the pathname-based Shell operation; the remaining final pathname-consumption race is documented in `CLEANUP_SAFETY.md` rather than concealed.
