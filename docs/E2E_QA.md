# End-to-end QA

Run `./scripts/e2e.ps1` for the unattended Windows workflow suite. It composes App presentation state, Core workflows, Windows discovery/content analysis, and the bounded real Recycle Bin smoke without HWND, picker, or production QA hooks.

The suite uses uniquely named temporary corpora and covers full scan-to-Results-to-review-to-Recycle-Bin-to-stale-result behavior, fresh rescan, candidate and keeper replacement/mutation/disappearance, reparse and hard-link safety, Unicode/long paths, cancellation, failure boundaries, and real presentation handoff. Phase 14 also runs its committed source corpus through profile, type, extension, size, subfolder, and exclusion combinations. It excludes only the explicitly separate Phase 10 `Stress` category.
