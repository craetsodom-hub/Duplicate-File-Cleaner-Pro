using DuplicateFileCleanerPro.App.SimilarPhotos;
using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.Scanning;
using DuplicateFileCleanerPro.Core.Similarity;

namespace DuplicateFileCleanerPro.Core.Tests;

[TestClass]
public sealed class SimilarPhotosReviewViewModelTests
{
    [TestMethod]
    public void FiltersSearchSortAndSessionReviewMarksRemainNonDestructive()
    {
        SimilarPhotosReviewViewModel viewModel = CreateViewModel();
        Assert.HasCount(2, viewModel.AllGroups);
        viewModel.TierFilter = SimilarityTier.VerySimilar;
        Assert.HasCount(1, viewModel.VisibleGroups);
        viewModel.SearchText = "beach";
        Assert.HasCount(1, viewModel.VisibleGroups);
        viewModel.SortOption = SimilarPhotoSortOption.PhotoCount;
        SimilarPhotoItemViewModel photo = viewModel.VisibleGroups[0].Photos[0];
        Assert.IsTrue(photo.SetMark(SimilarPhotoReviewMark.Keep));
        Assert.AreEqual(SimilarPhotoReviewMark.Keep, photo.Mark);
        viewModel.ClearMarks();
        Assert.AreEqual(SimilarPhotoReviewMark.None, photo.Mark);
    }

    [TestMethod]
    public void NeverAllowsEveryPhotoInGroupToBeConsideredForRemoval()
    {
        SimilarPhotosReviewViewModel viewModel = CreateViewModel();
        SimilarPhotoGroupViewModel group = viewModel.AllGroups[0];
        Assert.IsTrue(group.Photos[0].SetMark(SimilarPhotoReviewMark.ConsiderRemoving));
        Assert.IsFalse(group.Photos[1].SetMark(SimilarPhotoReviewMark.ConsiderRemoving));
    }

    [TestMethod]
    public void ComparisonClearsWhenNewGroupIsSelected()
    {
        SimilarPhotosReviewViewModel viewModel = CreateViewModel();
        viewModel.SelectGroup(viewModel.AllGroups[0]);
        viewModel.ChooseLeft(viewModel.AllGroups[0].Photos[0]);
        viewModel.ChooseRight(viewModel.AllGroups[0].Photos[1]);
        Assert.IsTrue(viewModel.CanCompare);
        viewModel.SelectGroup(viewModel.AllGroups[1]);
        Assert.IsFalse(viewModel.CanCompare);
    }

    private static SimilarPhotosReviewViewModel CreateViewModel()
    {
        DiscoveredFile beachA = File("C:\\Photos\\beach-a.jpg", 1);
        DiscoveredFile beachB = File("C:\\Photos\\beach-b.jpg", 2);
        DiscoveredFile cityA = File("C:\\Photos\\city-a.jpg", 3);
        DiscoveredFile cityB = File("C:\\Photos\\city-b.jpg", 4);
        SimilarPhotoAnalysisResult analysis = new(
            [new SimilarPhotoGroup(beachA, [beachA, beachB], SimilarityTier.VerySimilar), new SimilarPhotoGroup(cityA, [cityA, cityB], SimilarityTier.Similar)],
            [], [], 4, 2, 2, false);
        return new SimilarPhotosReviewViewModel(new CompletedSimilarPhotoScanResult(new DiscoveryResult([], [], false), analysis, SimilarPhotoSensitivity.Balanced, ["C:\\Photos"]));
    }

    private static DiscoveredFile File(string path, ulong id) => new(path, Path.GetFileName(path), ".jpg", 12, DateTimeOffset.UnixEpoch.AddDays(id), DateTimeOffset.UnixEpoch, new PhysicalFileIdentity(9, id, 0), FileAttributes.Normal);
}
