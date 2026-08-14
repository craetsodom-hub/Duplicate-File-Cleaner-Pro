# Phase 0 Feasibility Plan

Phase 0 produces executable evidence before any production UI work.

| Gate | Experiment | Pass condition |
| --- | --- | --- |
| 0A | Windows handle-based identity harness | hard links share identity; rename retains it; replacement changes it |
| 0B | Exact-content harness | size, sample, hash, then byte comparison; injected collision is rejected |
| 0C | Recycle Bin feasibility | a generated, marker-owned file can be requested to move to Recycle Bin with no permanent fallback |
| 0D | Disk-backed index | Microsoft.Data.Sqlite licensing, restore, x64 package compatibility, and representative record benchmark are evidenced |
| 0E | Dependency audit | every candidate dependency has purpose, version, license, native/package implications, and attribution status |
| 0F | Packaging feasibility | a minimal x64 packaged WinUI app using a development-only identity builds and launches |
| 0G | Deletion journal | synthetic interrupted-operation reconciliation is idempotent and never resumes deletion |

The phase ends only after evidence is reviewed, ADRs are written, required automated gates pass, and no manual gate remains.
