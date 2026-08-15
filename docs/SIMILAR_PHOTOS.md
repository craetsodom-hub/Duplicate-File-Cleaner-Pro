# Similar Photos

## Semantics and boundary

Similar Photos is a read-only analysis mode for local images that appear visually related. It is separate from `ExactDuplicateDetector`: similarity is evidence, not mathematical identity, and its groups must never be called verified, exact, safe, or automatically removable. The Phase 16 engine has no cleanup plan, keeper rule, selection, rename, move, delete, or Recycle Bin dependency.

`DuplicateFileCleanerPro.Core.Similarity` owns the platform-neutral fingerprints, thresholds, candidate index, comparison evidence, complete-link grouping, progress, cancellation, and result models. `DuplicateFileCleanerPro.Infrastructure.Windows.Similarity` owns bounded Windows image decoding and discovery-snapshot validation. No Similar Photos UI or persistent index exists in this phase.

## Decode and fingerprint pipeline

The Windows decoder uses the installed Windows Imaging Component codecs through `BitmapDecoder`. It reads only local files, applies EXIF orientation to the decoded analysis bitmap without modifying the source, downsamples with Fant interpolation to a maximum dimension of 64 pixels, converts to BGRA8, and releases each decoded bitmap before the next file. Full-resolution images are never retained by the engine.

Each decoded image produces complementary, explainable evidence:

- contrast-normalized 16×16 luminance structure;
- horizontal and vertical 64-bit difference hashes;
- a 64-bit average hash;
- a 12-bin RGB histogram;
- aspect ratio;
- a second center-cropped structure/hash fingerprint for modest crop tolerance.

Comparison uses a weighted composite of structure (38%), directional difference hashes (24%), average hash (14%), color distribution (14%), and aspect ratio (10%). Structural, difference-hash, and aspect gates must also pass; a high score in one feature cannot compensate for unrelated structure.

## Thresholds and user meaning

The engine exposes `Strict`, `Balanced`, and `Broad` profiles but no technical controls. Balanced is the conservative default. Internal composite minimums are 0.92, 0.84, and 0.76 respectively, with correspondingly lower structural/hash/aspect gates. Public results use honest tiers rather than a misleading percentage:

- **Very similar:** composite evidence at least 0.93.
- **Similar:** at least 0.84.
- **Loosely similar:** below 0.84 but accepted by the selected profile.

The composite remains available as diagnostic evidence for calibration; it is not a probability or a claim of exactness.

## Candidate reduction and grouping

For at most 256 eligible photos, exhaustive comparison provides accuracy with a bounded small input. Larger libraries use an aspect-ratio bucket plus twelve locality-sensitive 16-bit segments drawn from horizontal dHash, vertical dHash, and average hash. Each photo considers at most 64 indexed candidates, while identical fingerprints also compare to a deterministic representative. Bucket retention is bounded, preventing quadratic candidate growth on repetitive libraries.

Similarity is non-transitive. Groups are not connected components. The deterministic builder chooses the photo with the strongest accepted neighborhood as representative, then admits a photo only when it meets the selected threshold against **every** current member. This complete-link rule may split a visual family into several smaller groups; that is intentional and prevents A≈B≈C chains from implying A≈C.

## Formats

The eligible extensions are JPEG/JPG, PNG, BMP, GIF, TIFF/TIF, WebP, HEIC, and HEIF. JPEG, PNG, BMP, still-frame GIF, and TIFF decode through codecs present in the Windows baseline and are covered by generated integration tests. GIF analysis uses the first frame. WebP and HEIC/HEIF depend on a locally installed Windows codec. Missing codecs and corrupt files are skipped with structured reasons; support is never fabricated. There is no cloud hydration or network fallback.

## Correctness, progress, and safety

Every decode validates physical identity, length, last-write time, change time, and alternate-stream state before and after analysis. Replaced or changed files are skipped. Errors are isolated as unsupported format, codec unavailable, corrupt image, inaccessible, changed during analysis, or decode failed.

Cancellation is observed during decoding, fingerprint creation, candidate indexing, comparison, and group construction. Progress reports `FindingPhotos`, `AnalyzingPhotos`, `ComparingSimilarities`, and `BuildingGroups`; counts are determinate only when their denominator is known. Progress observers cannot alter correctness.

All processing is local and in memory for the active run. There is no network dependency, telemetry, upload, account, persistent fingerprint database, hidden index, or scan history.

## Calibration and limitations

The deterministic corpus covers exact renamed images, JPEG recompression, resize, brightness and contrast changes, a modest crop, two-degree rotation, Unicode names, same-scene variations, unrelated scenes with shared colors/layout/brightness, repetitive stripe patterns, corrupt data, unsupported extensions, and replacement races. Balanced detected all declared related transformations in the current generated corpus with no adversarial false positives.

Known limitations:

- large crops, material viewpoint changes, strong rotations, overlays, and severe edits may split groups;
- complete-link grouping intentionally favors smaller trustworthy groups over recall;
- visually repetitive or low-information images are difficult, so the conservative gates can produce false negatives;
- GIF animation beyond the first frame is ignored;
- WebP and HEIC/HEIF availability varies by machine;
- this is perceptual evidence, not identity proof, and must remain non-destructive until a separate review UX is designed.
