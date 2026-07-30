using System.Globalization;
using System.Text.Json;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Tests;

public sealed class LockedHoldoutEvaluationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [Theory]
    [InlineData(1.0, 0, false, "GO")]
    [InlineData(0.95, 1, false, "GO")]
    [InlineData(0.94, 0, false, "CONDITIONAL GO")]
    [InlineData(0.90, 0, false, "CONDITIONAL GO")]
    [InlineData(0.89, 0, false, "NO-GO")]
    [InlineData(0.99, 2, false, "NO-GO")]
    [InlineData(0.99, 0, true, "NO-GO")]
    public void Decide_UsesDocumentedThresholds(
        double precision,
        int wrongVisit,
        bool systematicFp,
        string expected)
    {
        Assert.Equal(
            expected,
            LockedHoldoutEvaluationService.Decide(precision, wrongVisit, systematicFp));
    }

    [Fact]
    public void DetectSystematicFalsePositives_RequiresRepeatedCategory()
    {
        Assert.False(
            LockedHoldoutEvaluationService.DetectSystematicFalsePositives(
            [
                Error("FP_NoValidCandidate", 1),
            ]));
        Assert.True(
            LockedHoldoutEvaluationService.DetectSystematicFalsePositives(
            [
                Error("FP_NoValidCandidate", 1),
                Error("FP_NoValidCandidate", 2),
            ]));
        Assert.False(
            LockedHoldoutEvaluationService.DetectSystematicFalsePositives(
            [
                Error("FP_NoValidCandidate", 1),
                Error("FP_Ambiguous", 2),
            ]));
    }

    [Fact]
    public void Evaluate_ComputesMetrics_AndWritesReports()
    {
        var temp = NewTempDocs();
        try
        {
            WriteHoldout(
                temp,
                [
                    LabeledCase(
                        1,
                        "CorrectCandidate",
                        "stop-a",
                        "ConfirmedLocationMatch",
                        Candidate("stop-a", 50, 60)),
                    LabeledCase(
                        2,
                        "NoValidCandidate",
                        null,
                        "NoReliableMatch",
                        Candidate("far", 1800, 2)),
                ]);

            using var offline = OfflineOnlyGuard.Enter();
            var result = LockedHoldoutEvaluationService.Evaluate(
                temp,
                gitCommit: "test-commit",
                gitTag: "test-tag",
                requireFrozenHoldoutIdentity: false);

            Assert.True(result.Completed);
            Assert.Equal(0, result.ExitCode);
            Assert.NotNull(result.Report);
            Assert.True(result.Report!.HoldoutOpened);
            Assert.False(result.Report.ExternalDataAccessed);
            Assert.Equal(2, result.Report.CaseCount);
            Assert.Equal(1, result.Report.AcceptedMatches);
            Assert.Equal(1, result.Report.CorrectAcceptedMatches);
            Assert.Equal(1.0, result.Report.Precision);
            Assert.Equal(0.5, result.Report.Coverage);
            Assert.Equal(0, result.Report.FalsePositives);
            Assert.Equal(0, result.Report.FalseNegatives);
            Assert.Equal(0, result.Report.WrongVisitCandidateChoices);
            Assert.Equal(1, result.Report.Abstentions);
            Assert.Equal("GO", result.Report.Decision);
            Assert.True(File.Exists(result.FinalJsonPath));
            Assert.True(File.Exists(result.FinalMarkdownPath));
            Assert.True(File.Exists(result.StartedMarkerPath));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void Evaluate_Rejects_WhenFinalReportAlreadyExists()
    {
        var temp = NewTempDocs();
        try
        {
            WriteHoldout(
                temp,
                [
                    LabeledCase(
                        1,
                        "CorrectCandidate",
                        "stop-a",
                        "ConfirmedLocationMatch",
                        Candidate("stop-a", 50, 60)),
                ]);
            File.WriteAllText(
                Path.Combine(temp, LockedHoldoutEvaluationService.FinalJsonFileName),
                "{}");

            var result = LockedHoldoutEvaluationService.Evaluate(
                temp,
                requireFrozenHoldoutIdentity: false);

            Assert.False(result.Completed);
            Assert.Equal(1, result.ExitCode);
            Assert.Equal("REJECTED", result.Decision);
            Assert.Contains(result.Messages, message => message.Contains("Finale holdoutrapport", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(temp, LockedHoldoutEvaluationService.StartedMarkerFileName)));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void Evaluate_Rejects_WhenStartedMarkerAlreadyExists()
    {
        var temp = NewTempDocs();
        try
        {
            WriteHoldout(
                temp,
                [
                    LabeledCase(
                        1,
                        "CorrectCandidate",
                        "stop-a",
                        "ConfirmedLocationMatch",
                        Candidate("stop-a", 50, 60)),
                ]);
            File.WriteAllText(
                Path.Combine(temp, LockedHoldoutEvaluationService.StartedMarkerFileName),
                "{}");

            var result = LockedHoldoutEvaluationService.Evaluate(
                temp,
                requireFrozenHoldoutIdentity: false);

            Assert.False(result.Completed);
            Assert.Equal(1, result.ExitCode);
            Assert.Equal("REJECTED", result.Decision);
            Assert.Contains(result.Messages, message => message.Contains("started", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void Evaluate_SecondRun_IsBlocked_AfterFirstCompletion()
    {
        var temp = NewTempDocs();
        try
        {
            WriteHoldout(
                temp,
                [
                    LabeledCase(
                        1,
                        "CorrectCandidate",
                        "stop-a",
                        "ConfirmedLocationMatch",
                        Candidate("stop-a", 50, 60)),
                ]);

            var first = LockedHoldoutEvaluationService.Evaluate(
                temp,
                requireFrozenHoldoutIdentity: false);
            Assert.True(first.Completed);

            var second = LockedHoldoutEvaluationService.Evaluate(
                temp,
                requireFrozenHoldoutIdentity: false);
            Assert.False(second.Completed);
            Assert.Equal("REJECTED", second.Decision);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void Evaluate_UsesOfflineGuard_NoLiveProviders()
    {
        var temp = NewTempDocs();
        try
        {
            WriteHoldout(
                temp,
                [
                    LabeledCase(
                        1,
                        "NoValidCandidate",
                        null,
                        "NoReliableMatch"),
                ]);

            var result = LockedHoldoutEvaluationService.Evaluate(
                temp,
                requireFrozenHoldoutIdentity: false);
            Assert.True(result.Completed);
            Assert.NotNull(result.Report);
            Assert.False(result.Report!.ExternalDataAccessed);

            using var scope = OfflineOnlyGuard.Enter();
            var ex = Assert.Throws<InvalidOperationException>(
                () => OfflineOnlyGuard.EnsureLiveAccessAllowed("Powerfleet"));
            Assert.Contains("Powerfleet", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void Evaluate_NoGo_WhenFalsePositiveOnNoValidCandidate()
    {
        var temp = NewTempDocs();
        try
        {
            WriteHoldout(
                temp,
                [
                    LabeledCase(
                        1,
                        "NoValidCandidate",
                        null,
                        "NoReliableMatch",
                        Candidate("near", 40, 40)),
                ]);

            var result = LockedHoldoutEvaluationService.Evaluate(
                temp,
                requireFrozenHoldoutIdentity: false);
            Assert.True(result.Completed);
            Assert.NotNull(result.Report);
            Assert.Equal(1, result.Report!.FalsePositives);
            Assert.Equal(0.0, result.Report.Precision);
            Assert.Equal("NO-GO", result.Report.Decision);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static LockedHoldoutErrorRow Error(string category, long id) =>
        new()
        {
            PerformanceId = id,
            Label = "NoValidCandidate",
            Category = category,
            PredictedDecision = "RecoveredProbable",
            Diagnostics = "test",
        };

    private static string NewTempDocs()
    {
        var path = Path.Combine(Path.GetTempPath(), "holdout-eval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteHoldout(
        string docsPath,
        IReadOnlyList<LocationMatchingBenchmarkCase> cases)
    {
        var contentSha = LocationMatchingBenchmarkSampling.ComputeContentSha256(cases);
        File.WriteAllText(
            Path.Combine(docsPath, LocationMatchingBenchmarkService.HoldoutFileName),
            JsonSerializer.Serialize(
                new LocationMatchingHoldoutFile
                {
                    Locked = true,
                    DoNotUseForOptimization = true,
                    Warning = "test",
                    Cases = cases,
                },
                JsonOptions));
        File.WriteAllText(
            Path.Combine(docsPath, LocationMatchingBenchmarkService.HoldoutManifestFileName),
            JsonSerializer.Serialize(
                new HoldoutSamplingManifest
                {
                    RandomSeed = 1,
                    GeneratedAt = DateTimeOffset.UtcNow,
                    Locked = true,
                    TargetCaseCount = cases.Count,
                    MaxCasesPerLacleunik = 8,
                    MinUniqueLacleunik = 1,
                    SelectedPerformanceIds = cases.Select(item => item.PerformanceId).ToArray(),
                    CompleteMonthsUsed = ["2025-10"],
                    CountsByTechnician = new Dictionary<string, int> { ["Test"] = cases.Count },
                    CountsByMonth = new Dictionary<string, int> { ["2025-10"] = cases.Count },
                    CountsByExposure = new Dictionary<string, int> { ["SeenLocation"] = cases.Count },
                    ContentSha256 = contentSha,
                },
                JsonOptions));
    }

    private static LocationMatchingBenchmarkCase LabeledCase(
        long id,
        string label,
        string? expectedStopId,
        string existingStatus,
        params LocationMatchingBenchmarkCandidate[] candidates)
    {
        var start = DateTimeOffset.Parse("2025-10-15T09:00:00+02:00", CultureInfo.InvariantCulture);
        return new LocationMatchingBenchmarkCase
        {
            PerformanceId = id,
            Technician = "Test Tech",
            Date = DateOnly.Parse("2025-10-15", CultureInfo.InvariantCulture),
            Start = start,
            End = start.AddHours(1),
            Lacleunik = "L1",
            PlenionAddress = "Teststraat 1",
            GeocodeQuality = GeocodeQualityClass.PreciseBuilding,
            ExistingMatchStatus = existingStatus,
            Candidates = candidates,
            Label = label,
            ExpectedStopId = expectedStopId,
            ReviewerConfidence = "High",
            LocationExposure = "SeenLocation",
            DatasetRole = "holdout",
        };
    }

    private static LocationMatchingBenchmarkCandidate Candidate(
        string stopId,
        double distanceMeters,
        int overlapMinutes)
    {
        var arrival = DateTimeOffset.Parse("2025-10-15T09:00:00+02:00", CultureInfo.InvariantCulture);
        return new LocationMatchingBenchmarkCandidate
        {
            StopId = stopId,
            Address = "Teststraat 1",
            DistanceMeters = distanceMeters,
            Arrival = arrival,
            Departure = arrival.AddMinutes(Math.Max(overlapMinutes, 1)),
            OverlapMinutes = overlapMinutes,
            StartDifferenceMinutes = 0,
            EndDifferenceMinutes = 60 - overlapMinutes,
            ExistingCandidateStatus = "Candidate",
            ExistingCandidateScore = 50,
            Explanation = "test",
        };
    }
}
