# Scan session workflow

Phase 4 keeps one in-memory workflow at a time. A UI-neutral Core controller owns Start/Cancel, run generation, state, failure, and result lifetime. The session service moves through `Preparing`, `Discovering`, `Analyzing`, then exactly one of `Completed`, `Cancelled`, or `Failed`.

Discovery reports real paths, discovered counts, and skips and stays indeterminate because total scope is unknown. Hash analysis reports monotonically processed candidate bytes against the repeated-length physical-file total. Mandatory byte verification is explicitly marked and displayed indeterminately; hash bytes reaching their denominator never pretends verification is complete. UI delivery is coalesced without changing engine counters.

The entire filesystem pipeline runs behind one explicit worker boundary, independent of synchronization-context timing. Cancellation reaches discovery, hashing, comparison, final validation, and the completion transition. Repeated cancellation is safe; queued updates are generation-guarded; a cancelled or failed workflow exposes no completed result. A second run is valid after cancellation or completion.

The session opens no scanned file for writing and does not persist scan history or results.
