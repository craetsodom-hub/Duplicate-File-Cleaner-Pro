# QA Strategy

The engineering gate is `scripts/verify.ps1` and has five layers:

1. Core deterministic tests prove grouping, hash-collision resistance, physical alias handling, checked arithmetic, orchestration, cancellation races, progress invariants, and workflow state.
2. Windows integration tests use disposable temp corpora to prove full file identity, hard links, reparse/ADS/policy exclusion, Unicode and long-path behavior, mutation fail-closed behavior, streaming resources, and exact results.
3. Presentation-state tests exercise the UI-neutral workflow controller without HWND automation.
4. A packaged launch smoke test proves the production process/window can activate.
5. Screenshot QA remains useful at visual milestones but is not an engine-correctness oracle.
6. Cleanup safety tests exercise malformed intent, physical-identity overlap, candidate/keeper replacement and mutation, cancellation/partial success, Recycle Bin failure, throwing observers, and a fixed-seed 1,000-case survivor-invariant matrix.
7. Cleanup presentation tests verify review snapshots, candidate versus reclaimed accounting, progress, partial results, cancellation, outcome mapping, stale-session behavior, and close-request cancellation without HWND automation.
8. Settings tests verify System/Light/Dark defaults, persistence, invalid-value fallback, no duplicate theme application, and that the settings store writes only the appearance key.
9. Accessibility tests verify resource-backed interactive names, semantic shell markers, safe cleanup-dialog default behavior, and explicit feedback when survivor protection prevents the final selection in a duplicate group.
10. `scripts/stress.ps1` is an explicit unattended Phase 10 gate. It creates and removes temporary corpora for 25,000-file discovery, same-size negatives, 2,000 verified duplicate groups, streaming large files, cancellation, repeated scans, and a 5,000-group/50,000-member Results state. It is intentionally excluded from routine verification.
11. `scripts/e2e.ps1` is the Phase 11 unattended integration gate. It runs the Windows filesystem, App presentation, cleanup handoff, safety-adversarial, and bounded real Recycle Bin workflows with unique disposable corpora; HWND automation is not a correctness dependency.

The deterministic safety corpus contains thousands of files and hundreds of MiB, including exact groups, same-size negatives, empty and Unicode duplicates, large files, hard links, nesting, and skipped policy objects. It verifies exact groups, independent members, reclaimable bytes, and reports real timings/memory/handle observations.

Automated destructive tests may operate only on disposable generated test data. They must never target a developer's valuable personal directories.

One bounded `RecycleBinSmoke` integration test scans two generated Unicode-named copies through real discovery/detection, creates the App review snapshot, invokes the presentation workflow and real `IFileOperation` Recycle Bin boundary, and proves the selected file is absent while its keeper remains. The larger cleanup matrix uses fakes and never fills the Recycle Bin.

The verifier also rejects production destructive APIs, synchronous async blocking, QA hooks, network/telemetry/upload APIs, reference-image drift, build warnings, and whitespace errors.
