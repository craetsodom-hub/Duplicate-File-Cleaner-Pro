using System.Text;
using DuplicateFileCleanerPro.App.Results;
using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.Scanning;
using DuplicateFileCleanerPro.Infrastructure.Windows.Detection;
using DuplicateFileCleanerPro.Infrastructure.Windows.Discovery;

namespace DuplicateFileCleanerPro.IntegrationTests;

[TestClass]
public sealed class ResultsWorkflowIntegrationTests
{
    [TestMethod]
    public async Task RealWindowsWorkflowProducesIndependentVerifiedResultsForReview()
    {
        string root = Path.Combine(Path.GetTempPath(), "DuplicateFileCleanerPro.Phase5.Results", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string firstPayload = "verified content ✓";
            await File.WriteAllTextAsync(Path.Combine(root, "original.txt"), firstPayload, new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(root, "renamed-copy.bin"), firstPayload, new UTF8Encoding(false));
            long expectedReclaimable = Encoding.UTF8.GetByteCount(firstPayload);
            for (int group = 0; group < 48; group++)
            {
                string payload = $"generated review group {group:D2}";
                for (int member = 0; member < 3; member++)
                {
                    string directory = Path.Combine(root, "deep", $"bucket-{group % 7:D2}");
                    Directory.CreateDirectory(directory);
                    await File.WriteAllTextAsync(Path.Combine(directory, $"group-{group:D2}-different-name-{member}.bin"), payload, new UTF8Encoding(false));
                }

                expectedReclaimable += 2L * Encoding.UTF8.GetByteCount(payload);
            }

            string unicodeDirectory = Path.Combine(root, "Unicode", "深い");
            Directory.CreateDirectory(unicodeDirectory);
            const string unicodePayload = "same unicode content ✓";
            await File.WriteAllTextAsync(Path.Combine(unicodeDirectory, "résumé-α.txt"), unicodePayload, new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(unicodeDirectory, "副本-β.bin"), unicodePayload, new UTF8Encoding(false));
            expectedReclaimable += Encoding.UTF8.GetByteCount(unicodePayload);
            for (int unique = 0; unique < 80; unique++)
            {
                await File.WriteAllTextAsync(Path.Combine(root, $"unique-{unique:D2}.txt"), $"unique-{unique:D2}-{Guid.NewGuid():N}", new UTF8Encoding(false));
            }

            await File.WriteAllTextAsync(Path.Combine(root, "same-size-negative-a.bin"), "different same byte!", new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(root, "same-size-negative-b.bin"), "another same byte!", new UTF8Encoding(false));

            using var workflow = new ScanWorkflowController(new ScanSessionService(new WindowsFileDiscoveryService(), new WindowsContentAnalysisService()));
            RootNormalizationResult normalized = new WindowsScanRootNormalizer().Normalize([root]);
            ScanSessionResult scan = await workflow.StartAsync(normalized.Roots, new DiscoveryPolicy());
            Assert.AreEqual(ScanSessionState.Completed, scan.State);
            Assert.IsNotNull(scan.CompletedResult);

            var review = new ResultsReviewViewModel(scan.CompletedResult);
            Assert.AreEqual(50, review.DuplicateGroupCount);
            Assert.AreEqual(148, review.VerifiedMemberCount);
            Assert.AreEqual(expectedReclaimable, review.ReclaimableBytes);
            review.AllGroups[0].Members[0].IsSelected = true;
            review.AllGroups[0].Members[1].IsSelected = true;
            Assert.AreEqual(review.AllGroups[0].Members.Count - 1, review.SelectedCandidateCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
