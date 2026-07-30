using System.Globalization;
using System.Text;
using System.Text.Json;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

/// <summary>
/// One-shot offline locked-holdout evaluation. Never initializes Plenion/Powerfleet/Geoapify.
/// Does not change matcher thresholds or holdout contents.
/// </summary>
internal static class LockedHoldoutEvaluationService
{
    public const string FinalJsonFileName = "location-matching-holdout-final.json";
    public const string FinalMarkdownFileName = "location-matching-holdout-final.md";
    public const string StartedMarkerFileName = "location-matching-holdout-started.json";
    public const string LabelsFileName = LockedHoldoutReviewPackService.LabelsFileName;
    public const string ExpectedConfigurationHashSha256 =
        "b4cccfa21f20e5d3be59b992fcdb8352849c36dd1d24529990235e918565b043";
    public const string ExpectedHoldoutContentSha256 =
        "206a0ac89162151a6b236deb3047c9574d84409c2b3a82b2ff2f6c415d08f2b9";
    public const int ExpectedCaseCount = 59;

    private static readonly string[] AllowedLabels =
    [
        "CorrectCandidate",
        "NoValidCandidate",
        "Ambiguous",
    ];

    private static readonly string[] AllowedConfidence =
    [
        "High",
        "Medium",
        "Low",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
    };

    public static LockedHoldoutEvaluationResult Evaluate(
        string docsPath,
        string? gitCommit = null,
        string? gitTag = null,
        AdaptiveLocationMatchingOptions? options = null,
        bool requireFrozenHoldoutIdentity = true)
    {
        using var offline = OfflineOnlyGuard.Enter();
        Directory.CreateDirectory(docsPath);

        var finalJsonPath = Path.Combine(docsPath, FinalJsonFileName);
        var finalMdPath = Path.Combine(docsPath, FinalMarkdownFileName);
        var startedPath = Path.Combine(docsPath, StartedMarkerFileName);
        var messages = new List<string>();

        if (File.Exists(finalJsonPath))
        {
            messages.Add($"Finale holdoutrapport bestaat al: {finalJsonPath}");
            return Rejected(finalJsonPath, finalMdPath, startedPath, messages);
        }

        if (File.Exists(startedPath))
        {
            messages.Add($"Holdout-started marker bestaat al: {startedPath}");
            return Rejected(finalJsonPath, finalMdPath, startedPath, messages);
        }

        var commit = string.IsNullOrWhiteSpace(gitCommit) ? TryReadGitCommit() : gitCommit.Trim();
        var matchingOptions = options ?? new AdaptiveLocationMatchingOptions();
        matchingOptions.Validate();
        var configurationHash = FrozenMatcherVerificationService.ComputeConfigurationHash(
            FrozenMatcherVerificationService.SnapshotOptions(matchingOptions));
        if (requireFrozenHoldoutIdentity &&
            !string.Equals(configurationHash, ExpectedConfigurationHashSha256, StringComparison.Ordinal))
        {
            messages.Add(
                $"Configuratiehash {configurationHash} wijkt af van bevroren {ExpectedConfigurationHashSha256}.");
            return Rejected(finalJsonPath, finalMdPath, startedPath, messages);
        }

        var holdoutPath = Path.Combine(docsPath, LocationMatchingBenchmarkService.HoldoutFileName);
        var manifestPath = Path.Combine(docsPath, LocationMatchingBenchmarkService.HoldoutManifestFileName);
        var labelsPath = Path.Combine(docsPath, LabelsFileName);
        if (!File.Exists(holdoutPath) || !File.Exists(manifestPath))
        {
            messages.Add("Holdoutbestand of manifest ontbreekt.");
            return Rejected(finalJsonPath, finalMdPath, startedPath, messages);
        }

        if (!File.Exists(labelsPath))
        {
            messages.Add($"Labelbestand ontbreekt: {LabelsFileName}");
            return Rejected(finalJsonPath, finalMdPath, startedPath, messages);
        }

        // Preflight load + label validation must succeed before the one-shot is consumed.
        if (!TryLoadHoldout(
                holdoutPath,
                manifestPath,
                requireFrozenHoldoutIdentity,
                out var rawCases,
                out var manifest,
                out var contentSha,
                out var loadError))
        {
            messages.Add(loadError);
            return Rejected(finalJsonPath, finalMdPath, startedPath, messages);
        }

        if (!TryLoadAndValidateLabels(labelsPath, rawCases, out var labeledCases, out var labelError))
        {
            messages.Add(labelError);
            return Rejected(finalJsonPath, finalMdPath, startedPath, messages);
        }

        File.WriteAllText(
            startedPath,
            JsonSerializer.Serialize(
                new LockedHoldoutStartedMarker
                {
                    StartedAt = DateTimeOffset.UtcNow,
                    GitCommit = commit,
                    Note = "Holdout evaluation started; do not re-run.",
                },
                JsonOptions),
            Encoding.UTF8);

        var scored = labeledCases
            .OrderBy(item => item.PerformanceId)
            .Select(item => ScoreCase(item, matchingOptions))
            .ToArray();
        var report = BuildReport(
            commit,
            gitTag,
            configurationHash,
            manifest.ContentSha256 ?? contentSha,
            contentSha,
            labeledCases,
            scored);
        WriteReports(finalJsonPath, finalMdPath, report);
        messages.Add($"HoldoutOpened=True Cases={report.CaseCount} Decision={report.Decision}");
        messages.Add(
            $"Precision={report.Precision} Coverage={report.Coverage} FP={report.FalsePositives} FN={report.FalseNegatives} WrongVisit={report.WrongVisitCandidateChoices}");
        return new LockedHoldoutEvaluationResult
        {
            Completed = true,
            ExitCode = 0,
            Decision = report.Decision,
            FinalJsonPath = finalJsonPath,
            FinalMarkdownPath = finalMdPath,
            StartedMarkerPath = startedPath,
            Report = report,
            Messages = messages,
        };
    }

    private static bool TryLoadHoldout(
        string holdoutPath,
        string manifestPath,
        bool requireFrozenHoldoutIdentity,
        out LocationMatchingBenchmarkCase[] cases,
        out HoldoutSamplingManifest manifest,
        out string contentSha,
        out string error)
    {
        cases = [];
        manifest = null!;
        contentSha = string.Empty;
        error = string.Empty;
        try
        {
            var holdoutFile = JsonSerializer.Deserialize<LocationMatchingHoldoutFile>(
                                  File.ReadAllText(holdoutPath),
                                  JsonOptions) ??
                              throw new InvalidOperationException("Holdout JSON is ongeldig.");
            manifest = JsonSerializer.Deserialize<HoldoutSamplingManifest>(
                           File.ReadAllText(manifestPath),
                           JsonOptions) ??
                       throw new InvalidOperationException("Holdoutmanifest is ongeldig.");

            if (!holdoutFile.Locked || !manifest.Locked)
            {
                error = "Holdout of manifest is niet locked.";
                return false;
            }

            cases = holdoutFile.Cases.ToArray();
            contentSha = LocationMatchingBenchmarkSampling.ComputeContentSha256(cases);
            if (requireFrozenHoldoutIdentity)
            {
                if (!string.Equals(contentSha, ExpectedHoldoutContentSha256, StringComparison.Ordinal))
                {
                    error =
                        $"Holdout ContentSha256 {contentSha} wijkt af van verwacht {ExpectedHoldoutContentSha256}.";
                    return false;
                }

                if (cases.Length != ExpectedCaseCount)
                {
                    error = $"Holdout casecount {cases.Length} != {ExpectedCaseCount}.";
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static bool TryLoadAndValidateLabels(
        string labelsPath,
        IReadOnlyList<LocationMatchingBenchmarkCase> holdoutCases,
        out LocationMatchingBenchmarkCase[] labeledCases,
        out string error)
    {
        labeledCases = [];
        error = string.Empty;
        try
        {
            var entries = JsonSerializer.Deserialize<List<CalibrationLabelEntry>>(
                              File.ReadAllText(labelsPath),
                              JsonOptions) ??
                          [];
            ValidateHoldoutLabelFile(entries, holdoutCases);
            var byId = entries.ToDictionary(item => item.PerformanceId);
            labeledCases = holdoutCases
                .Select(item =>
                {
                    var entry = byId[item.PerformanceId];
                    var visitIds = entry.ExpectedVisitStopIds is { Count: > 0 }
                        ? entry.ExpectedVisitStopIds
                            .Where(id => !string.IsNullOrWhiteSpace(id))
                            .Select(id => id.Trim())
                            .Distinct(StringComparer.Ordinal)
                            .ToArray()
                        : null;
                    return item with
                    {
                        Label = entry.Label,
                        ExpectedStopId = string.IsNullOrWhiteSpace(entry.ExpectedStopId)
                            ? null
                            : entry.ExpectedStopId.Trim(),
                        ExpectedVisitStopIds = visitIds,
                        ReviewerConfidence = entry.ReviewerConfidence,
                        ReviewerNote = entry.ReviewerNote,
                    };
                })
                .ToArray();
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static void ValidateHoldoutLabelFile(
        IReadOnlyList<CalibrationLabelEntry> entries,
        IReadOnlyList<LocationMatchingBenchmarkCase> holdoutCases)
    {
        var errors = new List<string>();
        if (entries.Count != holdoutCases.Count)
        {
            errors.Add($"Verwacht {holdoutCases.Count} labels, gevonden {entries.Count}.");
        }

        var expectedIds = holdoutCases.Select(item => item.PerformanceId).ToHashSet();
        var seen = new HashSet<long>();
        var stopIdsByPerformance = holdoutCases.ToDictionary(
            item => item.PerformanceId,
            item => item.Candidates
                .Select(candidate => candidate.StopId)
                .ToHashSet(StringComparer.Ordinal));

        foreach (var entry in entries)
        {
            if (!seen.Add(entry.PerformanceId))
            {
                errors.Add($"Dubbele PerformanceId: {entry.PerformanceId}.");
            }

            if (!expectedIds.Contains(entry.PerformanceId))
            {
                errors.Add($"Onbekende PerformanceId: {entry.PerformanceId}.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Label) ||
                !AllowedLabels.Contains(entry.Label, StringComparer.Ordinal))
            {
                errors.Add(
                    $"PerformanceId {entry.PerformanceId}: Label moet CorrectCandidate, NoValidCandidate of Ambiguous zijn.");
            }

            if (string.IsNullOrWhiteSpace(entry.ReviewerConfidence) ||
                !AllowedConfidence.Contains(entry.ReviewerConfidence, StringComparer.Ordinal))
            {
                errors.Add(
                    $"PerformanceId {entry.PerformanceId}: ReviewerConfidence moet High, Medium of Low zijn.");
            }

            var stopId = string.IsNullOrWhiteSpace(entry.ExpectedStopId)
                ? null
                : entry.ExpectedStopId.Trim();
            var visitIds = entry.ExpectedVisitStopIds is { Count: > 0 }
                ? entry.ExpectedVisitStopIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
                : Array.Empty<string>();

            if (string.Equals(entry.Label, "CorrectCandidate", StringComparison.Ordinal))
            {
                if (visitIds.Length == 0 && stopId is null)
                {
                    errors.Add(
                        $"PerformanceId {entry.PerformanceId}: CorrectCandidate vereist ExpectedVisitStopIds of ExpectedStopId voor precies één bezoek.");
                }

                foreach (var visitId in visitIds)
                {
                    if (!stopIdsByPerformance[entry.PerformanceId].Contains(visitId))
                    {
                        errors.Add(
                            $"PerformanceId {entry.PerformanceId}: ExpectedVisitStopId '{visitId}' bestaat niet bij deze case.");
                    }
                }

                if (stopId is not null &&
                    visitIds.Length == 0 &&
                    !stopIdsByPerformance[entry.PerformanceId].Contains(stopId))
                {
                    errors.Add(
                        $"PerformanceId {entry.PerformanceId}: ExpectedStopId '{stopId}' bestaat niet bij deze case.");
                }
            }
            else if (entry.Label is "NoValidCandidate" or "Ambiguous")
            {
                if (stopId is not null || visitIds.Length > 0)
                {
                    errors.Add(
                        $"PerformanceId {entry.PerformanceId}: {entry.Label} vereist geen verwacht bezoek.");
                }
            }
        }

        foreach (var missing in expectedIds.Where(id => !seen.Contains(id)).OrderBy(id => id))
        {
            errors.Add($"Ontbrekende PerformanceId: {missing}.");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Holdoutlabels ongeldig of onvolledig (one-shot niet verbruikt):" +
                Environment.NewLine +
                string.Join(Environment.NewLine, errors.Select(item => "- " + item)));
        }
    }

    internal static string Decide(
        double precision,
        int wrongVisitCandidateChoices,
        bool systematicFalsePositivePattern)
    {
        if (precision < 0.90 ||
            systematicFalsePositivePattern ||
            wrongVisitCandidateChoices > 1)
        {
            return "NO-GO";
        }

        if (precision >= 0.95)
        {
            return "GO";
        }

        return "CONDITIONAL GO";
    }

    internal static bool DetectSystematicFalsePositives(IReadOnlyList<LockedHoldoutErrorRow> errors)
    {
        var fps = errors
            .Where(item => item.Category.StartsWith("FP_", StringComparison.Ordinal))
            .ToArray();
        if (fps.Length < 2)
        {
            return false;
        }

        return fps
            .GroupBy(item => item.Category, StringComparer.Ordinal)
            .Any(group => group.Count() >= 2);
    }

    private static LockedHoldoutEvaluationResult Rejected(
        string finalJsonPath,
        string finalMdPath,
        string startedPath,
        List<string> messages) =>
        new()
        {
            Completed = false,
            ExitCode = 1,
            Decision = "REJECTED",
            FinalJsonPath = finalJsonPath,
            FinalMarkdownPath = finalMdPath,
            StartedMarkerPath = startedPath,
            Report = null,
            Messages = messages,
        };

    private static ScoredCase ScoreCase(
        LocationMatchingBenchmarkCase item,
        AdaptiveLocationMatchingOptions options)
    {
        var prediction = OfflineHybridPredictor.Predict(item, options, recovery: true);
        var visits = OfflineVisitMerge.Merge(item.Candidates, options);
        var best = visits
            .Select(visit =>
            {
                var overlap = OfflineVisitMerge.OverlapMinutes(
                    item.Start,
                    item.End,
                    visit.Arrival,
                    visit.Departure);
                var performanceMinutes = Math.Max(
                    1,
                    (int)Math.Round((item.End - item.Start).TotalMinutes, MidpointRounding.AwayFromZero));
                return (
                    Visit: visit,
                    Overlap: overlap,
                    OverlapPercent: 100d * overlap / performanceMinutes,
                    Distance: visit.DistanceMeters);
            })
            .OrderByDescending(entry => entry.Overlap)
            .ThenBy(entry => entry.Distance ?? double.MaxValue)
            .FirstOrDefault();

        var distanceZone = ClassifyDistanceZone(best.Distance);
        var overlapZone = ClassifyOverlapZone(
            best.Visit is null ? null : (int?)Math.Round((double)best.Overlap, MidpointRounding.AwayFromZero),
            best.Visit is null ? null : best.OverlapPercent);

        var label = item.Label!;
        var correctAccepted = false;
        var falsePositive = false;
        var falseNegative = false;
        var wrongVisit = false;
        string? category = null;

        if (string.Equals(label, "CorrectCandidate", StringComparison.Ordinal))
        {
            if (!prediction.Accepted)
            {
                falseNegative = true;
                category = "FN_CorrectCandidate";
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
                category = "WrongVisitCandidate";
            }
        }
        else if (label is "NoValidCandidate" or "Ambiguous")
        {
            if (prediction.Accepted)
            {
                falsePositive = true;
                category = label == "Ambiguous" ? "FP_Ambiguous" : "FP_NoValidCandidate";
            }
        }

        return new ScoredCase(
            item,
            prediction,
            correctAccepted,
            falsePositive,
            falseNegative,
            wrongVisit,
            category,
            distanceZone,
            overlapZone);
    }

    private static LockedHoldoutFinalReport BuildReport(
        string commit,
        string? gitTag,
        string configurationHash,
        string manifestHash,
        string contentSha,
        IReadOnlyList<LocationMatchingBenchmarkCase> cases,
        IReadOnlyList<ScoredCase> scored)
    {
        var accepted = scored.Count(item => item.Prediction.Accepted);
        var correct = scored.Count(item => item.CorrectAccepted);
        var fp = scored.Count(item => item.FalsePositive);
        var fn = scored.Count(item => item.FalseNegative);
        var wrong = scored.Count(item => item.WrongVisit);
        var abstentions = scored.Count(item => !item.Prediction.Accepted);
        var precision = Round(accepted == 0 ? 0 : correct / (double)accepted);
        var coverage = Round(scored.Count == 0 ? 0 : accepted / (double)scored.Count);

        var errors = scored
            .Where(item => item.ErrorCategory is not null)
            .Select(item => new LockedHoldoutErrorRow
            {
                PerformanceId = item.Case.PerformanceId,
                Label = item.Case.Label!,
                Category = item.ErrorCategory!,
                PredictedDecision = item.Prediction.Decision,
                PredictedStopId = item.Prediction.StopId,
                ReviewerConfidence = item.Case.ReviewerConfidence,
                DistanceZone = item.DistanceZone,
                OverlapZone = item.OverlapZone,
                GeocodeQuality = item.Case.GeocodeQuality.ToString(),
                Diagnostics = string.Create(
                    CultureInfo.InvariantCulture,
                    $"status={item.Case.ExistingMatchStatus};sources={item.Prediction.SourceStopIds.Count};recovery={item.Prediction.UsedRecovery}"),
            })
            .ToArray();

        var systematic = DetectSystematicFalsePositives(errors);
        var decision = Decide(precision, wrong, systematic);
        var notes = new List<string>
        {
            "Offline-only holdoutevaluatie; geen Plenion/Powerfleet/Geoapify-toegang.",
            "Coverage is informatief en geen zelfstandig afkeurcriterium.",
            "Holdout: 59 cases, 2025-10-01 t/m 2025-12-31, één technieker.",
        };
        if (systematic)
        {
            notes.Add("Systematisch false-positivepatroon gedetecteerd (zelfde FP-categorie ≥2).");
        }

        if (wrong == 1)
        {
            notes.Add("Exact één verkeerde VisitCandidate; GO blijft mogelijk indien verklaarbaar.");
        }

        var labelDistribution = cases
            .GroupBy(item => item.Label!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var high = scored
            .Where(item => string.Equals(item.Case.ReviewerConfidence, "High", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return new LockedHoldoutFinalReport
        {
            EvaluatedAt = DateTimeOffset.UtcNow,
            GitCommit = commit,
            GitTag = gitTag,
            ConfigurationHashSha256 = configurationHash,
            HoldoutManifestHashSha256 = manifestHash,
            HoldoutContentSha256 = contentSha,
            CaseCount = scored.Count,
            LabelDistribution = labelDistribution,
            AcceptedMatches = accepted,
            CorrectAcceptedMatches = correct,
            Precision = precision,
            Coverage = coverage,
            FalsePositives = fp,
            FalseNegatives = fn,
            WrongVisitCandidateChoices = wrong,
            Abstentions = abstentions,
            HighConfidence = high.Length == 0 ? null : ToSlice(high),
            ByDistanceZone = SliceBy(scored, item => item.DistanceZone),
            ByOverlapZone = SliceBy(scored, item => item.OverlapZone),
            ByGeocodeQuality = SliceBy(scored, item => item.Case.GeocodeQuality.ToString()),
            Errors = errors,
            ErrorCategories = errors
                .GroupBy(item => item.Category, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            SystematicFalsePositivePattern = systematic,
            Decision = decision,
            DecisionNotes = notes,
            ExternalDataAccessed = false,
            HoldoutOpened = true,
        };
    }

    private static Dictionary<string, FrozenMatcherMetricSlice> SliceBy(
        IReadOnlyList<ScoredCase> scored,
        Func<ScoredCase, string> keySelector) =>
        scored
            .GroupBy(keySelector, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => ToSlice(group.ToArray()), StringComparer.Ordinal);

    private static FrozenMatcherMetricSlice ToSlice(IReadOnlyList<ScoredCase> scored)
    {
        var accepted = scored.Count(item => item.Prediction.Accepted);
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

    private static void WriteReports(
        string finalJsonPath,
        string finalMdPath,
        LockedHoldoutFinalReport report)
    {
        File.WriteAllText(finalJsonPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        File.WriteAllText(finalMdPath, RenderMarkdown(report), Encoding.UTF8);
    }

    private static string RenderMarkdown(LockedHoldoutFinalReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Locked holdout final evaluation");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- EvaluatedAt: {report.EvaluatedAt:O}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- GitCommit: `{report.GitCommit}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- GitTag: `{report.GitTag ?? "(none)"}`");
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"- ConfigurationHashSha256: `{report.ConfigurationHashSha256}`");
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"- HoldoutManifestHashSha256: `{report.HoldoutManifestHashSha256}`");
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"- HoldoutContentSha256: `{report.HoldoutContentSha256}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Decision: **{report.Decision}**");
        sb.AppendLine();
        sb.AppendLine("## Metrics");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|---|---:|");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Cases | {report.CaseCount} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Accepted | {report.AcceptedMatches} |");
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"| Correct accepted | {report.CorrectAcceptedMatches} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Precision | {report.Precision:0.0000} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Coverage | {report.Coverage:0.0000} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| False positives | {report.FalsePositives} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| False negatives | {report.FalseNegatives} |");
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"| Wrong VisitCandidate | {report.WrongVisitCandidateChoices} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Abstentions | {report.Abstentions} |");
        sb.AppendLine();
        sb.AppendLine("## Label distribution");
        sb.AppendLine();
        foreach (var pair in report.LabelDistribution.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {pair.Key}: {pair.Value}");
        }

        sb.AppendLine();
        sb.AppendLine("## Error categories");
        sb.AppendLine();
        if (report.ErrorCategories.Count == 0)
        {
            sb.AppendLine("- (none)");
        }
        else
        {
            foreach (var pair in report.ErrorCategories.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {pair.Key}: {pair.Value}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Decision notes");
        sb.AppendLine();
        foreach (var note in report.DecisionNotes)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {note}");
        }

        sb.AppendLine();
        sb.AppendLine("## Errors");
        sb.AppendLine();
        if (report.Errors.Count == 0)
        {
            sb.AppendLine("- (none)");
        }
        else
        {
            foreach (var error in report.Errors)
            {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"- {error.PerformanceId}|{error.Category}|{error.Label}|{error.PredictedDecision}|{error.Diagnostics}");
            }
        }

        return sb.ToString();
    }

    private static string ClassifyDistanceZone(double? meters) =>
        meters switch
        {
            null => "Unknown",
            <= 100 => "Strong0To100",
            <= 250 => "Probable101To250",
            <= 500 => "Learned251To500",
            _ => "Beyond500",
        };

    private static string ClassifyOverlapZone(int? overlapMinutes, double? overlapPercent)
    {
        if (overlapMinutes is null || overlapPercent is null)
        {
            return "None";
        }

        if (overlapPercent >= 50 || overlapMinutes >= 30)
        {
            return "StrongOverlap";
        }

        if (overlapPercent >= 30 || overlapMinutes >= 4)
        {
            return "ModerateOverlap";
        }

        return "WeakOverlap";
    }

    private static double Round(double value) =>
        Math.Round(value, 4, MidpointRounding.AwayFromZero);

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

    private sealed record ScoredCase(
        LocationMatchingBenchmarkCase Case,
        OfflineHybridPredictor.Prediction Prediction,
        bool CorrectAccepted,
        bool FalsePositive,
        bool FalseNegative,
        bool WrongVisit,
        string? ErrorCategory,
        string DistanceZone,
        string OverlapZone);
}
