# Architecture

## Boundaries

- **App** owns WinUI XAML, application shell, view models, navigation, interaction state, dialogs, accessibility metadata, and localization binding.
- **Core** owns future domain models, safety policies, contracts, state/progress models, duplicate-group invariants, and cleanup planning. It is platform independent wherever practical.
- **Infrastructure.Windows** will isolate Windows filesystem, Shell, Explorer, and Win32 implementations.

## Dependency direction

```text
App -> Core
App -> Infrastructure.Windows
Infrastructure.Windows -> Core
Core -> no App or Infrastructure dependency
```

Core is independently testable. Future long-running work will expose cancellation and progress through Core contracts; UI scheduling and Windows APIs remain outside Core. No speculative framework, database, or service architecture is introduced in Phase 0.
