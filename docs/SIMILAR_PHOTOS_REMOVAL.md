# Similar Photos Removal Safety

Similar Photos identifies visually related local images. It does not prove that files are identical, interchangeable, or safe to remove.

## Authority

Removal authority comes only from an explicit, session-only `Consider removing` mark followed by the dedicated Review removal screen and native confirmation. Similarity tier, dimensions, dates, names, locations, and file sizes never select candidates automatically.

Opening Review removal captures immutable intent. Filters and search do not change that snapshot. Review marks, thumbnails, paths, and outcomes are not persisted.

## Independent Survivor

Every affected similarity group must retain at least one independent physical file. The presentation layer prevents marking every member, and the removal planner enforces the invariant independently. Path aliases and duplicate physical identities fail closed; the application never chooses a keeper automatically.

Immediately before each removal, the engine requires at least one currently valid independent survivor. If a remaining photo disappears, changes, becomes inaccessible, or gains ambiguous hard links, the candidate is skipped.

## Revalidation

The Windows platform boundary compares the analyzed snapshot with the current filesystem object:

- physical file identity;
- length;
- last-write and change timestamps;
- regular local-file and reparse policy;
- hard-link count.

A missing, replaced, changed, inaccessible, reparse, or otherwise ambiguous candidate is skipped. Visual similarity is not recomputed as deletion authority, and byte equality with the survivor is intentionally not required.

## Execution

The audited Windows `IFileOperation` boundary is the only production destructive operation. It always requests Recycle Bin semantics and has no permanent-delete fallback.

Execution is sequential and cancellation is observed between independent shell operations. Completed Recycle Bin moves are not rolled back if a later item is skipped or fails. Outcomes report moved, skipped, failed, and cancelled items factually, including actual moved bytes rather than projected reclaimable space.

After any destructive attempt, the active Similar Photos result becomes stale, marks are invalidated, and another removal requires a new analysis.

## Limitations

Windows owns Recycle Bin restoration; the application provides no custom undo and does not guarantee recoverability. Filesystems or volumes that cannot guarantee the audited Recycle Bin operation are reported as failures. Concurrent changes can cause conservative skips even when a user still considers the photo removable.
