# ADR-0007: Recycle Bin Default

Status: Accepted for desktop/full-trust operation; Phase 6 will add packaged integration coverage.

The default removal path is an explicit Windows Recycle Bin operation. It must report failure and leave the file in place; no permanent-delete fallback exists. The Phase 0 harness moved only a generated marker-owned temporary file through a Recycle Bin request and verified that the source path was removed.
