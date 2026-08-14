# Architecture

## Boundaries

- **App** owns WinUI XAML, navigation, dialogs, accessibility, localization, theme resources, and dispatching coalesced state onto the UI thread.
- **Core** owns immutable scan snapshots, safety contracts, exact-duplicate proof, session orchestration, cancellation, progress semantics, and the UI-neutral scan workflow controller.
- **Infrastructure.Windows** owns Windows root normalization, enumeration, stable file identity, stream inspection, and bounded read-only content analysis.

## Dependency direction

```text
App -> Core
App -> Infrastructure.Windows
Infrastructure.Windows -> Core
Core -> no App or Infrastructure dependency
```

Core has no Windows or UI dependency. One explicit worker boundary surrounds the discovery/analysis pipeline even when dependencies complete synchronously. Infrastructure never knows UI state; App does not decide filesystem safety. No repository/factory framework, database, global mutable scan state, or service locator is present.

The scan workflow controller owns one active run, cancellation, state, failure, and in-memory result lifetime. The Window only translates those states into controls and guards/coalesces dispatcher updates by run generation.
