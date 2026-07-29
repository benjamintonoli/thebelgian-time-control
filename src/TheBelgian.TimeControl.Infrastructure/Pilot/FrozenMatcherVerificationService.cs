using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

/// <summary>
/// Offline-only frozen matcher verification gate. Never opens holdout and never calls live providers.
/// </summary>
internal static class FrozenMatcherVerificationService
{
    public const string ManifestFileName = "frozen-matcher-manifest.json";
    public const string ReportFileName = "frozen-matcher-verification.json";
    public const string LabelsReviewer1FileName = "calibration-labels-reviewer1.json";
    public const string RecoveryLabelsReviewer1FileName = "recovery-audit-labels-reviewer1.json";

    // Verification expectations only — never used by production matching.
    private static readonly (long Id, string Expectation)[] RegressionExpectations =
    [
        (276126, "Reject"),
        (279620, "Reject"),
        (276882, "MergedVisit"),
        (279852, "MergedVisit"),
        (280280, "MergedVisit"),
        (279970, "Accept"),
        (280198, "Reject"),
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static FrozenMatcherVerificationResult Verify(
        string docsPath,
        string? gitCommit = null)
    {
        var failures = new List<string>();
        var notes = new List<string>
        {
            "Offline-only verificatie; locked holdout niet geopend.",
            "Geen Plenion/ODBC/Powerfleet/Geoapify DI of HTTP-initialisatie in dit commandopad.",
        };

        using var offlineScope = OfflineOnlyGuard.Enter();
        var options = new AdaptiveLocationMatchingOptions();
        options.Validate();
        var optionsSnapshot = SnapshotOptions(options);
        var configurationHash = ComputeConfigurationHash(optionsSnapshot);
        var commit = string.IsNullOrWhiteSpace(gitCommit)
            ? TryReadGitCommit()
            : gitCommit.Trim();

        FrozenMatcherInputHashes hashes;
        try
        {
            hashes = ComputeInputHashes(docsPath);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return FailEarly(
                docsPath,
                commit,
                configurationHash,
                failures: [$"Ontbrekende of onleesbare input: {exception.Message}"],
                notes);
        }

        if (OfflineOnlyGuard.IsActive == false)
        {
            failures.Add("OfflineOnlyGuard was niet actief tijdens verificatie.");
        }

        CalibrationScore calibrationScore;
        AuditScore auditScore;
        try
        {
            calibrationScore = ScoreCalibrationOffline(docsPath, options);
            auditScore = ScoreRecoveryAuditOffline(docsPath, options);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return FailEarly(
                docsPath,
                commit,
                configurationHash,
                failures: [$"Evaluatiefout: {exception.Message}"],
                notes);
        }

        var regressionChecks = EvaluateRegressions(calibrationScore.Cases, auditScore.Cases, failures);
        CheckCalibrationCriteria(calibrationScore.Metrics, failures);
        CheckAuditCriteria(auditScore.RecoveryOnly, auditScore.AllLabeled, failures);

        var passed = failures.Count == 0;
        var manifest = new FrozenMatcherManifest
        {
            MatcherVersion = options.CalculationVersion,
            GitCommit = commit,
            ConfigurationHashSha256 = configurationHash,
            CreatedAt = DateTimeOffset.UtcNow,
            Options = optionsSnapshot,
            InputFileSha256 = hashes,
            HoldoutPolicy = "Locked holdout was not opened, read, or evaluated.",
            Mode = "offline-local-datasets-only",
        };

        var manifestPath = Path.Combine(docsPath, ManifestFileName);
        var reportPath = Path.Combine(docsPath, ReportFileName);
        Directory.CreateDirectory(docsPath);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), Encoding.UTF8);

        var result = new FrozenMatcherVerificationResult
        {
            Passed = passed,
            ExitCode = passed ? 0 : 1,
            GitCommit = commit,
            ConfigurationHashSha256 = configurationHash,
            ManifestPath = manifestPath,
            ReportPath = reportPath,
            ExternalDataAccessed = false,
            HoldoutOpened = false,
            Calibration = calibrationScore.Metrics,
            RecoveryOnly = auditScore.RecoveryOnly,
            AllLabeledHybrid = auditScore.AllLabeled,
            RegressionChecks = regressionChecks,
            Failures = failures,
            Notes = notes,
        };
        File.WriteAllText(reportPath, JsonSerializer.Serialize(result, JsonOptions), Encoding.UTF8);
        return result;
    }

    /// <summary>
    /// Test helper: run criteria checks against an options instance (may fail on purpose).
    /// </summary>
    internal static bool MeetsFrozenCalibrationCriteria(FrozenMatcherMetricSlice metrics) =>
        Math.Abs(metrics.Precision - 1.0) < 0.0001 &&
        (metrics.Coverage ?? 0) >= 0.333 &&
        metrics.FalsePositives == 0 &&
        metrics.WrongVisitCandidateChoices == 0;

    internal static bool MeetsFrozenAuditCriteria(
        FrozenMatcherMetricSlice recoveryOnly,
        FrozenMatcherMetricSlice allLabeled) =>
        Math.Abs(recoveryOnly.Precision - 1.0) < 0.0001 &&
        Math.Abs(allLabeled.Precision - 1.0) < 0.0001 &&
        allLabeled.FalsePositives == 0 &&
        allLabeled.FalseNegatives == 0 &&
        allLabeled.WrongVisitCandidateChoices == 0 &&
        recoveryOnly.FalsePositives == 0;

    internal static AdaptiveLocationMatchingOptionsSnapshot SnapshotOptions(
        AdaptiveLocationMatchingOptions options) =>
        new()
        {
            CalculationVersion = options.CalculationVersion,
            StrongDistanceMeters = options.StrongDistanceMeters,
            ProbableDistanceMeters = options.ProbableDistanceMeters,
            MaximumLearnedClusterDistanceMeters = options.MaximumLearnedClusterDistanceMeters,
            MinimumOverlapMinutes = options.MinimumOverlapMinutes,
            MinimumOverlapPercent = options.MinimumOverlapPercent,
            StrongOverlapPercent = options.StrongOverlapPercent,
            VisitMergeDistanceMeters = options.VisitMergeDistanceMeters,
            VisitMergeMaxGapMinutes = options.VisitMergeMaxGapMinutes,
            EnablePrecisionPreservingRecovery = options.EnablePrecisionPreservingRecovery,
            RecoveryMaximumDistanceMeters = options.RecoveryMaximumDistanceMeters,
            RecoveryMinimumOverlapMinutes = options.RecoveryMinimumOverlapMinutes,
            RecoveryMinimumOverlapPercent = options.RecoveryMinimumOverlapPercent,
            RecoveryStrongOverlapMinutes = options.RecoveryStrongOverlapMinutes,
            RecoveryStrongOverlapPercent = options.RecoveryStrongOverlapPercent,
            RecoveryMinimumScoreMargin = options.RecoveryMinimumScoreMargin,
            RecoveryShortChainMinOverlapMinutes = options.RecoveryShortChainMinOverlapMinutes,
            RecoveryShortChainMinCombinedOverlapPercent =
                options.RecoveryShortChainMinCombinedOverlapPercent,
        };

    internal static string ComputeConfigurationHash(
        AdaptiveLocationMatchingOptionsSnapshot snapshot)
    {
        var payload = JsonSerializer.Serialize(snapshot);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant();
    }

    private static FrozenMatcherInputHashes ComputeInputHashes(string docsPath)
    {
        return new FrozenMatcherInputHashes
        {
            LocationMatchingCalibrationJson = Sha256File(
                Path.Combine(docsPath, LocationMatchingBenchmarkService.CalibrationFileName)),
            CalibrationLabelsReviewer1Json = Sha256File(
                Path.Combine(docsPath, LabelsReviewer1FileName)),
            RecoveryAuditSetJson = Sha256File(
                Path.Combine(docsPath, LocationMatchingRecoveryAuditService.SetFileName)),
            RecoveryAuditLabelsReviewer1Json = Sha256File(
                Path.Combine(docsPath, RecoveryLabelsReviewer1FileName)),
        };
    }

    private static string Sha256File(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Vereist frozen-matcher inputbestand ontbreekt.", path);
        }

        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static CalibrationScore ScoreCalibrationOffline(
        string docsPath,
        AdaptiveLocationMatchingOptions options)
    {
        var calibration = LocationMatchingBenchmarkService.LoadCalibrationCases(docsPath)
            .Where(item => !string.IsNullOrWhiteSpace(item.Label))
            .OrderBy(item => item.PerformanceId)
            .ToArray();
        if (calibration.Length != LocationMatchingBenchmarkSampling.CalibrationCaseCount)
        {
            throw new InvalidOperationException(
                $"Kalibratieset verwacht {LocationMatchingBenchmarkSampling.CalibrationCaseCount} gelabelde cases; gevonden {calibration.Length}.");
        }

        var cases = new List<ScoredCase>(calibration.Length);
        foreach (var item in calibration)
        {
            var prediction = OfflineHybridPredictor.Predict(item, options, recovery: true);
            cases.Add(ScoreCalibrationCase(item, prediction));
        }

        return new CalibrationScore(cases, ToCalibrationMetrics(cases));
    }

    private static AuditScore ScoreRecoveryAuditOffline(
        string docsPath,
        AdaptiveLocationMatchingOptions options)
    {
        var setPath = Path.Combine(docsPath, LocationMatchingRecoveryAuditService.SetFileName);
        var set = JsonSerializer.Deserialize<RecoveryAuditSetFile>(
                      File.ReadAllText(setPath),
                      JsonOptions) ??
                  throw new InvalidOperationException("Recovery-auditset is ongeldig.");
        if (set.Cases.Count != 51)
        {
            throw new InvalidOperationException(
                $"Recovery-auditset verwacht 51 cases; gevonden {set.Cases.Count}.");
        }

        var labeled = set.Cases
            .Where(item => !string.IsNullOrWhiteSpace(item.Label))
            .Select(item => OfflineHybridPredictor.RescoreAuditCase(item, options))
            .ToArray();
        if (labeled.Length != 51)
        {
            throw new InvalidOperationException(
                $"Recovery-auditset verwacht 51 gelabelde cases; gevonden {labeled.Length}.");
        }

        var scored = labeled.Select(ScoreAuditCase).ToArray();
        var recoveryOnly = scored
            .Where(item => item.UsedRecovery)
            .ToArray();
        return new AuditScore(
            labeled,
            ToAuditMetrics(recoveryOnly),
            ToAuditMetrics(scored));
    }

    private static ScoredCase ScoreCalibrationCase(
        LocationMatchingBenchmarkCase item,
        OfflineHybridPredictor.Prediction prediction)
    {
        var label = item.Label!;
        var correctAccepted = false;
        var falsePositive = false;
        var falseNegative = false;
        var wrongVisit = false;

        if (string.Equals(label, "CorrectCandidate", StringComparison.Ordinal))
        {
            if (!prediction.Accepted)
            {
                falseNegative = true;
            }
            else if (VisitLabelMatching.MatchesVisit(
                         item.ExpectedStopId,
                         item.ExpectedVisitStopIds,
                         prediction.StopId,
                         prediction.SourceStopIds))
            {
                correctAccepted = true;
            }
            else
            {
                falsePositive = true;
                wrongVisit = true;
            }
        }
        else if (label is "NoValidCandidate" or "Ambiguous")
        {
            if (prediction.Accepted)
            {
                falsePositive = true;
            }
        }

        return new ScoredCase(
            item.PerformanceId,
            prediction.Accepted,
            correctAccepted,
            falsePositive,
            falseNegative,
            wrongVisit,
            prediction.Decision,
            prediction.SourceStopIds.ToArray(),
            UsedRecovery: prediction.UsedRecovery,
            Source: "calibration");
    }

    private static ScoredCase ScoreAuditCase(RecoveryAuditCase item)
    {
        var accepted = item.HybridDecision is "Confirmed" or "Probable" or "RecoveredProbable";
        var sources = item.SelectedSourceStopIds ?? [];
        var label = item.Label!;
        var correctAccepted = false;
        var falsePositive = false;
        var falseNegative = false;
        var wrongVisit = false;

        if (string.Equals(label, "CorrectCandidate", StringComparison.Ordinal))
        {
            if (!accepted)
            {
                falseNegative = true;
            }
            else if (VisitLabelMatching.MatchesVisit(
                         item.ExpectedStopId,
                         item.ExpectedVisitStopIds,
                         item.SelectedStopId,
                         sources))
            {
                correctAccepted = true;
            }
            else
            {
                falsePositive = true;
                wrongVisit = true;
            }
        }
        else if (label is "NoValidCandidate" or "Ambiguous")
        {
            if (accepted)
            {
                falsePositive = true;
            }
        }

        return new ScoredCase(
            item.PerformanceId,
            accepted,
            correctAccepted,
            falsePositive,
            falseNegative,
            wrongVisit,
            item.HybridDecision,
            sources.ToArray(),
            UsedRecovery: item.UsedRecovery,
            Source: "audit");
    }

    private static FrozenMatcherMetricSlice ToCalibrationMetrics(IReadOnlyList<ScoredCase> scored)
    {
        var accepted = scored.Count(item => item.Accepted);
        var correct = scored.Count(item => item.CorrectAccepted);
        return new FrozenMatcherMetricSlice
        {
            CaseCount = scored.Count,
            AcceptedMatches = accepted,
            CorrectAcceptedMatches = correct,
            Precision = Round(accepted == 0 ? 0 : correct / (double)accepted),
            Coverage = Round(scored.Count == 0 ? 0 : accepted / (double)scored.Count),
            FalsePositives = scored.Count(item => item.FalsePositive),
            FalseNegatives = scored.Count(item => item.FalseNegative),
            WrongVisitCandidateChoices = scored.Count(item => item.WrongVisit),
        };
    }

    private static FrozenMatcherMetricSlice ToAuditMetrics(IReadOnlyList<ScoredCase> scored)
    {
        var accepted = scored.Count(item => item.Accepted);
        var correct = scored.Count(item => item.CorrectAccepted);
        return new FrozenMatcherMetricSlice
        {
            CaseCount = scored.Count,
            AcceptedMatches = accepted,
            CorrectAcceptedMatches = correct,
            Precision = Round(accepted == 0 ? 0 : correct / (double)accepted),
            Coverage = null,
            FalsePositives = scored.Count(item => item.FalsePositive),
            FalseNegatives = scored.Count(item => item.FalseNegative),
            WrongVisitCandidateChoices = scored.Count(item => item.WrongVisit),
        };
    }

    private static void CheckCalibrationCriteria(
        FrozenMatcherMetricSlice metrics,
        List<string> failures)
    {
        if (Math.Abs(metrics.Precision - 1.0) > 0.0001)
        {
            failures.Add($"Calibration precision {metrics.Precision} != 1.000");
        }

        if ((metrics.Coverage ?? 0) < 0.333)
        {
            failures.Add($"Calibration coverage {metrics.Coverage} < 0.333");
        }

        if (metrics.FalsePositives != 0)
        {
            failures.Add($"Calibration FP={metrics.FalsePositives}");
        }

        if (metrics.WrongVisitCandidateChoices != 0)
        {
            failures.Add($"Calibration wrong VisitCandidate={metrics.WrongVisitCandidateChoices}");
        }
    }

    private static void CheckAuditCriteria(
        FrozenMatcherMetricSlice recoveryOnly,
        FrozenMatcherMetricSlice allLabeled,
        List<string> failures)
    {
        if (Math.Abs(recoveryOnly.Precision - 1.0) > 0.0001)
        {
            failures.Add($"Recovery-only precision {recoveryOnly.Precision} != 1.000");
        }

        if (Math.Abs(allLabeled.Precision - 1.0) > 0.0001)
        {
            failures.Add($"Audit hybrid precision {allLabeled.Precision} != 1.000");
        }

        if (allLabeled.FalsePositives != 0 || recoveryOnly.FalsePositives != 0)
        {
            failures.Add(
                $"Audit FP all={allLabeled.FalsePositives} recoveryOnly={recoveryOnly.FalsePositives}");
        }

        if (allLabeled.FalseNegatives != 0)
        {
            failures.Add($"Audit FN={allLabeled.FalseNegatives}");
        }

        if (allLabeled.WrongVisitCandidateChoices != 0)
        {
            failures.Add($"Audit wrong VisitCandidate={allLabeled.WrongVisitCandidateChoices}");
        }
    }

    private static List<FrozenMatcherRegressionCheck> EvaluateRegressions(
        IReadOnlyList<ScoredCase> calibration,
        IReadOnlyList<RecoveryAuditCase> audit,
        List<string> failures)
    {
        var byId = new Dictionary<long, (bool Accepted, IReadOnlyList<string> Sources, string Decision, string Source)>();
        foreach (var item in calibration)
        {
            byId[item.PerformanceId] = (item.Accepted, item.SourceStopIds, item.Decision, "calibration");
        }

        foreach (var item in audit)
        {
            var accepted = item.HybridDecision is "Confirmed" or "Probable" or "RecoveredProbable";
            byId[item.PerformanceId] = (
                accepted,
                item.SelectedSourceStopIds ?? [],
                item.HybridDecision,
                "audit");
        }

        var checks = new List<FrozenMatcherRegressionCheck>();
        foreach (var (id, expectation) in RegressionExpectations)
        {
            if (!byId.TryGetValue(id, out var observed))
            {
                var check = new FrozenMatcherRegressionCheck
                {
                    PerformanceId = id,
                    Expectation = expectation,
                    Passed = false,
                    Observed = "missing",
                };
                checks.Add(check);
                failures.Add($"Regressie {id}: case ontbreekt in lokale datasets.");
                continue;
            }

            var passed = expectation switch
            {
                "Reject" => !observed.Accepted,
                "Accept" => observed.Accepted,
                "MergedVisit" => observed.Accepted && observed.Sources.Count >= 2,
                _ => false,
            };
            var observedText = string.Create(
                CultureInfo.InvariantCulture,
                $"{observed.Source}:{observed.Decision}:accepted={observed.Accepted}:sources={observed.Sources.Count}");
            checks.Add(
                new FrozenMatcherRegressionCheck
                {
                    PerformanceId = id,
                    Expectation = expectation,
                    Passed = passed,
                    Observed = observedText,
                });
            if (!passed)
            {
                failures.Add($"Regressie {id} verwacht {expectation}, kreeg {observedText}.");
            }
        }

        return checks;
    }

    private static FrozenMatcherVerificationResult FailEarly(
        string docsPath,
        string commit,
        string configurationHash,
        List<string> failures,
        List<string> notes)
    {
        var empty = new FrozenMatcherMetricSlice
        {
            CaseCount = 0,
            AcceptedMatches = 0,
            CorrectAcceptedMatches = 0,
            Precision = 0,
            Coverage = 0,
            FalsePositives = 0,
            FalseNegatives = 0,
            WrongVisitCandidateChoices = 0,
        };
        var result = new FrozenMatcherVerificationResult
        {
            Passed = false,
            ExitCode = 1,
            GitCommit = commit,
            ConfigurationHashSha256 = configurationHash,
            ManifestPath = Path.Combine(docsPath, ManifestFileName),
            ReportPath = Path.Combine(docsPath, ReportFileName),
            ExternalDataAccessed = false,
            HoldoutOpened = false,
            Calibration = empty,
            RecoveryOnly = empty,
            AllLabeledHybrid = empty,
            RegressionChecks = [],
            Failures = failures,
            Notes = notes,
        };
        Directory.CreateDirectory(docsPath);
        File.WriteAllText(
            result.ReportPath,
            JsonSerializer.Serialize(result, JsonOptions),
            Encoding.UTF8);
        return result;
    }

    private static string TryReadGitCommit()
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse HEAD",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null)
            {
                return "unknown";
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            return string.IsNullOrWhiteSpace(output) ? "unknown" : output;
        }
        catch
        {
            return "unknown";
        }
    }

    private static double Round(double value) =>
        Math.Round(value, 4, MidpointRounding.AwayFromZero);

    private sealed record ScoredCase(
        long PerformanceId,
        bool Accepted,
        bool CorrectAccepted,
        bool FalsePositive,
        bool FalseNegative,
        bool WrongVisit,
        string Decision,
        IReadOnlyList<string> SourceStopIds,
        bool UsedRecovery,
        string Source);

    private sealed record CalibrationScore(
        IReadOnlyList<ScoredCase> Cases,
        FrozenMatcherMetricSlice Metrics);

    private sealed record AuditScore(
        IReadOnlyList<RecoveryAuditCase> Cases,
        FrozenMatcherMetricSlice RecoveryOnly,
        FrozenMatcherMetricSlice AllLabeled);
}
