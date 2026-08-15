namespace DuplicateFileCleanerPro.Core.Similarity;

internal sealed record SimilarPhotoFingerprint(
    ulong DifferenceHash,
    ulong VerticalDifferenceHash,
    ulong AverageHash,
    byte[] NormalizedLuminance,
    ulong CenterDifferenceHash,
    ulong CenterVerticalDifferenceHash,
    ulong CenterAverageHash,
    byte[] CenterNormalizedLuminance,
    ushort[] ColorHistogram,
    double AspectRatio);

internal static class SimilarPhotoFingerprintBuilder
{
    private const int StructureSize = 16;

    public static SimilarPhotoFingerprint Create(PhotoAnalysisImage image, CancellationToken cancellationToken)
    {
        ReadOnlySpan<byte> source = image.Pixels.Span;
        byte[] luminance = new byte[StructureSize * StructureSize];
        ushort[] histogram = new ushort[12];
        for (int y = 0; y < StructureSize; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int sourceY = Math.Min(image.Height - 1, (2 * y + 1) * image.Height / (2 * StructureSize));
            for (int x = 0; x < StructureSize; x++)
            {
                int sourceX = Math.Min(image.Width - 1, (2 * x + 1) * image.Width / (2 * StructureSize));
                int offset = (sourceY * image.Width + sourceX) * 4;
                byte b = source[offset];
                byte g = source[offset + 1];
                byte r = source[offset + 2];
                luminance[y * StructureSize + x] = (byte)((54 * r + 183 * g + 19 * b) >> 8);
                histogram[r >> 6]++;
                histogram[4 + (g >> 6)]++;
                histogram[8 + (b >> 6)]++;
            }
        }

        NormalizeContrast(luminance);
        byte[] centerLuminance = SampleCenterLuminance(image);
        NormalizeContrast(centerLuminance);
        ulong averageHash = AverageHash(luminance);
        ulong differenceHash = DifferenceHash(luminance, vertical: false);
        ulong verticalDifferenceHash = DifferenceHash(luminance, vertical: true);
        return new SimilarPhotoFingerprint(
            differenceHash, verticalDifferenceHash, averageHash, luminance,
            DifferenceHash(centerLuminance, vertical: false), DifferenceHash(centerLuminance, vertical: true), AverageHash(centerLuminance), centerLuminance,
            histogram, (double)image.Width / image.Height);
    }

    private static byte[] SampleCenterLuminance(PhotoAnalysisImage image)
    {
        ReadOnlySpan<byte> source = image.Pixels.Span;
        byte[] result = new byte[StructureSize * StructureSize];
        int insetX = Math.Max(1, image.Width / 16);
        int insetY = Math.Max(1, image.Height / 16);
        int usableWidth = Math.Max(1, image.Width - 2 * insetX);
        int usableHeight = Math.Max(1, image.Height - 2 * insetY);
        for (int y = 0; y < StructureSize; y++)
        for (int x = 0; x < StructureSize; x++)
        {
            int sourceX = Math.Min(image.Width - 1, insetX + (2 * x + 1) * usableWidth / (2 * StructureSize));
            int sourceY = Math.Min(image.Height - 1, insetY + (2 * y + 1) * usableHeight / (2 * StructureSize));
            int offset = (sourceY * image.Width + sourceX) * 4;
            result[y * StructureSize + x] = (byte)((54 * source[offset + 2] + 183 * source[offset + 1] + 19 * source[offset]) >> 8);
        }
        return result;
    }

    private static void NormalizeContrast(byte[] values)
    {
        double mean = values.Average(value => (double)value);
        double variance = values.Average(value => (value - mean) * (value - mean));
        double deviation = Math.Sqrt(variance);
        if (deviation < 1)
        {
            Array.Fill(values, (byte)128);
            return;
        }

        for (int index = 0; index < values.Length; index++)
        {
            double normalized = 128 + ((values[index] - mean) * 42 / deviation);
            values[index] = (byte)Math.Clamp((int)Math.Round(normalized), 0, 255);
        }
    }

    private static ulong AverageHash(byte[] luminance)
    {
        Span<byte> grid = stackalloc byte[64];
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
            grid[y * 8 + x] = luminance[(y * 2) * StructureSize + (x * 2)];
        double average = grid.ToArray().Average(value => (double)value);
        ulong result = 0;
        for (int index = 0; index < grid.Length; index++) if (grid[index] >= average) result |= 1UL << index;
        return result;
    }

    private static ulong DifferenceHash(byte[] luminance, bool vertical)
    {
        ulong result = 0;
        int bit = 0;
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++, bit++)
        {
            byte left = luminance[(y * 2) * StructureSize + x * 2];
            byte right = vertical
                ? luminance[Math.Min(15, y * 2 + 2) * StructureSize + x * 2]
                : luminance[(y * 2) * StructureSize + Math.Min(15, x * 2 + 2)];
            if (left >= right) result |= 1UL << bit;
        }
        return result;
    }
}

internal static class SimilarPhotoComparer
{
    public static SimilarityEvidence Compare(SimilarPhotoFingerprint first, SimilarPhotoFingerprint second)
    {
        double structural = Math.Max(
            LuminanceSimilarity(first.NormalizedLuminance, second.NormalizedLuminance),
            Math.Max(LuminanceSimilarity(first.CenterNormalizedLuminance, second.NormalizedLuminance), LuminanceSimilarity(first.NormalizedLuminance, second.CenterNormalizedLuminance)));
        double differenceHash = Math.Max(
            HashPairSimilarity(first.DifferenceHash, first.VerticalDifferenceHash, second.DifferenceHash, second.VerticalDifferenceHash),
            Math.Max(HashPairSimilarity(first.CenterDifferenceHash, first.CenterVerticalDifferenceHash, second.DifferenceHash, second.VerticalDifferenceHash), HashPairSimilarity(first.DifferenceHash, first.VerticalDifferenceHash, second.CenterDifferenceHash, second.CenterVerticalDifferenceHash)));
        double averageHash = Math.Max(
            HashSimilarity(first.AverageHash, second.AverageHash),
            Math.Max(HashSimilarity(first.CenterAverageHash, second.AverageHash), HashSimilarity(first.AverageHash, second.CenterAverageHash)));
        double histogramDistance = first.ColorHistogram.Zip(second.ColorHistogram, (left, right) => Math.Abs(left - right)).Sum() / (3.0 * 256);
        double color = Math.Clamp(1 - histogramDistance, 0, 1);
        double aspect = Math.Exp(-Math.Abs(Math.Log(first.AspectRatio / second.AspectRatio)));
        double composite = 0.38 * structural + 0.24 * differenceHash + 0.14 * averageHash + 0.14 * color + 0.10 * aspect;
        return new SimilarityEvidence(structural, differenceHash, averageHash, color, aspect, composite);
    }

    private static double LuminanceSimilarity(byte[] first, byte[] second) => 1 - first.Zip(second, (left, right) => Math.Abs(left - right)).Average() / 255.0;
    private static double HashSimilarity(ulong first, ulong second) => 1 - System.Numerics.BitOperations.PopCount(first ^ second) / 64.0;
    private static double HashPairSimilarity(ulong firstHorizontal, ulong firstVertical, ulong secondHorizontal, ulong secondVertical) => (HashSimilarity(firstHorizontal, secondHorizontal) + HashSimilarity(firstVertical, secondVertical)) / 2;

    public static bool Meets(SimilarityEvidence evidence, SimilarPhotoThresholds thresholds) =>
        evidence.CompositeStrength >= thresholds.MinimumCompositeStrength &&
        evidence.StructuralSimilarity >= thresholds.MinimumStructuralSimilarity &&
        evidence.DifferenceHashSimilarity >= thresholds.MinimumDifferenceHashSimilarity &&
        evidence.AspectSimilarity >= thresholds.MinimumAspectSimilarity;

    public static SimilarityTier Tier(double strength) => strength >= 0.93
        ? SimilarityTier.VerySimilar
        : strength >= 0.84 ? SimilarityTier.Similar : SimilarityTier.LooselySimilar;
}
