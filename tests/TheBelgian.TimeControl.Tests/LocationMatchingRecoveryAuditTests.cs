using System.Globalization;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Tests;

public sealed class LocationMatchingRecoveryAuditTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [Fact]
    public void SelectRecoveryAuditCases_IncludesAllRecovery_AndCapsControls()
    {
        var pool = new List<RecoveryAuditClassifiedCase>();
        for (var id = 1; id <= 20; id++)
        {
            pool.Add(Classified(id, usedRecovery: true, adaptiveAccepted: false, abstention: false));
        }

        for (var id = 100; id < 140; id++)
        {
            pool.Add(Classified(id, usedRecovery: false, adaptiveAccepted: true, abstention: false));
        }

        for (var id = 200; id < 240; id++)
        {
            pool.Add(Classified(id, usedRecovery: false, adaptiveAccepted: false, abstention: true));
        }

        var selected = LocationMatchingBenchmarkSampling.SelectRecoveryAuditCases(pool);
        var distribution = LocationMatchingBenchmarkSampling.BuildRecoveryAuditDistribution(selected);

        Assert.Equal(20, distribution.RecoveryOnly);
        Assert.Equal(15, distribution.AdaptiveAcceptedControl);
        Assert.Equal(15, distribution.AbstentionControl);
        Assert.Equal(50, distribution.Total);
        Assert.All(
            selected.Where(item => item.UsedRecovery),
            item => Assert.Contains("RecoveryOnly", item.Strata));
    }

    [Fact]
    public void SelectRecoveryAuditCases_TagsWeakOverlapAndProbableDistance()
    {
        var pool = new[]
        {
            Classified(
                1,
                usedRecovery: true,
                adaptiveAccepted: false,
                abstention: false,
                overlapMinutes: 4,
                overlapPercent: 40,
                distanceMeters: 150,
                geocode: "PartialAddress"),
        };

        var selected = LocationMatchingBenchmarkSampling.SelectRecoveryAuditCases(pool);
        Assert.Single(selected);
        Assert.Contains("WeakOverlapRecovery", selected[0].Strata);
        Assert.Contains("ProbableDistanceRecovery", selected[0].Strata);
        Assert.Contains("WeakGeocodeRecovery", selected[0].Strata);
    }

    [Fact]
    public void ValidateLabelFile_RequiresExpectedStopIdForCorrectCandidate()
    {
        var cases = new[]
        {
            AuditCase(10, ["A"]),
        };
        var labels = new[]
        {
            new CalibrationLabelEntry
            {
                PerformanceId = 10,
                Label = "CorrectCandidate",
                ExpectedStopId = null,
                ReviewerConfidence = "High",
                ReviewerNote = null,
            },
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            LocationMatchingRecoveryAuditService.ValidateLabelFile(labels, cases));
        Assert.Contains("ExpectedStopId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BlindMarkdown_OmitsMatcherStatusAndRecoveryReason()
    {
        var set = new RecoveryAuditSetFile
        {
            DatasetRole = "recovery-audit",
            ExportedAt = DateTimeOffset.Parse("2026-07-29T00:00:00Z", Invariant),
            RandomSeed = 1,
            CaseCount = 1,
            Distribution = new RecoveryAuditDistribution
            {
                RecoveryOnly = 1,
                AdaptiveAcceptedControl = 0,
                AbstentionControl = 0,
                WeakOverlapRecovery = 0,
                ProbableDistanceRecovery = 0,
                WeakGeocodeRecovery = 0,
                Total = 1,
            },
            BlindNote = "Blind pack test",
            Cases =
            [
                AuditCase(42, ["stop-1"]) with
                {
                    HybridDecision = "RecoveredProbable",
                    AdaptiveDecision = "Unresolved",
                    UsedRecovery = true,
                    GeocodeQuality = "PartialAddress",
                    Strata = ["RecoveryOnly"],
                },
            ],
        };

        var markdown = typeof(LocationMatchingRecoveryAuditService)
            .GetMethod(
                "ToBlindMarkdown",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [set]) as string;

        Assert.NotNull(markdown);
        Assert.Contains("PerformanceId: `42`", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("RecoveredProbable", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Unresolved", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("RecoveryOnly", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("PartialAddress", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfirmedLocationMatch", markdown, StringComparison.Ordinal);
    }

    private static RecoveryAuditClassifiedCase Classified(
        long id,
        bool usedRecovery,
        bool adaptiveAccepted,
        bool abstention,
        int overlapMinutes = 30,
        double overlapPercent = 80,
        double? distanceMeters = 50,
        string geocode = "PreciseBuilding") =>
        new(
            id,
            Source(id),
            usedRecovery,
            adaptiveAccepted,
            abstention,
            adaptiveAccepted ? "Confirmed" : "Unresolved",
            usedRecovery ? "RecoveredProbable" : abstention ? "Unresolved" : "Confirmed",
            "stop-1",
            ["stop-1"],
            distanceMeters,
            overlapMinutes,
            overlapPercent,
            distanceMeters is > 100 and <= 250 ? "Probable101To250" : "Strong0To100",
            geocode,
            []);

    private static RecoveryAuditCase AuditCase(long id, string[] stopIds) =>
        new()
        {
            PerformanceId = id,
            Technician = "Tech",
            Date = new DateOnly(2026, 7, 1),
            Start = DateTimeOffset.Parse("2026-07-01T08:00:00+02:00", Invariant),
            End = DateTimeOffset.Parse("2026-07-01T09:00:00+02:00", Invariant),
            PlenionAddress = "Teststraat 1",
            PreviousPerformance = null,
            NextPerformance = null,
            Candidates = stopIds.Select(stopId => new LocationMatchingBenchmarkCandidate
            {
                StopId = stopId,
                Address = "Stop",
                DistanceMeters = 40,
                Arrival = DateTimeOffset.Parse("2026-07-01T08:00:00+02:00", Invariant),
                Departure = DateTimeOffset.Parse("2026-07-01T09:00:00+02:00", Invariant),
                OverlapMinutes = 60,
                StartDifferenceMinutes = 0,
                EndDifferenceMinutes = 0,
                ExistingCandidateStatus = "NoReliableMatch",
                ExistingCandidateScore = 0,
                Explanation = "hidden",
            }).ToArray(),
            Strata = [],
            AdaptiveDecision = "Unresolved",
            HybridDecision = "Unresolved",
            UsedRecovery = false,
        };

    private static LocationMatchingBenchmarkCase Source(long id) =>
        new()
        {
            PerformanceId = id,
            Technician = "Tech",
            Date = new DateOnly(2026, 7, 1),
            Start = DateTimeOffset.Parse("2026-07-01T08:00:00+02:00", Invariant),
            End = DateTimeOffset.Parse("2026-07-01T09:00:00+02:00", Invariant),
            PlenionAddress = "Teststraat 1",
            GeocodeQuality = GeocodeQualityClass.PartialAddress,
            ExistingMatchStatus = "NoReliableMatch",
            Candidates =
            [
                new LocationMatchingBenchmarkCandidate
                {
                    StopId = "stop-1",
                    Address = "Stop",
                    DistanceMeters = 40,
                    Arrival = DateTimeOffset.Parse("2026-07-01T08:00:00+02:00", Invariant),
                    Departure = DateTimeOffset.Parse("2026-07-01T09:00:00+02:00", Invariant),
                    OverlapMinutes = 60,
                    StartDifferenceMinutes = 0,
                    EndDifferenceMinutes = 0,
                    ExistingCandidateStatus = "NoReliableMatch",
                    ExistingCandidateScore = 0,
                    Explanation = "hidden",
                },
            ],
        };
}
