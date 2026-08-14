# ADR-0004: Session Index

Status: Accepted pending Phase 3 scale benchmarks.

Use Microsoft.Data.Sqlite 10.0.10 with SQLitePCLRaw 2.1.12 for app-owned, temporary local scan-session metadata. The Phase 0 harness created an indexed 10,000-record database and executed a candidate query successfully. Sessions contain metadata only and will use marker-owned directories, batched transactions, bounded WAL behavior, and safe cleanup.
