# Release Model

Development MSIX identity is separate from the future Partner Center identity. Production identity values, Store package profile, and submission occur only after the user supplies Partner Center values in Phase 10. Production packages must exclude pseudo-localization, test assets, temporary data, diagnostics, and development identity strings.

Every accepted phase requires its relevant test gate, current documentation, `git diff --check`, a focused commit, a clean working tree, and a non-forced push to `origin/main`.
