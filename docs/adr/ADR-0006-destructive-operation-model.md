# ADR-0006: Destructive Operation Model

Status: Accepted.

Removal will consume an immutable deletion plan and revalidate the path, physical identity, regular-file state, reparse state, size, and modification state immediately before each operation. A local journal records minimum necessary state. Startup reconciliation never resumes pending deletion; unresolved work becomes nontechnical review state. The Phase 0 harness proved that reconciliation is idempotent and converts pending work to `needs-review` without deleting anything.
