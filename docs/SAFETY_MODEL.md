# Safety Model

The following invariants are permanent requirements for any future implementation:

- Fail closed whenever file state, identity, eligibility, or cleanup intent is uncertain.
- Metadata-only comparisons never establish duplicate status.
- Cleanup intent must be immutable and revalidated immediately before any operation.
- At least one independently stored, verified copy must survive every cleanup plan.
- v1 handles local regular files only. Ambiguous filesystem objects are skipped.
- A file changed between scan and cleanup is skipped.
- v1 uses the Windows Recycle Bin; permanent deletion is prohibited.
- The application must not request elevation or modify ACLs or ownership.
- Safety always takes priority over performance or convenience.

Later phases must be hardlink- and physical-file-identity-aware and must perform immediate pre-cleanup revalidation. This document deliberately does not prescribe an implementation that could weaken these guarantees.
