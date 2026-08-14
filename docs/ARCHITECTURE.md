# Architecture

The intended solution separates the WinUI application shell, pure domain/safety rules, Windows filesystem operations, and local infrastructure. Long-running operations are session-scoped, cancellable, and guarded so stale callbacks cannot alter an active workspace.

Phase 0 must validate physical file identity, exact verification, disk-backed indexing, package access, Recycle Bin behavior, and journal recovery before this becomes a production architecture.
