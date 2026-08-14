# ADR-0005: Reparse and Cloud Policy

Status: Accepted.

Do not traverse directory reparse points, follow file reparse points as ordinary files, or hydrate cloud-only placeholders. Record compact skip summaries and fail closed when type or identity cannot be proved. Network sources remain deferred unless their safety guarantees are separately proven.
