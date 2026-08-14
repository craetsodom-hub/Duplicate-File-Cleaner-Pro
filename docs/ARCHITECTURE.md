# Architecture

## Boundaries

- **App** owns WinUI XAML, navigation, dialogs, accessibility, localization, theme resources, and dispatching coalesced state onto the UI thread.
- **Core** owns immutable scan snapshots, safety contracts, exact-duplicate proof, session orchestration, cancellation, progress semantics, and the UI-neutral scan workflow controller.
- **Infrastructure.Windows** owns Windows root normalization, enumeration, stable file identity, stream inspection, bounded read-only content analysis, immediate cleanup revalidation, and the single audited Recycle Bin boundary.

## Dependency direction

```text
App -> Core
App -> Infrastructure.Windows
Infrastructure.Windows -> Core
Core -> no App or Infrastructure dependency
```

Core has no Windows or UI dependency. One explicit worker boundary surrounds the discovery/analysis pipeline even when dependencies complete synchronously. Infrastructure never knows UI state; App does not decide filesystem safety. No repository/factory framework, database, global mutable scan state, or service locator is present.

The scan workflow controller owns one active run, cancellation, state, failure, and in-memory result lifetime. The Window only translates those states into controls and guards/coalesces dispatcher updates by run generation.

## Results review state

Verified `CompletedScanResult` snapshots remain owned by **Core**. The App's `ResultsReviewViewModel` holds only session-local expansion, search, sorting, filtering, and candidate-selection state over those immutable members. It preserves the Core physical identity rather than rebuilding it from paths, prevents selecting every member of a verified group, and can produce a read-only Phase 6 handoff that is explicitly not deletion authorization. Starting a new scan clears the old review state; only a newly completed scan enables Results again.

## Cleanup safety state

Cleanup planning and survivor/outcome semantics live in Core. Cleanup execution consumes an immutable plan, revalidates an explicit keeper before every candidate, and uses a session-scoped `SafetyOperationCoordinator` when scan and cleanup composition share lifecycle ownership. The App exposes a typed conversion from review handoff to Core cleanup intent but has no Phase 6 cleanup command or UI.
