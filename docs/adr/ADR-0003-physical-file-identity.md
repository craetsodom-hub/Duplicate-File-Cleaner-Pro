# ADR-0003: Physical File Identity

Status: Accepted for NTFS-capable local volumes; other filesystems require capability-gated behavior.

Use `GetFileInformationByHandleEx(FileIdInfo)` on an opened Win32 file handle to form a volume serial number plus 128-bit file identifier. The Phase 0 harness proved that a hard link has the same identity, a rename retains it, and a replacement at the original path changes it. Hard-link aliases are deduplicated before hashing and reclaimable-space calculation.
