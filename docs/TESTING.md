# Testing Strategy

Tests are safety infrastructure. Core tests will cover staged candidate pruning, forced hash collisions, byte verification, hard links, changed files, retention invariants, recoverable-space calculation, and deletion planning. Integration tests will use only marker-authenticated synthetic test roots and fail closed otherwise.

Architecture tests will guard against unsafe regressions such as direct destructive filesystem calls from UI code, raw hash-only classification, unsafe recursive cleanup, hard-coded production strings, unbounded UI result ownership, and unexpected networking.
