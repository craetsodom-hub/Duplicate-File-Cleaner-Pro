# Scan session workflow

Phase 4 keeps one in-memory scan session at a time. The Core session service moves through `Preparing`, `Discovering`, `Analyzing`, then either `Completed`, `Cancelled`, or `Failed`.

Discovery reports real current paths, discovered-file counts, and skips. Its progress is deliberately indeterminate because recursive enumeration has no trustworthy total beforehand. After discovery, analysis reports processed candidate bytes against the known total for repeated-length physical files. The UI uses this only for real hash-stage progress; byte verification remains part of the engine's completed proof, never a fabricated percentage.

Cancellation is propagated to discovery, stream hashing, and byte comparison. A cancelled session exposes no completed result. A completed result contains the accepted discovery snapshot and verified exact-duplicate result only in memory for Phase 5; it does not authorize cleanup.

The session opens no scanned file for writing and does not persist scan history or results.
