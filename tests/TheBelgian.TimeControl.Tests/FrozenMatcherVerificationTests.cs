using System.Globalization;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Services;
using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Tests;

public sealed class FrozenMatcherVerificationTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [Fact]
    public void VisitFragments_SameLocation_MergeIntoOneVisit()
    {
        var options = new AdaptiveLocationMatchingOptions();
        var visits = VisitCandidateBuilder.Build(
            [
                Stop("a", 51.05m, 3.72m, 8, 0, 11, 30),
                Stop("b", 51.0501m, 3.7201m, 11, 40, 15, 0),
            ],
            options,
            new HaversineDistanceCalculator());

        Assert.Single(visits);
        Assert.Equal(2, visits[0].ConstituentStopIds.Count);
    }

    [Fact]
    public void VisitFragments_LargeTimeGap_AreNotMerged()
    {
        var options = new AdaptiveLocationMatchingOptions();
        var visits = VisitCandidateBuilder.Build(
            [
                Stop("a", 51.05m, 3.72m, 8, 0, 9, 0),
                Stop("b", 51.05m, 3.72m, 9, 20, 10, 0),
            ],
            options,
            new HaversineDistanceCalculator());

        Assert.Equal(2, visits.Count);
    }

    [Fact]
    public void WeakOverlap_DoesNotCreateRecoveryMatch()
    {
        var options = new AdaptiveLocationMatchingOptions();
        var item = Case(
            performanceId: 1,
            start: "2026-07-09T14:40:00+02:00",
            end: "2026-07-09T14:45:00+02:00",
            existingStatus: "NoReliableMatch",
            candidates:
            [
                Candidate(
                    "weak",
                    "2026-07-09T14:43:00+02:00",
                    "2026-07-09T15:44:00+02:00",
                    distanceMeters: 50,
                    overlapMinutes: 2),
            ]);

        var prediction = OfflineHybridPredictor.Predict(item, options, recovery: true);
        Assert.False(prediction.Accepted);
        Assert.False(prediction.UsedRecovery);
    }

    [Fact]
    public void StopStartingAfterPerformanceEnd_IsRejected()
    {
        var options = new AdaptiveLocationMatchingOptions();
        var item = Case(
            performanceId: 2,
            start: "2026-07-23T13:10:00+02:00",
            end: "2026-07-23T14:15:00+02:00",
            existingStatus: "NoReliableMatch",
            candidates:
            [
                Candidate(
                    "late",
                    "2026-07-23T14:18:00+02:00",
                    "2026-07-23T14:37:00+02:00",
                    distanceMeters: 118,
                    overlapMinutes: 0),
            ]);

        var prediction = OfflineHybridPredictor.Predict(item, options, recovery: true);
        Assert.False(prediction.Accepted);
    }

    [Fact]
    public void ExistingMatchStatus_CannotBypassPostEndValidation()
    {
        var options = new AdaptiveLocationMatchingOptions();
        var item = Case(
            performanceId: 3,
            start: "2026-07-23T13:10:00+02:00",
            end: "2026-07-23T14:15:00+02:00",
            existingStatus: "ProbableLocationMatch",
            candidates:
            [
                Candidate(
                    "late",
                    "2026-07-23T14:18:00+02:00",
                    "2026-07-23T14:37:00+02:00",
                    distanceMeters: 118,
                    overlapMinutes: 0),
            ]);

        var prediction = OfflineHybridPredictor.Predict(item, options, recovery: true);
        Assert.False(prediction.Accepted);
        Assert.Equal("Unresolved", prediction.Decision);
    }

    [Fact]
    public void OfflineOnlyGuard_BlocksLiveProviderAccess()
    {
        using var scope = OfflineOnlyGuard.Enter();
        var ex = Assert.Throws<InvalidOperationException>(
            () => OfflineOnlyGuard.EnsureLiveAccessAllowed("PlenionODBC"));
        Assert.Contains("PlenionODBC", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FrozenVerification_Fails_WhenConfigurationHashCriteriaBroken()
    {
        var good = new FrozenMatcherMetricSlice
        {
            CaseCount = 30,
            AcceptedMatches = 10,
            CorrectAcceptedMatches = 10,
            Precision = 1,
            Coverage = 0.3333,
            FalsePositives = 0,
            FalseNegatives = 3,
            WrongVisitCandidateChoices = 0,
        };
        Assert.True(FrozenMatcherVerificationService.MeetsFrozenCalibrationCriteria(good));

        var bad = good with { Precision = 0.9, FalsePositives = 1 };
        Assert.False(FrozenMatcherVerificationService.MeetsFrozenCalibrationCriteria(bad));

        var options = new AdaptiveLocationMatchingOptions();
        var hashA = FrozenMatcherVerificationService.ComputeConfigurationHash(
            FrozenMatcherVerificationService.SnapshotOptions(options));
        var hashB = FrozenMatcherVerificationService.ComputeConfigurationHash(
            FrozenMatcherVerificationService.SnapshotOptions(
                new AdaptiveLocationMatchingOptions { RecoveryMinimumOverlapMinutes = 99 }));
        Assert.NotEqual(hashA, hashB);
    }

    [Fact]
    public void FrozenVerification_Fails_OnMissingLocalInputs()
    {
        var temp = Path.Combine(Path.GetTempPath(), "frozen-matcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var result = FrozenMatcherVerificationService.Verify(temp, gitCommit: "test");
            Assert.False(result.Passed);
            Assert.Equal(1, result.ExitCode);
            Assert.Contains(
                result.Failures,
                failure => failure.Contains("Ontbrekende", StringComparison.OrdinalIgnoreCase) ||
                           failure.Contains("ontbreekt", StringComparison.OrdinalIgnoreCase) ||
                           failure.Contains("input", StringComparison.OrdinalIgnoreCase));
            Assert.False(result.ExternalDataAccessed);
            Assert.False(result.HoldoutOpened);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static PilotStop Stop(
        string id,
        decimal lat,
        decimal lon,
        int startHour,
        int startMinute,
        int endHour,
        int endMinute) =>
        new(
            id,
            new DateOnly(2026, 7, 23),
            id + "-in",
            id + "-out",
            DateTimeOffset.Parse(
                $"2026-07-23T{startHour:00}:{startMinute:00}:00+02:00",
                Invariant),
            DateTimeOffset.Parse(
                $"2026-07-23T{endHour:00}:{endMinute:00}:00+02:00",
                Invariant),
            Math.Max(1, (endHour * 60 + endMinute) - (startHour * 60 + startMinute)),
            "Stop " + id,
            null,
            null,
            null,
            null,
            null,
            lat,
            lon,
            null,
            "1",
            "Tech",
            true,
            "ok");

    private static LocationMatchingBenchmarkCase Case(
        long performanceId,
        string start,
        string end,
        string existingStatus,
        IReadOnlyList<LocationMatchingBenchmarkCandidate> candidates) =>
        new()
        {
            PerformanceId = performanceId,
            Technician = "Tech",
            Date = DateOnly.FromDateTime(DateTimeOffset.Parse(start, Invariant).DateTime),
            Start = DateTimeOffset.Parse(start, Invariant),
            End = DateTimeOffset.Parse(end, Invariant),
            Lacleunik = "1",
            PlenionAddress = "Teststraat 1, 9000 Gent",
            GeocodeQuality = GeocodeQualityClass.PartialAddress,
            ExistingMatchStatus = existingStatus,
            Candidates = candidates,
            Label = "NoValidCandidate",
        };

    private static LocationMatchingBenchmarkCandidate Candidate(
        string stopId,
        string arrival,
        string departure,
        double distanceMeters,
        int overlapMinutes) =>
        new()
        {
            StopId = stopId,
            Address = "Stop",
            Arrival = DateTimeOffset.Parse(arrival, Invariant),
            Departure = DateTimeOffset.Parse(departure, Invariant),
            DistanceMeters = distanceMeters,
            OverlapMinutes = overlapMinutes,
            StartDifferenceMinutes = 0,
            EndDifferenceMinutes = 0,
            ExistingCandidateStatus = "Candidate",
            ExistingCandidateScore = 50,
            Explanation = "test",
        };
}
