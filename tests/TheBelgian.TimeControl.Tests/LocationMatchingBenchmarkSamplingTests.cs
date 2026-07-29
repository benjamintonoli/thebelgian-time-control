using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Tests;

public sealed class LocationMatchingBenchmarkSamplingTests
{
    [Fact]
    public void SelectDevelopmentCases_KeepsJulyCoreAndCapsAt200()
    {
        var july = Enumerable.Range(1, 83)
            .Select(id => Case(id, "Filip Dekuyper", new DateOnly(2026, 7, 10), "SeenLocation", 1, 50))
            .ToList();
        var pool = Enumerable.Range(1000, 250)
            .Select(id => Case(
                id,
                TechnicianNames[id % TechnicianNames.Length],
                new DateOnly(2026, 5, 1 + (id % 20)),
                id % 2 == 0 ? "SeenLocation" : "UnseenLocation",
                id % 3,
                (id % 4) switch
                {
                    0 => 40,
                    1 => 180,
                    2 => 320,
                    _ => 600,
                }))
            .ToList();

        var selected = LocationMatchingBenchmarkSampling.SelectDevelopmentCases(july, pool);

        Assert.Equal(200, selected.Count);
        Assert.All(july, item => Assert.Contains(selected, selectedItem => selectedItem.PerformanceId == item.PerformanceId));
        Assert.All(selected, item => Assert.Equal("development", item.DatasetRole));
    }

    [Fact]
    public void SelectHoldoutCases_RespectsCapAndSeed()
    {
        var pool = new List<LocationMatchingBenchmarkCase>();
        for (var id = 1; id <= 800; id++)
        {
            pool.Add(Case(
                id,
                TechnicianNames[id % TechnicianNames.Length],
                new DateOnly(2026, 5 + (id % 3), 1 + (id % 25)),
                id % 2 == 0 ? "SeenLocation" : "UnseenLocation",
                id % 3,
                100 + (id % 400),
                lacleunik: $"L{id % 150}"));
        }

        var (first, firstManifest) = LocationMatchingBenchmarkSampling.SelectHoldoutCases(
            pool,
            new HashSet<long>(),
            ["2026-05", "2026-06", "2026-07"]);
        var (second, secondManifest) = LocationMatchingBenchmarkSampling.SelectHoldoutCases(
            pool,
            new HashSet<long>(),
            ["2026-05", "2026-06", "2026-07"]);

        Assert.Equal(300, first.Count);
        Assert.True(firstManifest.Locked);
        Assert.Equal(firstManifest.RandomSeed, secondManifest.RandomSeed);
        Assert.Equal(
            first.Select(item => item.PerformanceId),
            second.Select(item => item.PerformanceId));
        Assert.True(
            first.Select(item => item.Lacleunik)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .Count() >= 100);
        Assert.All(
            first.GroupBy(item => item.Lacleunik),
            group => Assert.True(group.Count() <= 8));
    }

    [Fact]
    public void SelectChallengeCases_OnlyHardCasesWithinBounds()
    {
        var easy = Case(1, "Filip Dekuyper", new DateOnly(2026, 6, 1), "SeenLocation", 1, 40);
        var hard = Case(2, "Filip Dekuyper", new DateOnly(2026, 6, 2), "UnseenLocation", 3, 400) with
        {
            ExistingMatchStatus = "NoReliableMatch",
        };
        var selected = LocationMatchingBenchmarkSampling.SelectChallengeCases(
            [easy, hard],
            new HashSet<long>());

        Assert.DoesNotContain(selected, item => item.PerformanceId == 1);
        Assert.Contains(selected, item => item.PerformanceId == 2);
    }

    [Fact]
    public void Evaluation_ReturnsNull_WhenLabelsMissing()
    {
        var cases = new[]
        {
            Case(1, "Filip Dekuyper", new DateOnly(2026, 7, 1), "SeenLocation", 1, 40),
        };
        var metrics = LocationMatchingBenchmarkEvaluation.TryCompute(cases, _ => "Confirmed");
        Assert.Null(metrics);
        var scaffold = LocationMatchingBenchmarkEvaluation.Prepare(cases);
        Assert.False(scaffold.LabelsPresent);
        Assert.Contains("precision", scaffold.PreparedMetrics);
        Assert.Contains("Wilson 95% CI", scaffold.PreparedMetrics);
        Assert.Contains("risk-coverage curve", scaffold.PreparedMetrics);
    }

    [Fact]
    public void SelectCalibrationCases_ReturnsThirtyWithSecondReview()
    {
        var pool = Enumerable.Range(1, 120)
            .Select(id => Case(
                id,
                TechnicianNames[id % TechnicianNames.Length],
                new DateOnly(2026, 6, 1 + (id % 20)),
                id % 2 == 0 ? "SeenLocation" : "UnseenLocation",
                id % 3,
                (id % 4) switch
                {
                    0 => 40,
                    1 => 180,
                    2 => 320,
                    _ => 600,
                }))
            .ToList();

        var selected = LocationMatchingBenchmarkSampling.SelectCalibrationCases(pool);

        Assert.Equal(30, selected.Count);
        Assert.All(selected, item => Assert.True(item.IsCalibrationCase));
        Assert.All(selected, item => Assert.True(item.RequiresSecondReview));
    }

    [Fact]
    public void ComputeLabelAgreement_ReportsKappaAndConflicts()
    {
        var cases = new[]
        {
            Case(1, "Filip Dekuyper", new DateOnly(2026, 7, 1), "SeenLocation", 1, 40) with
            {
                Label = "CorrectCandidate",
                ExpectedStopId = "S1-0",
                SecondReviewLabel = "CorrectCandidate",
                SecondReviewExpectedStopId = "S1-0",
            },
            Case(2, "Filip Dekuyper", new DateOnly(2026, 7, 2), "UnseenLocation", 0, 400) with
            {
                Label = "NoValidCandidate",
                ExpectedStopId = null,
                SecondReviewLabel = "Ambiguous",
                SecondReviewExpectedStopId = null,
            },
            Case(3, "Filip Dekuyper", new DateOnly(2026, 7, 3), "SeenLocation", 2, 120) with
            {
                Label = "Ambiguous",
                ExpectedStopId = null,
                SecondReviewLabel = "Ambiguous",
                SecondReviewExpectedStopId = null,
            },
        };

        var agreement = LocationMatchingBenchmarkSampling.ComputeLabelAgreement(cases);

        Assert.Equal(3, agreement.DoubleLabeledCount);
        Assert.Equal(2, agreement.ExactLabelAgreementCount);
        Assert.Equal(1, agreement.ConflictCount);
        Assert.Equal(3, agreement.ExpectedStopIdAgreementCount);
        Assert.InRange(agreement.CohensKappa, -1, 1);
    }

    [Fact]
    public void AuditLeakage_DetectsMayJuneHistoricalOverlap()
    {
        var development = new[]
        {
            Case(1, "Filip Dekuyper", new DateOnly(2026, 7, 1), "SeenLocation", 1, 40),
        };
        var holdout = new[]
        {
            Case(2, "Filip Dekuyper", new DateOnly(2026, 5, 10), "UnseenLocation", 1, 40),
            Case(3, "Filip Dekuyper", new DateOnly(2026, 6, 10), "UnseenLocation", 1, 40),
        };
        var audit = LocationMatchingBenchmarkSampling.AuditLeakage(
            development,
            holdout,
            [],
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 6, 30));

        Assert.True(audit.MayOrJuneUsedAsBothHistoricalAndHoldout);
        Assert.Equal(2, audit.HoldoutInMayJun2026Count);
        Assert.Equal(2, audit.HoldoutInHistoricalLearningWindowCount);
    }

    private static readonly string[] TechnicianNames =
    [
        "Filip Dekuyper",
        "Jonas Deklerck",
        "Jasper De Smet",
        "Jarno Vergauwen",
        "Dimitri Stiers",
    ];

    private static LocationMatchingBenchmarkCase Case(
        long id,
        string technician,
        DateOnly date,
        string exposure,
        int candidateCount,
        double distance,
        string? lacleunik = null)
    {
        var candidates = Enumerable.Range(0, candidateCount)
            .Select(index => new LocationMatchingBenchmarkCandidate
            {
                StopId = $"S{id}-{index}",
                Address = $"Stop {index}",
                DistanceMeters = distance + index,
                Arrival = date.ToDateTime(new TimeOnly(9, 0)).ToUniversalTime(),
                Departure = date.ToDateTime(new TimeOnly(10, 0)).ToUniversalTime(),
                OverlapMinutes = 30,
                StartDifferenceMinutes = 5,
                EndDifferenceMinutes = 5,
                ExistingCandidateStatus = "Possible",
                ExistingCandidateScore = 10 - index,
                Explanation = "test",
            })
            .ToArray();
        return new LocationMatchingBenchmarkCase
        {
            PerformanceId = id,
            Technician = technician,
            Date = date,
            Start = date.ToDateTime(new TimeOnly(9, 0)).ToUniversalTime(),
            End = date.ToDateTime(new TimeOnly(11, 0)).ToUniversalTime(),
            Lacleunik = lacleunik ?? $"LOC{id}",
            PlenionAddress = "Teststraat 1",
            GeocodeQuality = GeocodeQualityClass.PartialAddress,
            ExistingMatchStatus = "ProbableLocationMatch",
            ActivityType = "CustomerWork",
            LocationExposure = exposure,
            Candidates = candidates,
        };
    }
}
