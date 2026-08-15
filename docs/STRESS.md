# Reliability stress suite

Run `./scripts/stress.ps1` for the Phase 10 unattended temporary-corpus suite. It is intentionally separate from the normal verification gate and removes its corpus after completion.

On the Phase 10 engineering machine, the final run measured: 25,000 small unique files discovered in 2,240 ms; 2,000 same-size negatives analyzed in 557 ms; 2,000 two-member exact groups analyzed in 2,108 ms; and a 128 MiB large-file corpus analyzed in 539 ms. Peak working set was 78,884,864 bytes, immediate discovery cancellation completed in 1 ms, and ten repeated scans changed handle count by +4. These are local sanity measurements, not product benchmarks.

The suite also constructs a 5,000-group / 50,000-member Results presentation state, exercises search/sort/filter/selection/expand state, and verifies exact result counts and survivor protection.
