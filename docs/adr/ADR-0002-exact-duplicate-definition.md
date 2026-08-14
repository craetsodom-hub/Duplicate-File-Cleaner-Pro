# ADR-0002: Exact Duplicate Definition

Status: Accepted.

Candidate processing is size grouping, deterministic beginning/middle/end sampling, full SHA-256, and streaming byte-for-byte verification. Full hashes only prune work. The Phase 0 harness injected the same fake full hash for unequal files and the byte verifier rejected the false match. Empty files are excluded from normal duplicate groups.
