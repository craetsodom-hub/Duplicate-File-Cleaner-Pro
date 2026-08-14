using System.Security.Cryptography;

namespace DuplicateFileCleanerPro.Phase0.Core;

public sealed record ProbeFile(string Path, long Length, string PhysicalIdentity);

public sealed record ExactDuplicateGroup(IReadOnlyList<ProbeFile> Members);

public sealed class ExactDuplicateProbe
{
    private readonly Func<string, byte[]> _fullHash;

    public ExactDuplicateProbe(Func<string, byte[]>? fullHash = null)
    {
        _fullHash = fullHash ?? ComputeSha256;
    }

    public IReadOnlyList<ExactDuplicateGroup> FindExactGroups(IEnumerable<ProbeFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        return files
            .GroupBy(file => file.Length)
            .Where(group => group.Key > 0 && group.Count() > 1)
            .SelectMany(sizeGroup => sizeGroup.GroupBy(file => Convert.ToHexString(ComputeSampleSignature(file.Path))))
            .Where(sampleGroup => sampleGroup.Count() > 1)
            .SelectMany(sampleGroup => sampleGroup.GroupBy(file => Convert.ToHexString(_fullHash(file.Path))))
            .Where(hashGroup => hashGroup.Count() > 1)
            .SelectMany(VerifyHashGroup)
            .Where(group => group.Members.Count > 1)
            .ToArray();
    }

    private static IEnumerable<ExactDuplicateGroup> VerifyHashGroup(IGrouping<string, ProbeFile> hashGroup)
    {
        var unassigned = hashGroup
            .GroupBy(file => file.PhysicalIdentity, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        while (unassigned.Count > 1)
        {
            var anchor = unassigned[0];
            var verified = new List<ProbeFile> { anchor };
            unassigned.RemoveAt(0);

            for (var index = unassigned.Count - 1; index >= 0; index--)
            {
                if (HaveIdenticalBytes(anchor.Path, unassigned[index].Path))
                {
                    verified.Add(unassigned[index]);
                    unassigned.RemoveAt(index);
                }
            }

            if (verified.Count > 1)
            {
                yield return new ExactDuplicateGroup(verified.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray());
            }
        }
    }

    public static bool HaveIdenticalBytes(string firstPath, string secondPath)
    {
        const int bufferLength = 128 * 1024;
        var first = new byte[bufferLength];
        var second = new byte[bufferLength];

        using var firstStream = new FileStream(firstPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferLength, FileOptions.SequentialScan);
        using var secondStream = new FileStream(secondPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferLength, FileOptions.SequentialScan);
        if (firstStream.Length != secondStream.Length)
        {
            return false;
        }

        while (true)
        {
            var firstRead = firstStream.Read(first);
            var secondRead = secondStream.Read(second);
            if (firstRead != secondRead)
            {
                return false;
            }

            if (firstRead == 0)
            {
                return true;
            }

            if (!first.AsSpan(0, firstRead).SequenceEqual(second.AsSpan(0, secondRead)))
            {
                return false;
            }
        }
    }

    private static byte[] ComputeSampleSignature(string path)
    {
        const int segmentLength = 4 * 1024;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, segmentLength, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var offsets = new[] { 0L, Math.Max(0, (stream.Length - segmentLength) / 2), Math.Max(0, stream.Length - segmentLength) }
            .Distinct();
        var buffer = new byte[segmentLength];

        foreach (var offset in offsets)
        {
            stream.Position = offset;
            var remaining = (int)Math.Min(segmentLength, stream.Length - offset);
            var read = 0;
            while (read < remaining)
            {
                var currentRead = stream.Read(buffer, read, remaining - read);
                if (currentRead == 0)
                {
                    throw new EndOfStreamException("A sample read ended unexpectedly.");
                }

                read += currentRead;
            }

            hash.AppendData(buffer, 0, read);
        }

        return hash.GetHashAndReset();
    }

    private static byte[] ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        return SHA256.HashData(stream);
    }
}
