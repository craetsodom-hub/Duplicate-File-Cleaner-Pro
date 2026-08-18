# Folder Intelligence

Phase 19 adds two inspection-only workflows under Exact Duplicates: Duplicate Folders and Compare Folders.

## Duplicate Folders

A verified duplicate-folder group contains two or more local folder roots whose eligible recursive file trees have the same normalized relative paths, file lengths, SHA-256 candidate digests, and byte-for-byte verified content. Folder names, creation/modified timestamps, ACLs, ownership, archive attributes, and empty directories do not prove equivalence. A folder with no eligible files is not reported.

Relative paths use Windows separators and are compared with ordinal, case-sensitive keys. This conservative choice avoids false equivalence when a directory has case-sensitive behavior; `A.txt` and `a.txt` are therefore distinct keys. Folder roots are normalized and overlapping selected roots are collapsed before discovery in Duplicate Folder mode.

The pipeline is safe and staged: local regular-file discovery, structural path/length candidate grouping, session-only content hashing, exact per-file comparison, final snapshot validation, and deterministic group construction. Aggregate signatures only reduce candidates and are never published as proof.

Logical file counts describe paths in a tree. Independent physical-file counts and potential reclaimable bytes are based on physical file identity, so hard-link aliases are not double-counted. Potential reclaimable space is the physical byte set outside the deterministically retained first root; it is informational only.

Nested duplicate-folder groups are suppressed when an equivalent parent group already represents the same relationships. This keeps results focused on the highest meaningful duplicate roots.

## Compare Folders

The user chooses a Master folder and one or more target folders. Master is only the user's reference; it is not treated as newer, authoritative, or safer. Each target is compared independently and results are session-only.

Rows are classified as:

- `Identical`: same relative path and exact verified content.
- `Different`: same relative path exists in both trees but bytes differ.
- `OnlyInMaster`: no target file at that path and no verified exact copy elsewhere.
- `OnlyInTarget`: no Master file at that path and no verified exact copy elsewhere.
- `MovedRenamedExactMatch`: a Master file has no same-path target, but one or more target files contain verified identical bytes at other paths.

Multiple target copies are shown together rather than choosing an arbitrary winner. Matching uses SHA-256 candidate buckets followed by byte-for-byte comparison and snapshot validation. Filename similarity, timestamp equality, and same-size evidence are never sufficient.

## Criteria, privacy, and limitations

Folder workflows default to all eligible local files and reuse the configured folder and extension exclusions. A filtered eligible tree proves only equivalence within those criteria. UNC/network locations, reparse traversal, offline placeholders, and other unsafe objects remain excluded by the existing discovery boundary.

Analysis is a snapshot operation. Files changed during discovery or verification are skipped conservatively. A bounded in-memory hash cache may be reused within one analysis session; no hashes, paths, results, or comparison history are persisted. No network, cloud, account, telemetry, synchronization, overwrite, rename, move, delete, or folder cleanup operation is performed.

CSV and TXT exports are explicit local file saves and contain factual paths, statuses, sizes, modified dates, group metrics, and exact-match relationships. Export history is not retained.
