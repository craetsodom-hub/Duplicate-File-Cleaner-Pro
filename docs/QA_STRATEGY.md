# QA Strategy

Later phases will add focused unit tests, deterministic synthetic file corpora, disposable temp-directory integration tests, filesystem race/change tests, hardlink tests, cancellation tests, cleanup-safety tests, UI automation, accessibility testing, and performance tests.

Automated destructive tests may operate only on disposable generated test data. They must never target a developer's valuable personal directories.

Phase 0 verifies project boundaries and establishes MSTest as the Core test foundation.
