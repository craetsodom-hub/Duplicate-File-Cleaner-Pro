using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.Scanning;
using DuplicateFileCleanerPro.Core.Similarity;

namespace DuplicateFileCleanerPro.Core.SimilarRemoval;

/// <summary>Executes explicit reviewed intent without treating visual similarity as content equivalence.</summary>
public sealed class SimilarPhotoRemovalEngine(
    ISimilarPhotoRemovalPlatform platform,
    SafetyOperationCoordinator? operationCoordinator = null)
{
    private int executionActive;
    public bool IsRunning => Volatile.Read(ref executionActive) != 0;

    public async Task<SimilarPhotoRemovalResult> ExecuteAsync(
        SimilarPhotoRemovalPlan plan,
        IProgress<SimilarPhotoRemovalProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (Interlocked.CompareExchange(ref executionActive, 1, 0) != 0) throw new InvalidOperationException("A Similar Photos removal operation is already running.");
        IDisposable? lease = null;
        try
        {
            lease = operationCoordinator?.Acquire(SafetyOperationKind.SimilarPhotoRemoval);
            ValidatePlanForExecution(plan);
            var results = new List<SimilarPhotoRemovalGroupResult>(plan.Groups.Count);
            int processed = 0, recycled = 0, skipped = 0, failed = 0;
            long recycledBytes = 0;
            bool cancelled = false;
            foreach (SimilarPhotoRemovalPlanGroup group in plan.Groups)
            {
                var outcomes = new List<SimilarPhotoRemovalOutcome>(group.Candidates.Count);
                int lastValidatedSurvivors = 0;
                for (int index = 0; index < group.Candidates.Count; index++)
                {
                    SimilarPhotoRemovalPlanMember candidate = group.Candidates[index];
                    if (cancellationToken.IsCancellationRequested)
                    {
                        cancelled = true;
                        foreach (SimilarPhotoRemovalPlanMember remaining in group.Candidates.Skip(index))
                        {
                            outcomes.Add(new(remaining, SimilarPhotoRemovalOutcomeStatus.Cancelled));
                            processed++; skipped++;
                        }
                        Report();
                        break;
                    }

                    var validSurvivors = new List<SimilarPhotoRemovalPlanMember>();
                    foreach (SimilarPhotoRemovalPlanMember survivor in group.Survivors)
                    {
                        try
                        {
                            SimilarPhotoRemovalValidation validation = await platform.ValidateAsync(survivor, cancellationToken).ConfigureAwait(false);
                            if (validation.IsValid) validSurvivors.Add(survivor);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            cancelled = true;
                            break;
                        }
                        catch (Exception)
                        {
                        }
                    }

                    lastValidatedSurvivors = validSurvivors.Select(member => member.ExpectedFile.PhysicalIdentity).Distinct().Count();
                    if (cancelled)
                    {
                        foreach (SimilarPhotoRemovalPlanMember remaining in group.Candidates.Skip(index))
                        {
                            outcomes.Add(new(remaining, SimilarPhotoRemovalOutcomeStatus.Cancelled));
                            processed++; skipped++;
                        }
                        Report();
                        break;
                    }

                    SimilarPhotoRemovalOutcome outcome;
                    if (lastValidatedSurvivors < 1)
                    {
                        outcome = new(candidate, SimilarPhotoRemovalOutcomeStatus.SkippedSurvivorUnavailable);
                    }
                    else
                    {
                        try
                        {
                            SimilarPhotoRecycleAttempt attempt = await platform.RevalidateAndRecycleAsync(candidate, validSurvivors, cancellationToken).ConfigureAwait(false);
                            outcome = new(candidate, Map(attempt.Status), attempt.NativeErrorCode);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            cancelled = true;
                            outcome = new(candidate, SimilarPhotoRemovalOutcomeStatus.Cancelled);
                        }
                        catch (Exception)
                        {
                            outcome = new(candidate, SimilarPhotoRemovalOutcomeStatus.FailedPlatform);
                        }
                    }

                    outcomes.Add(outcome);
                    processed++;
                    if (outcome.Status == SimilarPhotoRemovalOutcomeStatus.Recycled)
                    {
                        recycled++;
                        recycledBytes = checked(recycledBytes + candidate.ExpectedFile.Length);
                    }
                    else if (outcome.Status is SimilarPhotoRemovalOutcomeStatus.FailedRecycleBin or SimilarPhotoRemovalOutcomeStatus.FailedPlatform) failed++;
                    else skipped++;
                    Report();
                    if (cancelled) break;
                }

                results.Add(new(group.GroupIndex, Array.AsReadOnly(outcomes.ToArray()), lastValidatedSurvivors));
                if (cancelled)
                {
                    foreach (SimilarPhotoRemovalPlanGroup remainingGroup in plan.Groups.Skip(results.Count))
                    {
                        var remainingOutcomes = new List<SimilarPhotoRemovalOutcome>(remainingGroup.Candidates.Count);
                        foreach (SimilarPhotoRemovalPlanMember remaining in remainingGroup.Candidates)
                        {
                            remainingOutcomes.Add(new(remaining, SimilarPhotoRemovalOutcomeStatus.Cancelled));
                            processed++;
                            skipped++;
                        }

                        results.Add(new(remainingGroup.GroupIndex, Array.AsReadOnly(remainingOutcomes.ToArray()), 0));
                    }

                    Report();
                    break;
                }
            }

            return new(Array.AsReadOnly(results.ToArray()), cancelled);

            void Report()
            {
                try { progress?.Report(new(processed, plan.RequestedPhotoCount, recycled, skipped, failed, recycledBytes)); }
                catch (Exception) { }
            }
        }
        finally
        {
            lease?.Dispose();
            Volatile.Write(ref executionActive, 0);
        }
    }

    private static void ValidatePlanForExecution(SimilarPhotoRemovalPlan plan)
    {
        if (plan.AnalyzedResult.Discovery.WasCancelled || plan.AnalyzedResult.Analysis.WasCancelled)
        {
            throw new ArgumentException("Similar Photos plan cannot originate from a cancelled analysis.", nameof(plan));
        }

        if (plan.Groups.Count == 0)
        {
            throw new ArgumentException("Similar Photos plan must contain at least one selected group.", nameof(plan));
        }

        var identities = new HashSet<PhysicalFileIdentity>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long selectedBytes = 0;

        foreach (SimilarPhotoRemovalPlanGroup group in plan.Groups)
        {
            if (group.GroupIndex < 0 || group.GroupIndex >= plan.AnalyzedResult.Analysis.Groups.Count
                || group.Members.Count < 2
                || group.Candidates.Count == 0
                || group.Survivors.Count == 0
                || group.Candidates.Count + group.Survivors.Count != group.Members.Count)
            {
                throw new ArgumentException("Similar Photos plan violates the independent-survivor invariant.", nameof(plan));
            }

            SimilarPhotoGroup sourceGroup = plan.AnalyzedResult.Analysis.Groups[group.GroupIndex];
            if (sourceGroup.Tier != group.Tier || sourceGroup.Photos.Count != group.Members.Count)
            {
                throw new ArgumentException("Similar Photos plan does not match its analyzed group.", nameof(plan));
            }

            var membersByIdentity = new Dictionary<PhysicalFileIdentity, SimilarPhotoRemovalPlanMember>();
            foreach (SimilarPhotoRemovalPlanMember member in group.Members)
            {
                if (!membersByIdentity.TryAdd(member.ExpectedFile.PhysicalIdentity, member)
                    || !identities.Add(member.ExpectedFile.PhysicalIdentity)
                    || !paths.Add(member.ExpectedFile.NormalizedPath)
                    || !sourceGroup.Photos.Contains(member.ExpectedFile))
                {
                    throw new ArgumentException("Similar Photos plan contains duplicate or mismatched members.", nameof(plan));
                }
            }

            ValidatePartition(group.Candidates, membersByIdentity, "candidate", ref selectedBytes);
            ValidatePartition(group.Survivors, membersByIdentity, "survivor", ref selectedBytes, addBytes: false);

            if (group.Candidates.Any(candidate => group.Survivors.Any(survivor => survivor.ExpectedFile.PhysicalIdentity == candidate.ExpectedFile.PhysicalIdentity)))
            {
                throw new ArgumentException("Similar Photos plan overlaps candidates and survivors.", nameof(plan));
            }

            if (!group.Members.Select(member => member.ExpectedFile.PhysicalIdentity).ToHashSet()
                .SetEquals(group.Candidates.Select(member => member.ExpectedFile.PhysicalIdentity).Concat(group.Survivors.Select(member => member.ExpectedFile.PhysicalIdentity))))
            {
                throw new ArgumentException("Similar Photos plan does not fully partition its group.", nameof(plan));
            }
        }

        if (selectedBytes != plan.SelectedBytes || plan.RequestedPhotoCount != plan.Groups.Sum(group => group.Candidates.Count))
        {
            throw new ArgumentException("Similar Photos plan summary is inconsistent.", nameof(plan));
        }

        static void ValidatePartition(
            IReadOnlyList<SimilarPhotoRemovalPlanMember> partition,
            IReadOnlyDictionary<PhysicalFileIdentity, SimilarPhotoRemovalPlanMember> membersByIdentity,
            string label,
            ref long selectedBytes,
            bool addBytes = true)
        {
            var seen = new HashSet<PhysicalFileIdentity>();
            foreach (SimilarPhotoRemovalPlanMember member in partition)
            {
                if (!membersByIdentity.TryGetValue(member.ExpectedFile.PhysicalIdentity, out SimilarPhotoRemovalPlanMember? expected)
                    || expected.ExpectedFile != member.ExpectedFile
                    || !seen.Add(member.ExpectedFile.PhysicalIdentity))
                {
                    throw new ArgumentException($"Similar Photos plan contains an invalid {label} partition.", nameof(plan));
                }

                if (addBytes) selectedBytes = checked(selectedBytes + member.ExpectedFile.Length);
            }
        }
    }

    private static SimilarPhotoRemovalOutcomeStatus Map(SimilarPhotoRecycleAttemptStatus status) => status switch
    {
        SimilarPhotoRecycleAttemptStatus.Recycled => SimilarPhotoRemovalOutcomeStatus.Recycled,
        SimilarPhotoRecycleAttemptStatus.CandidateMissing => SimilarPhotoRemovalOutcomeStatus.SkippedMissing,
        SimilarPhotoRecycleAttemptStatus.CandidateIdentityMismatch => SimilarPhotoRemovalOutcomeStatus.SkippedIdentityMismatch,
        SimilarPhotoRecycleAttemptStatus.CandidateChanged => SimilarPhotoRemovalOutcomeStatus.SkippedChanged,
        SimilarPhotoRecycleAttemptStatus.CandidatePolicyRejected => SimilarPhotoRemovalOutcomeStatus.SkippedPolicy,
        SimilarPhotoRecycleAttemptStatus.CandidateAmbiguousHardLinks => SimilarPhotoRemovalOutcomeStatus.SkippedAmbiguousHardLinks,
        SimilarPhotoRecycleAttemptStatus.SurvivorUnavailable => SimilarPhotoRemovalOutcomeStatus.SkippedSurvivorUnavailable,
        SimilarPhotoRecycleAttemptStatus.RecycleBinFailed => SimilarPhotoRemovalOutcomeStatus.FailedRecycleBin,
        _ => SimilarPhotoRemovalOutcomeStatus.FailedPlatform,
    };
}
