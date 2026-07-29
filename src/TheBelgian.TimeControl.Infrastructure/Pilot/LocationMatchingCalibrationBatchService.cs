using System.Globalization;
using System.Text;
using System.Text.Json;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal static class LocationMatchingCalibrationBatchService
{
    public const string ReviewPackMarkdownFileName = "calibration-review-pack.md";
    public const string ReviewPackJsonFileName = "calibration-review-pack.json";
    public const string LabelTemplateFileName = "calibration-labels.json";

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
    };

    public static CalibrationExportResult ExportReviewPack(string docsPath)
    {
        Directory.CreateDirectory(docsPath);
        var source = LocationMatchingBenchmarkService.LoadCalibrationCases(docsPath);
        if (source.Count != LocationMatchingBenchmarkSampling.CalibrationCaseCount)
        {
            throw new InvalidOperationException(
                $"Kalibratieset moet exact {LocationMatchingBenchmarkSampling.CalibrationCaseCount} cases bevatten; gevonden: {source.Count}.");
        }

        var ordered = LocationMatchingBenchmarkSampling.BlindReviewOrder(source);
        var cases = new List<CalibrationReviewCase>(ordered.Count);
        for (var index = 0; index < ordered.Count; index++)
        {
            cases.Add(ToReviewCase(ordered[index], index + 1));
        }

        var pack = new CalibrationReviewPack
        {
            DatasetRole = "calibration-review-pack",
            CaseCount = cases.Count,
            ExportedAt = DateTimeOffset.UtcNow,
            BlindNote =
                "Blind pack: no matcher status, score, ranking, or prediction. " +
                "Candidate order is by arrival then StopId.",
            Cases = cases,
        };

        var markdownPath = Path.Combine(docsPath, ReviewPackMarkdownFileName);
        var jsonPath = Path.Combine(docsPath, ReviewPackJsonFileName);
        var templatePath = Path.Combine(docsPath, LabelTemplateFileName);
        File.WriteAllText(markdownPath, ToMarkdown(pack), Encoding.UTF8);
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(pack, JsonOptions), Encoding.UTF8);
        File.WriteAllText(
            templatePath,
            JsonSerializer.Serialize(
                cases.Select(item => new CalibrationLabelEntry
                {
                    PerformanceId = item.PerformanceId,
                    Label = null,
                    ExpectedStopId = null,
                    ReviewerConfidence = null,
                    ReviewerNote = null,
                }).ToArray(),
                JsonOptions),
            Encoding.UTF8);

        return new CalibrationExportResult
        {
            MarkdownPath = markdownPath,
            JsonPath = jsonPath,
            TemplatePath = templatePath,
            CaseCount = cases.Count,
        };
    }

    public static CalibrationLabelImportResult ImportLabels(
        string docsPath,
        string labelsPath,
        int reviewer)
    {
        if (reviewer is not (1 or 2))
        {
            throw new ArgumentException("Reviewer moet 1 of 2 zijn.", nameof(reviewer));
        }

        if (!File.Exists(labelsPath))
        {
            throw new FileNotFoundException("Labelbestand niet gevonden.", labelsPath);
        }

        var calibration = LocationMatchingBenchmarkService.LoadCalibrationCases(docsPath).ToList();
        if (calibration.Count != LocationMatchingBenchmarkSampling.CalibrationCaseCount)
        {
            throw new InvalidOperationException(
                $"Kalibratieset moet exact {LocationMatchingBenchmarkSampling.CalibrationCaseCount} cases bevatten; gevonden: {calibration.Count}.");
        }

        List<CalibrationLabelEntry> entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<CalibrationLabelEntry>>(
                          File.ReadAllText(labelsPath),
                          JsonOptions) ??
                      [];
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Labelbestand is geen geldige JSON: {exception.Message}",
                exception);
        }

        ValidateLabelFile(entries, calibration);

        var byId = entries.ToDictionary(item => item.PerformanceId);
        var updated = new List<LocationMatchingBenchmarkCase>(calibration.Count);
        foreach (var item in calibration)
        {
            var entry = byId[item.PerformanceId];
            var stopId = string.IsNullOrWhiteSpace(entry.ExpectedStopId)
                ? null
                : entry.ExpectedStopId.Trim();
            var note = string.IsNullOrWhiteSpace(entry.ReviewerNote)
                ? null
                : entry.ReviewerNote.Trim();
            if (reviewer == 1)
            {
                var next = item with
                {
                    Label = entry.Label,
                    ExpectedStopId = stopId,
                    ReviewerConfidence = entry.ReviewerConfidence,
                    ReviewerNote = note,
                    IsCalibrationCase = true,
                    RequiresSecondReview = true,
                };
                updated.Add(WithAdjudication(next));
            }
            else
            {
                var next = item with
                {
                    SecondReviewLabel = entry.Label,
                    SecondReviewExpectedStopId = stopId,
                    SecondReviewerConfidence = entry.ReviewerConfidence,
                    SecondReviewerNote = note,
                    IsCalibrationCase = true,
                    RequiresSecondReview = true,
                };
                updated.Add(WithAdjudication(next));
            }
        }

        LocationMatchingBenchmarkService.SaveCalibrationAndDevelopmentCases(docsPath, updated);
        var saved = LocationMatchingBenchmarkService.LoadCalibrationCases(docsPath);
        return new CalibrationLabelImportResult
        {
            Reviewer = reviewer,
            ImportedCount = updated.Count,
            LabelsPath = Path.GetFullPath(labelsPath),
            CalibrationPath = Path.Combine(docsPath, LocationMatchingBenchmarkService.CalibrationFileName),
            Agreement = LocationMatchingBenchmarkSampling.ComputeLabelAgreement(saved),
        };
    }

    public static CalibrationLabelImportResult ResetReviewerLabels(
        string docsPath,
        int reviewer)
    {
        if (reviewer is not (1 or 2))
        {
            throw new ArgumentException("Reviewer moet 1 of 2 zijn.", nameof(reviewer));
        }

        var calibration = LocationMatchingBenchmarkService.LoadCalibrationCases(docsPath).ToList();
        if (calibration.Count != LocationMatchingBenchmarkSampling.CalibrationCaseCount)
        {
            throw new InvalidOperationException(
                $"Kalibratieset moet exact {LocationMatchingBenchmarkSampling.CalibrationCaseCount} cases bevatten; gevonden: {calibration.Count}.");
        }

        var updated = calibration.Select(item =>
        {
            if (reviewer == 1)
            {
                return WithAdjudication(item with
                {
                    Label = null,
                    ExpectedStopId = null,
                    ReviewerConfidence = null,
                    ReviewerNote = null,
                    AdjudicationStatus = null,
                    IsCalibrationCase = true,
                    RequiresSecondReview = true,
                });
            }

            return WithAdjudication(item with
            {
                SecondReviewLabel = null,
                SecondReviewExpectedStopId = null,
                SecondReviewerConfidence = null,
                SecondReviewerNote = null,
                AdjudicationStatus = null,
                IsCalibrationCase = true,
                RequiresSecondReview = true,
            });
        }).ToList();

        LocationMatchingBenchmarkService.SaveCalibrationAndDevelopmentCases(docsPath, updated);
        var saved = LocationMatchingBenchmarkService.LoadCalibrationCases(docsPath);
        return new CalibrationLabelImportResult
        {
            Reviewer = reviewer,
            ImportedCount = 0,
            LabelsPath = "(reset)",
            CalibrationPath = Path.Combine(docsPath, LocationMatchingBenchmarkService.CalibrationFileName),
            Agreement = LocationMatchingBenchmarkSampling.ComputeLabelAgreement(saved),
        };
    }

    public static string WriteEmptyReviewerTemplate(
        string docsPath,
        string fileName)
    {
        var calibration = LocationMatchingBenchmarkService.LoadCalibrationCases(docsPath);
        if (calibration.Count != LocationMatchingBenchmarkSampling.CalibrationCaseCount)
        {
            throw new InvalidOperationException(
                $"Kalibratieset moet exact {LocationMatchingBenchmarkSampling.CalibrationCaseCount} cases bevatten; gevonden: {calibration.Count}.");
        }

        var path = Path.Combine(docsPath, fileName);
        var template = calibration
            .OrderBy(item => item.PerformanceId)
            .Select(item => new CalibrationLabelEntry
            {
                PerformanceId = item.PerformanceId,
                Label = null,
                ExpectedStopId = null,
                ReviewerConfidence = null,
                ReviewerNote = null,
            })
            .ToArray();
        File.WriteAllText(path, JsonSerializer.Serialize(template, JsonOptions), Encoding.UTF8);
        return path;
    }

    internal static void ValidateLabelFile(
        IReadOnlyList<CalibrationLabelEntry> entries,
        IReadOnlyList<LocationMatchingBenchmarkCase> calibration)
    {
        var errors = new List<string>();
        if (entries.Count != LocationMatchingBenchmarkSampling.CalibrationCaseCount)
        {
            errors.Add(
                $"Verwacht {LocationMatchingBenchmarkSampling.CalibrationCaseCount} labels, gevonden {entries.Count}.");
        }

        var expectedIds = calibration.Select(item => item.PerformanceId).ToHashSet();
        var seen = new HashSet<long>();
        var stopIdsByPerformance = calibration.ToDictionary(
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
            if (string.Equals(entry.Label, "CorrectCandidate", StringComparison.Ordinal))
            {
                if (stopId is null)
                {
                    errors.Add(
                        $"PerformanceId {entry.PerformanceId}: CorrectCandidate vereist ExpectedStopId.");
                }
                else if (!stopIdsByPerformance[entry.PerformanceId].Contains(stopId))
                {
                    errors.Add(
                        $"PerformanceId {entry.PerformanceId}: ExpectedStopId '{stopId}' bestaat niet bij deze case.");
                }
            }
            else if (entry.Label is "NoValidCandidate" or "Ambiguous")
            {
                if (stopId is not null)
                {
                    errors.Add(
                        $"PerformanceId {entry.PerformanceId}: {entry.Label} vereist ExpectedStopId = null.");
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
                "Labelimport geweigerd (niets opgeslagen):" +
                Environment.NewLine +
                string.Join(Environment.NewLine, errors.Select(item => "- " + item)));
        }
    }

    private static LocationMatchingBenchmarkCase WithAdjudication(
        LocationMatchingBenchmarkCase item)
    {
        if (string.IsNullOrWhiteSpace(item.Label) ||
            string.IsNullOrWhiteSpace(item.SecondReviewLabel))
        {
            return item with { AdjudicationStatus = null };
        }

        var agree = string.Equals(item.Label, item.SecondReviewLabel, StringComparison.Ordinal) &&
                    string.Equals(item.ExpectedStopId, item.SecondReviewExpectedStopId, StringComparison.Ordinal);
        return item with { AdjudicationStatus = agree ? "Agree" : "Disagreement" };
    }

    private static CalibrationReviewCase ToReviewCase(
        LocationMatchingBenchmarkCase source,
        int caseNumber)
    {
        var performanceMinutes = Math.Max(
            1,
            (int)Math.Round(
                (source.End - source.Start).TotalMinutes,
                MidpointRounding.AwayFromZero));
        var candidates = source.Candidates
            .OrderBy(item => item.Arrival)
            .ThenBy(item => item.StopId, StringComparer.Ordinal)
            .Select(candidate =>
            {
                var dwell = Math.Max(
                    0,
                    (int)Math.Round(
                        (candidate.Departure - candidate.Arrival).TotalMinutes,
                        MidpointRounding.AwayFromZero));
                return new CalibrationReviewCandidate
                {
                    StopId = candidate.StopId,
                    Address = candidate.Address,
                    DistanceMeters = candidate.DistanceMeters is null
                        ? null
                        : Math.Round(candidate.DistanceMeters.Value, 1),
                    Arrival = candidate.Arrival,
                    Departure = candidate.Departure,
                    DwellMinutes = dwell,
                    OverlapMinutes = candidate.OverlapMinutes,
                    OverlapPercent = Math.Round(
                        100d * candidate.OverlapMinutes / performanceMinutes,
                        1),
                    StartDifferenceMinutes = candidate.StartDifferenceMinutes,
                    EndDifferenceMinutes = candidate.EndDifferenceMinutes,
                };
            })
            .ToArray();

        return new CalibrationReviewCase
        {
            CaseNumber = caseNumber,
            PerformanceId = source.PerformanceId,
            Technician = source.Technician,
            Date = source.Date,
            PerformanceStart = source.Start,
            PerformanceEnd = source.End,
            Lacleunik = source.Lacleunik,
            PlenionAddress = source.PlenionAddress,
            GeocodeQuality = source.GeocodeQuality.ToString(),
            ActivityType = source.ActivityType,
            PreviousPerformance = source.PreviousPerformance,
            NextPerformance = source.NextPerformance,
            LocationExposure = source.LocationExposure ?? "UnseenLocation",
            Candidates = candidates,
        };
    }

    private static string ToMarkdown(CalibrationReviewPack pack)
    {
        var lines = new List<string>
        {
            "# Calibration review pack",
            string.Empty,
            $"Cases: {pack.CaseCount}",
            $"ExportedAt: {pack.ExportedAt:O}",
            string.Empty,
            pack.BlindNote,
            string.Empty,
        };

        foreach (var item in pack.Cases)
        {
            lines.Add($"## Case {item.CaseNumber}");
            lines.Add(string.Empty);
            lines.Add($"- PerformanceId: `{item.PerformanceId}`");
            lines.Add($"- Technician: {item.Technician}");
            lines.Add(
                $"- Date: {item.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
            lines.Add(
                $"- Performance: {item.PerformanceStart:HH:mm} – {item.PerformanceEnd:HH:mm}");
            lines.Add($"- LACLEUNIK: {(item.Lacleunik ?? "—")}");
            lines.Add($"- Plenion address: {item.PlenionAddress}");
            lines.Add($"- Geocode quality: {item.GeocodeQuality}");
            lines.Add($"- Activity type: {(item.ActivityType ?? "—")}");
            lines.Add($"- Previous performance: {(item.PreviousPerformance ?? "—")}");
            lines.Add($"- Next performance: {(item.NextPerformance ?? "—")}");
            lines.Add($"- Location exposure: {item.LocationExposure}");
            lines.Add(string.Empty);
            if (item.Candidates.Count == 0)
            {
                lines.Add("No candidate stops.");
                lines.Add(string.Empty);
                continue;
            }

            lines.Add(
                "| StopId | Address | Distance m | Arrival | Departure | Dwell min | Overlap min | Overlap % | Δ start | Δ end |");
            lines.Add("| --- | --- | ---: | --- | --- | ---: | ---: | ---: | ---: | ---: |");
            foreach (var candidate in item.Candidates)
            {
                lines.Add(
                    $"| `{candidate.StopId}` | {EscapePipe(candidate.Address ?? "—")} | " +
                    $"{FormatDistance(candidate.DistanceMeters)} | {candidate.Arrival:HH:mm} | " +
                    $"{candidate.Departure:HH:mm} | {candidate.DwellMinutes} | " +
                    $"{candidate.OverlapMinutes} | {candidate.OverlapPercent.ToString("0.0", CultureInfo.InvariantCulture)} | " +
                    $"{candidate.StartDifferenceMinutes} | {candidate.EndDifferenceMinutes} |");
            }

            lines.Add(string.Empty);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string EscapePipe(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal);

    private static string FormatDistance(double? meters) =>
        meters is null
            ? "—"
            : meters.Value.ToString("0.0", CultureInfo.InvariantCulture);
}
