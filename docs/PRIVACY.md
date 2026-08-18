# Privacy

Duplicate File Cleaner Pro processes selected files locally. It does not upload file contents, filenames, paths, scan results, or cleanup results.

The current product has no telemetry, analytics, account, cloud processing, scan history, cleanup history, or database. It persists only local product preferences: appearance, current scan sources and criteria, reusable folder/extension exclusions, and user-named custom profiles. Persisted setup can contain folder paths chosen by the user; it remains in packaged-app local settings on this device and is never transmitted. Results, selections, previews, file contents, and cleanup outcomes are not persisted. A Results report is written only when the user explicitly selects a destination in the native Save dialog; the app does not retain a report history or an export path.

Cleanup is a local Windows Recycle Bin operation. This document describes current engineering behavior and is not a legal privacy-policy page.

## Folder Intelligence

Duplicate Folder analysis and Master Folder comparison run locally over the selected eligible file trees. Folder comparison results, verified hashes, moved/renamed relationships, selected roots, and comparison history are session-only and are not saved. Folder workflows do not contact a network service and do not synchronize, copy, overwrite, rename, move, or delete folders.

## Similar Photos

Similar Photos analyzes eligible images locally. It creates bounded image decodes and visual fingerprints in memory for the active analysis only; it does not upload photos, build a persistent image index, retain thumbnails, save similarity groups, keep review marks, removed paths, removal outcomes, or store photo history. The only Similar Photos preference retained locally is the default sensitivity. A photo is eligible for removal only after an explicit session-only `Consider removing` mark, dedicated review, identity revalidation, and confirmation; the operation uses the local Windows Recycle Bin and is not retained as app history.
