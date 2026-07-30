using System.Globalization;
using System.Text.Json;
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
    public void Evaluate_ComputesMetrics_FromSidecarLabels()
    {
        var temp = NewTempDocs();
        try
        {
            WriteHoldout(
                temp,
                [
                    Case(1, "ConfirmedLocationMatch", Candidate("stop-a", 50, 60)),
                    Case(2, "NoReliableMatch", Candidate("far", 1800, 2)),
                ]);
            WriteLabels(
                temp,
                [
                    Label(1, "CorrectCandidate", "stop-a", null, "High"),
                    Label(2, "NoValidCandidate", null, null, "High"),
                ]);

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
            Assert.Equal("GO", result.Report.Decision);
            Assert.True(File.Exists(result.StartedMarkerPath));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void Evaluate_IncompleteLabels_DoNotConsumeOneShot()
    {
        var temp = NewTempDocs();
        try
        {
            WriteHoldout(
                temp,
                [
                    Case(1, "ConfirmedLocationMatch", Candidate("stop-a", 50, 60)),
                ]);
            WriteLabels(
                temp,
                [
                    new CalibrationLabelEntry
                    {
                        PerformanceId = 1,
                        Label = null,
                        ExpectedStopId = null,
                        ReviewerConfidence = null,
                        ReviewerNote = null,
                    },
                ]);

            var result = LockedHoldoutEvaluationService.Evaluate(
                temp,
                requireFrozenHoldoutIdentity: false);

            Assert.False(result.Completed);
            Assert.Equal(1, result.ExitCode);
            Assert.Equal("REJECTED", result.Decision);
            Assert.False(File.Exists(Path.Combine(temp, LockedHoldoutEvaluationService.StartedMarkerFileName)));
            Assert.False(File.Exists(Path.Combine(temp, LockedHoldoutEvaluationService.FinalJsonFileName)));
            Assert.Contains(result.Messages, message => message.Contains("ongeldig", StringComparison.OrdinalIgnoreCase) ||
                                                       message.Contains("Label", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void Evaluate_MissingLabelsFile_DoesNotConsumeOneShot()
    {
        var temp = NewTempDocs();
        try
        {
            WriteHoldout(
                temp,
                [
                    Case(1, "ConfirmedLocationMatch", Candidate("stop-a", 50, 60)),
                ]);

            var result = LockedHoldoutEvaluationService.Evaluate(
                temp,
                requireFrozenHoldoutIdentity: false);

            Assert.False(result.Completed);
            Assert.Equal("REJECTED", result.Decision);
            Assert.False(File.Exists(Path.Combine(temp, LockedHoldoutEvaluationService.StartedMarkerFileName)));
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
            WriteHoldout(temp, [Case(1, "ConfirmedLocationMatch", Candidate("stop-a", 50, 60))]);
            WriteLabels(temp, [Label(1, "CorrectCandidate", "stop-a", null, "High")]);
            File.WriteAllText(Path.Combine(temp, LockedHoldoutEvaluationService.FinalJsonFileName), "{}");

            var result = LockedHoldoutEvaluationService.Evaluate(
                temp,
                requireFrozenHoldoutIdentity: false);

            Assert.False(result.Completed);
            Assert.Equal("REJECTED", result.Decision);
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
            WriteHoldout(temp, [Case(1, "ConfirmedLocationMatch", Candidate("stop-a", 50, 60))]);
            WriteLabels(temp, [Label(1, "CorrectCandidate", "stop-a", null, "High")]);
            File.WriteAllText(Path.Combine(temp, LockedHoldoutEvaluationService.StartedMarkerFileName), "{}");

            var result = LockedHoldoutEvaluationService.Evaluate(
                temp,
                requireFrozenHoldoutIdentity: false);

            Assert.False(result.Completed);
            Assert.Equal("REJECTED", result.Decision);
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
            WriteHoldout(temp, [Case(1, "ConfirmedLocationMatch", Candidate("stop-a", 50, 60))]);
            WriteLabels(temp, [Label(1, "CorrectCandidate", "stop-a", null, "High")]);

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
    public void Evaluate_NoGo_WhenFalsePositiveOnNoValidCandidate()
    {
        var temp = NewTempDocs();
        try
        {
            WriteHoldout(temp, [Case(1, "NoReliableMatch", Candidate("near", 40, 40))]);
            WriteLabels(temp, [Label(1, "NoValidCandidate", null, null, "High")]);

            var result = LockedHoldoutEvaluationService.Evaluate(
                temp,
                requireFrozenHoldoutIdentity: false);
            Assert.True(result.Completed);
            Assert.Equal(1, result.Report!.FalsePositives);
            Assert.Equal("NO-GO", result.Report.Decision);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void ExportReviewPack_IsBlind_AndWritesEmptyLabels()
    {
        var temp = NewTempDocs();
        try
        {
            WriteHoldout(
                temp,
                [
                    Case(
                        10,
                        "NoReliableMatch",
                        Candidate("a", 40, 30),
                        Candidate("b", 45, 20)),
                ]);

            var exported = LockedHoldoutReviewPackService.ExportReviewPack(
                temp,
                requireFrozenHoldoutIdentity: false);

            Assert.Equal(1, exported.CaseCount);
            Assert.True(File.Exists(exported.MarkdownPath));
            Assert.True(File.Exists(exported.LabelsPath));

            var markdown = File.ReadAllText(exported.MarkdownPath);
            Assert.Contains("PerformanceId", markdown, StringComparison.Ordinal);
            Assert.Contains("Possible visit groups", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("RecoveredProbable", markdown, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ExistingMatchStatus", markdown, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ExistingCandidateScore", markdown, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ScoreMargin", markdown, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NO-GO", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("CONDITIONAL GO", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("adaptive", markdown, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hybrid", markdown, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Unresolved", markdown, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ProbableLocationMatch", markdown, StringComparison.OrdinalIgnoreCase);

            var labels = JsonSerializer.Deserialize<List<CalibrationLabelEntry>>(
                File.ReadAllText(exported.LabelsPath),
                JsonOptions)!;
            Assert.Single(labels);
            Assert.Equal(10, labels[0].PerformanceId);
            Assert.Null(labels[0].Label);
            Assert.Null(labels[0].ExpectedStopId);
            Assert.Null(labels[0].ExpectedVisitStopIds);
            Assert.Null(labels[0].ReviewerConfidence);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void ExportReviewPack_DoesNotMutateHoldout()
    {
        var temp = NewTempDocs();
        try
        {
            var cases = new[]
            {
                Case(3, "NoReliableMatch", Candidate("x", 12, 15)),
            };
            WriteHoldout(temp, cases);
            var before = File.ReadAllText(
                Path.Combine(temp, LocationMatchingBenchmarkService.HoldoutFileName));
            var beforeSha = LocationMatchingBenchmarkSampling.ComputeContentSha256(cases);

            var exported = LockedHoldoutReviewPackService.ExportReviewPack(
                temp,
                requireFrozenHoldoutIdentity: false);

            var after = File.ReadAllText(
                Path.Combine(temp, LocationMatchingBenchmarkService.HoldoutFileName));
            Assert.Equal(before, after);
            Assert.Equal(beforeSha, exported.HoldoutContentSha256);
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
        LocationMatchingBenchmarkCase[] cases)
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
                    TargetCaseCount = cases.Length,
                    MaxCasesPerLacleunik = 8,
                    MinUniqueLacleunik = 1,
                    SelectedPerformanceIds = cases.Select(item => item.PerformanceId).ToArray(),
                    CompleteMonthsUsed = ["2025-10"],
                    CountsByTechnician = new Dictionary<string, int> { ["Test"] = cases.Length },
                    CountsByMonth = new Dictionary<string, int> { ["2025-10"] = cases.Length },
                    CountsByExposure = new Dictionary<string, int> { ["SeenLocation"] = cases.Length },
                    ContentSha256 = contentSha,
                },
                JsonOptions));
    }

    private static void WriteLabels(
        string docsPath,
        IReadOnlyList<CalibrationLabelEntry> entries)
    {
        File.WriteAllText(
            Path.Combine(docsPath, LockedHoldoutReviewPackService.LabelsFileName),
            JsonSerializer.Serialize(entries, JsonOptions));
    }

    private static CalibrationLabelEntry Label(
        long id,
        string label,
        string? expectedStopId,
        IReadOnlyList<string>? expectedVisitStopIds,
        string confidence) =>
        new()
        {
            PerformanceId = id,
            Label = label,
            ExpectedStopId = expectedStopId,
            ExpectedVisitStopIds = expectedVisitStopIds,
            ReviewerConfidence = confidence,
            ReviewerNote = "synthetic",
        };

    private static LocationMatchingBenchmarkCase Case(
        long id,
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
            Label = null,
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
