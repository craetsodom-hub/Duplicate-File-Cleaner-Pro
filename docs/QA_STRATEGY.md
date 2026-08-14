# QA Strategy

The engineering gate is `scripts/verify.ps1` and has five layers:

1. Core deterministic tests prove grouping, hash-collision resistance, physical alias handling, checked arithmetic, orchestration, cancellation races, progress invariants, and workflow state.
2. Windows integration tests use disposable temp corpora to prove full file identity, hard links, reparse/ADS/policy exclusion, Unicode and long-path behavior, mutation fail-closed behavior, streaming resources, and exact results.
3. Presentation-state tests exercise the UI-neutral workflow controller without HWND automation.
4. A packaged launch smoke test proves the production process/window can activate.
5. Screenshot QA remains useful at visual milestones but is not an engine-correctness oracle.
6. Cleanup safety tests exercise malformed intent, physical-identity overlap, candidate/keeper replacement and mutation, cancellation/partial success, Recycle Bin failure, throwing observers, and a fixed-seed 1,000-case survivor-invariant matrix.

The deterministic safety corpus contains thousands of files and hundreds of MiB, including exact groups, same-size negatives, empty and Unicode duplicates, large files, hard links, nesting, and skipped policy objects. It verifies exact groups, independent members, reclaimable bytes, and reports real timings/memory/handle observations.

Automated destructive tests may operate only on disposable generated test data. They must never target a developer's valuable personal directories.

One bounded `RecycleBinSmoke` integration test scans two generated Unicode-named copies through the real discovery/detection pipeline, selects one identity, executes the real `IFileOperation` Recycle Bin boundary, and proves the keeper remains. The larger cleanup matrix uses fakes and never fills the Recycle Bin.

The verifier also rejects production destructive APIs, synchronous async blocking, QA hooks, network/telemetry/upload APIs, reference-image drift, build warnings, and whitespace errors.
