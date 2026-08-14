# Duplicate File Cleaner Pro - Permanent Execution Rules

The Master Product Constitution supplied with this project is authoritative. Read it before each phase and do not broaden scope from visual references.

## Safety

- Data safety overrides performance, convenience, simplicity, and appearance.
- Exact duplicates require distinct physical identities, equal logical length, staged signatures, a full cryptographic hash, and byte-for-byte verification. A hash is never a deletion boundary.
- Recognize hard links by physical file identity; aliases are not reclaimable duplicate copies.
- Do not traverse directory reparse points or hydrate cloud-only placeholders. Fail closed on unsafe, changed, replaced, inaccessible, or special files.
- Never delete automatically, elevate privileges, follow a changed path, fall back from Recycle Bin to permanent deletion, or resume a pending destructive action after a crash.
- Automatic selection must retain at least one verified physical member in every group; enforce this in Core/domain logic and tests.
- Destructive tests may only operate on marker-owned synthetic paths under the current test temporary root.

## Engineering

- Use C#, WinUI 3, Windows App SDK, .NET 10 LTS, MVVM, DI, async/cancellation, x64-first packaged MSIX after feasibility evidence supports them.
- Keep UI, domain rules, filesystem operations, and persistence/journaling separated. UI code contains no business or low-level filesystem logic.
- Keep scanning and hashing off the UI thread; use bounded, cancellable pipelines and disk-backed session storage where validated.
- Use local-only processing. Do not add telemetry, analytics, cloud upload, networking, admin requirements, or background scanning.
- Treat user paths as sensitive. Do not log full paths by default. Never commit session databases, test corpora, packages, diagnostics, build output, or secrets.

## Quality and workflow

- Before changes, inspect Git state. Keep `main` clean, make focused commits only after required tests and `git diff --check` pass, then push accepted phase commits to `origin/main` without force-pushing.
- Use the normal model tier for routine work. Escalate only for a genuinely difficult blocker after a focused failed attempt, following the constitution's evidence format.
- Maintain ADRs for irreversible choices and keep product, architecture, safety, privacy, testing, roadmap, and release documents current.
- Respect all defined manual gates. Ask only for the narrow manual QA procedure required by the constitution.
- Do not implement production UI before Phase 0 is fully evidenced. Inspect all five visual references again at Phase 2.
