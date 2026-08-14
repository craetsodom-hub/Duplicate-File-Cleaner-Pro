using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.Scanning;

namespace DuplicateFileCleanerPro.Core.Cleanup;

/// <summary>Executes one immutable cleanup plan at a time and never authorizes permanent deletion.</summary>
public sealed class CleanupEngine(ICleanupPlatformService platform, SafetyOperationCoordinator? operationCoordinator = null)
{
    private int executionActive;

    public bool IsRunning => Volatile.Read(ref executionActive) != 0;

    public async Task<CleanupResult> ExecuteAsync(
        CleanupPlan plan,
        IProgress<CleanupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (Interlocked.CompareExchange(ref executionActive, 1, 0) != 0)
        {
            throw new InvalidOperationException("A cleanup operation is already running.");
        }

        IDisposable? operationLease = null;
        try
        {
            operationLease = operationCoordinator?.Acquire(SafetyOperationKind.Cleanup);
            return await Task.Run(async () =>
            {
                ValidatePlanForExecution(plan);
                var groupResults = new List<CleanupGroupResult>(plan.Groups.Count);
                int processed = 0;
                int recycled = 0;
                int skipped = 0;
                int failed = 0;
                long reclaimed = 0;
                bool cancelled = false;

                foreach (CleanupPlanGroup group in plan.Groups)
                {
                    var outcomes = new List<CleanupCandidateOutcome>(group.Candidates.Count);
                    var lastVerifiedKeepers = new HashSet<PhysicalFileIdentity>();
                    for (int candidateIndex = 0; candidateIndex < group.Candidates.Count; candidateIndex++)
                    {
                        CleanupPlanMember candidate = group.Candidates[candidateIndex];
                        if (cancellationToken.IsCancellationRequested)
                        {
                            cancelled = true;
                            AppendCancelled(group.Candidates.Skip(candidateIndex), outcomes, ref processed, ref skipped);
                            Report(progress, new CleanupProgress(processed, plan.RequestedCandidateCount, recycled, skipped, failed, reclaimed));
                            break;
                        }

                        var validKeepers = new List<CleanupPlanMember>(group.Keepers.Count);
                        lastVerifiedKeepers.Clear();
                        foreach (CleanupPlanMember keeper in group.Keepers)
                        {
                            CleanupFileValidation validation;
                            try
                            {
                                validation = await platform.ValidateAsync(keeper, cancellationToken).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                cancelled = true;
                                break;
                            }
                            catch (Exception)
                            {
                                continue;
                            }

                            if (validation.IsValid)
                            {
                                validKeepers.Add(keeper);
                                lastVerifiedKeepers.Add(keeper.ExpectedFile.PhysicalIdentity);
                            }
                        }

                        if (cancelled)
                        {
                            AppendCancelled(group.Candidates.Skip(candidateIndex), outcomes, ref processed, ref skipped);
                            Report(progress, new CleanupProgress(processed, plan.RequestedCandidateCount, recycled, skipped, failed, reclaimed));
                            break;
                        }

                        if (validKeepers.Count == 0)
                        {
                            AddOutcome(new(candidate, CleanupCandidateOutcomeStatus.SkippedKeeperUnavailable), outcomes, ref processed, ref recycled, ref skipped, ref failed, ref reclaimed);
                            Report(progress, new CleanupProgress(processed, plan.RequestedCandidateCount, recycled, skipped, failed, reclaimed));
                            continue;
                        }

                        CleanupRecycleAttempt? attempt = null;
                        bool sawKeeperFailure = false;
                        foreach (CleanupPlanMember keeper in validKeepers)
                        {
                            try
                            {
                                attempt = await platform.RevalidateAndRecycleAsync(candidate, keeper, cancellationToken).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                cancelled = true;
                                break;
                            }
                            catch (Exception)
                            {
                                attempt = null;
                                break;
                            }

                            if (IsKeeperFailure(attempt.Status))
                            {
                                sawKeeperFailure = true;
                                lastVerifiedKeepers.Remove(keeper.ExpectedFile.PhysicalIdentity);
                                continue;
                            }

                            break;
                        }

                        if (cancelled)
                        {
                            AppendCancelled(group.Candidates.Skip(candidateIndex), outcomes, ref processed, ref skipped);
                            Report(progress, new CleanupProgress(processed, plan.RequestedCandidateCount, recycled, skipped, failed, reclaimed));
                            break;
                        }

                        CleanupCandidateOutcome outcome = attempt is null
                            ? new(candidate, CleanupCandidateOutcomeStatus.FailedPlatform)
                            : MapAttempt(candidate, attempt, sawKeeperFailure);
                        AddOutcome(outcome, outcomes, ref processed, ref recycled, ref skipped, ref failed, ref reclaimed);
                        Report(progress, new CleanupProgress(processed, plan.RequestedCandidateCount, recycled, skipped, failed, reclaimed));
                    }

                    groupResults.Add(new CleanupGroupResult(group.GroupIndex, Array.AsReadOnly(outcomes.ToArray()), lastVerifiedKeepers.Count));
                    if (cancelled)
                    {
                        foreach (CleanupPlanGroup remainingGroup in plan.Groups.Skip(groupResults.Count))
                        {
                            var remainingOutcomes = new List<CleanupCandidateOutcome>(remainingGroup.Candidates.Count);
                            AppendCancelled(remainingGroup.Candidates, remainingOutcomes, ref processed, ref skipped);
                            groupResults.Add(new CleanupGroupResult(remainingGroup.GroupIndex, Array.AsReadOnly(remainingOutcomes.ToArray()), 0));
                        }

                        break;
                    }
                }

                return new CleanupResult(Array.AsReadOnly(groupResults.ToArray()), cancelled);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            operationLease?.Dispose();
            Volatile.Write(ref executionActive, 0);
        }
    }

    private static void ValidatePlanForExecution(CleanupPlan plan)
    {
        var identities = new HashSet<PhysicalFileIdentity>();
        long maximumReclaimedBytes = 0;
        foreach (CleanupPlanGroup group in plan.Groups)
        {
            if (group.Members.Count < 2 || group.Keepers.Count == 0 || group.Candidates.Count == 0 || group.Candidates.Count >= group.Members.Count)
            {
                throw new ArgumentException("Cleanup plan violates the independent-survivor invariant.", nameof(plan));
            }

            var groupIds = group.Members.Select(member => member.ExpectedFile.PhysicalIdentity).ToHashSet();
            var candidateIds = group.Candidates.Select(candidate => candidate.ExpectedFile.PhysicalIdentity).ToHashSet();
            var keeperIds = group.Keepers.Select(keeper => keeper.ExpectedFile.PhysicalIdentity).ToHashSet();
            var membersByIdentity = group.Members.ToDictionary(member => member.ExpectedFile.PhysicalIdentity);
            if (groupIds.Count != group.Members.Count
                || candidateIds.Count != group.Candidates.Count
                || keeperIds.Count != group.Keepers.Count
                || group.Candidates.Any(candidate => !membersByIdentity.TryGetValue(candidate.ExpectedFile.PhysicalIdentity, out CleanupPlanMember? member)
                    || candidate.ExpectedFile != member.ExpectedFile)
                || group.Keepers.Any(keeper => !membersByIdentity.TryGetValue(keeper.ExpectedFile.PhysicalIdentity, out CleanupPlanMember? member)
                    || keeper.ExpectedFile != member.ExpectedFile)
                || candidateIds.Intersect(keeperIds).Any()
                || !candidateIds.Union(keeperIds).ToHashSet().SetEquals(groupIds))
            {
                throw new ArgumentException("Cleanup plan membership is malformed.", nameof(plan));
            }

            if (group.GroupIndex < 0 || group.GroupIndex >= plan.VerifiedResult.Detection.Groups.Count)
            {
                throw new ArgumentException("Cleanup plan references an invalid verified group.", nameof(plan));
            }

            IReadOnlyList<DiscoveredFile> snapshotFiles = plan.VerifiedResult.Detection.Groups[group.GroupIndex].Files;
            Dictionary<PhysicalFileIdentity, DiscoveredFile> snapshotByIdentity;
            try
            {
                snapshotByIdentity = snapshotFiles.ToDictionary(file => file.PhysicalIdentity);
            }
            catch (ArgumentException)
            {
                throw new ArgumentException("The originating verified group contains duplicate physical identities.", nameof(plan));
            }

            if (snapshotFiles.Count != group.Members.Count
                || !snapshotByIdentity.Keys.ToHashSet().SetEquals(groupIds)
                || group.Members.Any(member => !snapshotByIdentity.TryGetValue(member.ExpectedFile.PhysicalIdentity, out DiscoveredFile? snapshot)
                    || member.ExpectedFile != snapshot))
            {
                throw new ArgumentException("Cleanup plan does not match its originating verified group.", nameof(plan));
            }

            foreach (PhysicalFileIdentity identity in groupIds)
            {
                if (!identities.Add(identity))
                {
                    throw new ArgumentException("A physical identity overlaps cleanup groups.", nameof(plan));
                }
            }

            maximumReclaimedBytes = group.Candidates.Aggregate(
                maximumReclaimedBytes,
                (total, candidate) => checked(total + candidate.ExpectedFile.Length));
        }
    }

    private static CleanupCandidateOutcome MapAttempt(CleanupPlanMember candidate, CleanupRecycleAttempt attempt, bool sawKeeperFailure) =>
        attempt.Status switch
        {
            CleanupRecycleAttemptStatus.Recycled => new(candidate, CleanupCandidateOutcomeStatus.Recycled),
            CleanupRecycleAttemptStatus.CandidateMissing => new(candidate, CleanupCandidateOutcomeStatus.SkippedMissing),
            CleanupRecycleAttemptStatus.CandidateIdentityMismatch => new(candidate, CleanupCandidateOutcomeStatus.SkippedIdentityMismatch),
            CleanupRecycleAttemptStatus.CandidateChanged => new(candidate, CleanupCandidateOutcomeStatus.SkippedChanged),
            CleanupRecycleAttemptStatus.CandidatePolicyRejected => new(candidate, CleanupCandidateOutcomeStatus.SkippedPolicy),
            CleanupRecycleAttemptStatus.CandidateUnavailable => new(candidate, CleanupCandidateOutcomeStatus.SkippedVerificationFailed),
            CleanupRecycleAttemptStatus.ContentMismatch or CleanupRecycleAttemptStatus.VerificationFailed => new(candidate, CleanupCandidateOutcomeStatus.SkippedVerificationFailed),
            CleanupRecycleAttemptStatus.RecycleBinFailed => new(candidate, CleanupCandidateOutcomeStatus.FailedRecycleBin, attempt.NativeErrorCode),
            _ when sawKeeperFailure || IsKeeperFailure(attempt.Status) => new(candidate, CleanupCandidateOutcomeStatus.SkippedKeeperUnavailable),
            _ => new(candidate, CleanupCandidateOutcomeStatus.FailedPlatform),
        };

    private static bool IsKeeperFailure(CleanupRecycleAttemptStatus status) => status is
        CleanupRecycleAttemptStatus.KeeperMissing or
        CleanupRecycleAttemptStatus.KeeperIdentityMismatch or
        CleanupRecycleAttemptStatus.KeeperChanged or
        CleanupRecycleAttemptStatus.KeeperPolicyRejected or
        CleanupRecycleAttemptStatus.KeeperUnavailable;

    private static void AddOutcome(
        CleanupCandidateOutcome outcome,
        List<CleanupCandidateOutcome> outcomes,
        ref int processed,
        ref int recycled,
        ref int skipped,
        ref int failed,
        ref long reclaimed)
    {
        outcomes.Add(outcome);
        processed++;
        if (outcome.Status == CleanupCandidateOutcomeStatus.Recycled)
        {
            recycled++;
            reclaimed = checked(reclaimed + outcome.Candidate.ExpectedFile.Length);
        }
        else if (outcome.Status is CleanupCandidateOutcomeStatus.FailedRecycleBin or CleanupCandidateOutcomeStatus.FailedPlatform)
        {
            failed++;
        }
        else
        {
            skipped++;
        }
    }

    private static void AppendCancelled(
        IEnumerable<CleanupPlanMember> candidates,
        List<CleanupCandidateOutcome> outcomes,
        ref int processed,
        ref int skipped)
    {
        foreach (CleanupPlanMember candidate in candidates)
        {
            outcomes.Add(new CleanupCandidateOutcome(candidate, CleanupCandidateOutcomeStatus.Cancelled));
            processed++;
            skipped++;
        }
    }

    private static void Report(IProgress<CleanupProgress>? progress, CleanupProgress value)
    {
        try
        {
            progress?.Report(value);
        }
        catch (Exception)
        {
            // Progress is observational and cannot alter cleanup execution.
        }
    }
}
