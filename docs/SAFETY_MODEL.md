# Safety Model

Deletion safety is the primary product constraint. A member may be considered for removal only after it has been proven to be a distinct physical file with exact primary-stream byte equality to a retained member. Hash equality is not sufficient.

Before removal, the application will validate a prebuilt immutable plan against the current path, physical identity, type, reparse state, size, and modification state. Any uncertainty skips the item. Recycle Bin failure leaves the item intact; the app never silently permanently deletes it.

Hard-link aliases are not duplicate physical storage. Reparse points are not traversed. Cloud-only placeholders are not hydrated. Files with unsafe special conditions, including meaningful alternate data streams where detected, are excluded from automatic selection.
