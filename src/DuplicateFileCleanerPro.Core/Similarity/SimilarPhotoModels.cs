using DuplicateFileCleanerPro.Core.Discovery;

namespace DuplicateFileCleanerPro.Core.Similarity;

public enum SimilarPhotoSkipReason
{
    UnsupportedFormat,
    CodecUnavailable,
    CorruptImage,
    Inaccessible,
    ChangedDuringAnalysis,
    DecodeFailed,
}

public enum SimilarityTier
{
    LooselySimilar,
    Similar,
    VerySimilar,
}

public enum SimilarPhotoSensitivity
{
    Strict,
    Balanced,
    Broad,
}

public enum SimilarPhotoProgressStage
{
    FindingPhotos,
    AnalyzingPhotos,
    ComparingSimilarities,
    BuildingGroups,
}

public sealed record SimilarPhotoProgress(
    SimilarPhotoProgressStage Stage,
    string CurrentPath,
    int CompletedItems,
    int? TotalItems,
    int CandidatePairs,
    int FinalComparisons,
    int GroupCount,
    int SkippedItemCount);

/// <summary>A small decoded BGRA image owned by the active analysis only.</summary>
public sealed class PhotoAnalysisImage
{
    private readonly byte[] pixels;

    public PhotoAnalysisImage(int width, int height, byte[] bgraPixels)
    {
        if (width is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(width));
        if (height is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(height));
        ArgumentNullException.ThrowIfNull(bgraPixels);
        if (bgraPixels.Length != checked(width * height * 4)) throw new ArgumentException("Pixel buffer must contain exactly width × height BGRA pixels.", nameof(bgraPixels));
        Width = width;
        Height = height;
        pixels = bgraPixels.ToArray();
    }

    public int Width { get; }
    public int Height { get; }
    public ReadOnlyMemory<byte> Pixels => pixels;
}

public sealed record PhotoDecodeOutcome(PhotoAnalysisImage? Image, SimilarPhotoSkipReason? FailureReason)
{
    public bool Succeeded => Image is not null && FailureReason is null;
    public static PhotoDecodeOutcome Success(PhotoAnalysisImage image) => new(image, null);
    public static PhotoDecodeOutcome Failure(SimilarPhotoSkipReason reason) => new(null, reason);
}

/// <summary>Read-only platform boundary for bounded local image decoding.</summary>
public interface ISimilarPhotoDecoder
{
    Task<PhotoDecodeOutcome> DecodeAsync(DiscoveredFile file, int maximumDimension, CancellationToken cancellationToken = default);
}

public sealed record SimilarityEvidence(
    double StructuralSimilarity,
    double DifferenceHashSimilarity,
    double AverageHashSimilarity,
    double ColorSimilarity,
    double AspectSimilarity,
    double CompositeStrength);

public sealed record SimilarPhotoRelationship(
    DiscoveredFile First,
    DiscoveredFile Second,
    SimilarityTier Tier,
    SimilarityEvidence Evidence);

public sealed record SimilarPhotoGroup(
    DiscoveredFile Representative,
    IReadOnlyList<DiscoveredFile> Photos,
    SimilarityTier Tier);

public sealed record SimilarPhotoSkippedItem(DiscoveredFile File, SimilarPhotoSkipReason Reason);

public sealed record SimilarPhotoAnalysisResult(
    IReadOnlyList<SimilarPhotoGroup> Groups,
    IReadOnlyList<SimilarPhotoRelationship> Relationships,
    IReadOnlyList<SimilarPhotoSkippedItem> SkippedItems,
    int EligiblePhotoCount,
    int CandidatePairCount,
    int FinalComparisonCount,
    bool WasCancelled);

public sealed record SimilarPhotoThresholds(
    double MinimumCompositeStrength,
    double MinimumStructuralSimilarity,
    double MinimumDifferenceHashSimilarity,
    double MinimumAspectSimilarity)
{
    public static SimilarPhotoThresholds For(SimilarPhotoSensitivity sensitivity) => sensitivity switch
    {
        SimilarPhotoSensitivity.Strict => new(0.92, 0.88, 0.78, 0.90),
        SimilarPhotoSensitivity.Balanced => new(0.84, 0.76, 0.66, 0.82),
        SimilarPhotoSensitivity.Broad => new(0.76, 0.64, 0.56, 0.72),
        _ => throw new ArgumentOutOfRangeException(nameof(sensitivity)),
    };
}
