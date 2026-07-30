using System.Globalization;
using System.Text;
using System.Text.Json;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

/// <summary>
/// Blind holdout review-pack export. Reads locked holdout locally; never predicts matches.
/// </summary>
internal static class LockedHoldoutReviewPackService
{
    public const string ReviewPackMarkdownFileName = "location-matching-holdout-review-pack.md";
    public const string LabelsFileName = "location-matching-holdout-labels.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static LockedHoldoutExportResult ExportReviewPack(
        string docsPath,
        bool requireFrozenHoldoutIdentity = true)
    {
        using var offline = OfflineOnlyGuard.Enter();
        Directory.CreateDirectory(docsPath);

        var holdoutPath = Path.Combine(docsPath, LocationMatchingBenchmarkService.HoldoutFileName);
        var manifestPath = Path.Combine(docsPath, LocationMatchingBenchmarkService.HoldoutManifestFileName);
        if (!File.Exists(holdoutPath) || !File.Exists(manifestPath))
        {
            throw new FileNotFoundException("Locked holdout of manifest ontbreekt.");
        }

        var holdoutFile = JsonSerializer.Deserialize<LocationMatchingHoldoutFile>(
                              File.ReadAllText(holdoutPath),
                              JsonOptions) ??
                          throw new InvalidOperationException("Holdout JSON is ongeldig.");
        var manifest = JsonSerializer.Deserialize<HoldoutSamplingManifest>(
                           File.ReadAllText(manifestPath),
                           JsonOptions) ??
                       throw new InvalidOperationException("Holdoutmanifest is ongeldig.");
        if (!holdoutFile.Locked || !manifest.Locked)
        {
            throw new InvalidOperationException("Holdout of manifest is niet locked.");
        }

        var source = holdoutFile.Cases
            .OrderBy(item => item.PerformanceId)
            .ToArray();
        var contentSha = LocationMatchingBenchmarkSampling.ComputeContentSha256(source);
        if (requireFrozenHoldoutIdentity)
        {
            if (!string.Equals(
                    contentSha,
                    LockedHoldoutEvaluationService.ExpectedHoldoutContentSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Holdout ContentSha256 {contentSha} wijkt af van verwacht " +
                    $"{LockedHoldoutEvaluationService.ExpectedHoldoutContentSha256}.");
            }

            if (source.Length != LockedHoldoutEvaluationService.ExpectedCaseCount)
            {
                throw new InvalidOperationException(
                    $"Holdout casecount {source.Length} != {LockedHoldoutEvaluationService.ExpectedCaseCount}.");
            }
        }

        var options = new AdaptiveLocationMatchingOptions();
        options.Validate();
        var cases = new List<LockedHoldoutReviewCase>(source.Length);
        for (var index = 0; index < source.Length; index++)
        {
            cases.Add(ToReviewCase(source[index], index + 1, options));
        }

        var pack = new LockedHoldoutReviewPack
        {
            DatasetRole = "locked-holdout-review-pack",
            CaseCount = cases.Count,
            ExportedAt = DateTimeOffset.UtcNow,
            BlindNote =
                "Blind pack: source evidence only. No matcher status, score, ranking, recovery, " +
                "or prediction. Candidate order is by arrival then StopId. PossibleVisitGroups are " +
                "objective consecutive-stop groupings by time/space proximity only.",
            HoldoutContentSha256 = contentSha,
            Cases = cases,
        };

        var markdownPath = Path.Combine(docsPath, ReviewPackMarkdownFileName);
        var labelsPath = Path.Combine(docsPath, LabelsFileName);
        File.WriteAllText(markdownPath, ToMarkdown(pack), Encoding.UTF8);
        File.WriteAllText(
            labelsPath,
            JsonSerializer.Serialize(
                cases.Select(item => new CalibrationLabelEntry
                {
                    PerformanceId = item.PerformanceId,
                    Label = null,
                    ExpectedStopId = null,
                    ExpectedVisitStopIds = null,
                    ReviewerConfidence = null,
                    ReviewerNote = null,
                }).ToArray(),
                JsonOptions),
            Encoding.UTF8);

        return new LockedHoldoutExportResult
        {
            MarkdownPath = markdownPath,
            LabelsPath = labelsPath,
            CaseCount = cases.Count,
            HoldoutContentSha256 = contentSha,
        };
    }

    private static LockedHoldoutReviewCase ToReviewCase(
        LocationMatchingBenchmarkCase source,
        int caseNumber,
        AdaptiveLocationMatchingOptions options)
    {
        var performanceMinutes = Math.Max(
            1,
            (int)Math.Round((source.End - source.Start).TotalMinutes, MidpointRounding.AwayFromZero));
        var candidates = source.Candidates
            .OrderBy(item => item.Arrival)
            .ThenBy(item => item.StopId, StringComparer.Ordinal)
            .Select(candidate => ToCandidate(candidate, performanceMinutes))
            .ToArray();

        var visits = OfflineVisitMerge.Merge(source.Candidates, options)
            .OrderBy(item => item.Arrival)
            .ThenBy(item => item.StopIds[0], StringComparer.Ordinal)
            .Select(visit =>
            {
                var overlap = OfflineVisitMerge.OverlapMinutes(
                    source.Start,
                    source.End,
                    visit.Arrival,
                    visit.Departure);
                var dwell = Math.Max(
                    0,
                    (int)Math.Round(
                        (visit.Departure - visit.Arrival).TotalMinutes,
                        MidpointRounding.AwayFromZero));
                var address = source.Candidates
                    .Where(item => visit.StopIds.Contains(item.StopId, StringComparer.Ordinal))
                    .Select(item => item.Address)
                    .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
                return new LockedHoldoutReviewVisitGroup
                {
                    ConstituentStopIds = visit.StopIds.ToArray(),
                    Address = address,
                    DistanceMeters = visit.DistanceMeters is null
                        ? null
                        : Math.Round(visit.DistanceMeters.Value, 1),
                    Arrival = visit.Arrival,
                    Departure = visit.Departure,
                    DwellMinutes = dwell,
                    OverlapMinutes = overlap,
                    OverlapPercent = Math.Round(100d * overlap / performanceMinutes, 1),
                    StartDifferenceMinutes = (int)Math.Round(
                        (visit.Arrival - source.Start).TotalMinutes,
                        MidpointRounding.AwayFromZero),
                    EndDifferenceMinutes = (int)Math.Round(
                        (visit.Departure - source.End).TotalMinutes,
                        MidpointRounding.AwayFromZero),
                };
            })
            .ToArray();

        return new LockedHoldoutReviewCase
        {
            CaseNumber = caseNumber,
            PerformanceId = source.PerformanceId,
            Technician = source.Technician,
            Date = source.Date,
            PerformanceStart = source.Start,
            PerformanceEnd = source.End,
            Lacleunik = source.Lacleunik,
            PlenionAddress = source.PlenionAddress,
            PreviousPerformance = source.PreviousPerformance,
            NextPerformance = source.NextPerformance,
            Candidates = candidates,
            PossibleVisitGroups = visits,
        };
    }

    private static LockedHoldoutReviewCandidate ToCandidate(
        LocationMatchingBenchmarkCandidate candidate,
        int performanceMinutes)
    {
        var dwell = Math.Max(
            0,
            (int)Math.Round(
                (candidate.Departure - candidate.Arrival).TotalMinutes,
                MidpointRounding.AwayFromZero));
        return new LockedHoldoutReviewCandidate
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
            OverlapPercent = Math.Round(100d * candidate.OverlapMinutes / performanceMinutes, 1),
            StartDifferenceMinutes = candidate.StartDifferenceMinutes,
            EndDifferenceMinutes = candidate.EndDifferenceMinutes,
        };
    }

    private static string ToMarkdown(LockedHoldoutReviewPack pack)
    {
        var lines = new List<string>
        {
            "# Locked holdout review pack",
            string.Empty,
            $"Cases: {pack.CaseCount}",
            $"ExportedAt: {pack.ExportedAt:O}",
            $"HoldoutContentSha256: `{pack.HoldoutContentSha256}`",
            string.Empty,
            pack.BlindNote,
            string.Empty,
            "Label in `location-matching-holdout-labels.json` as CorrectCandidate, " +
            "NoValidCandidate, or Ambiguous. CorrectCandidate requires ExpectedVisitStopIds " +
            "(or ExpectedStopId) for exactly one visit.",
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
            lines.Add($"- Previous performance: {(item.PreviousPerformance ?? "—")}");
            lines.Add($"- Next performance: {(item.NextPerformance ?? "—")}");
            lines.Add(string.Empty);

            if (item.Candidates.Count == 0)
            {
                lines.Add("No candidate stops.");
                lines.Add(string.Empty);
                continue;
            }

            lines.Add("### Candidate stops");
            lines.Add(string.Empty);
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
            lines.Add("### Possible visit groups");
            lines.Add(string.Empty);
            if (item.PossibleVisitGroups.Count == 0)
            {
                lines.Add("No visit groups.");
            }
            else
            {
                lines.Add(
                    "| ConstituentStopIds | Address | Distance m | Arrival | Departure | Dwell min | Overlap min | Overlap % | Δ start | Δ end |");
                lines.Add("| --- | --- | ---: | --- | --- | ---: | ---: | ---: | ---: | ---: |");
                foreach (var visit in item.PossibleVisitGroups)
                {
                    var ids = string.Join(", ", visit.ConstituentStopIds.Select(id => $"`{id}`"));
                    lines.Add(
                        $"| {ids} | {EscapePipe(visit.Address ?? "—")} | " +
                        $"{FormatDistance(visit.DistanceMeters)} | {visit.Arrival:HH:mm} | " +
                        $"{visit.Departure:HH:mm} | {visit.DwellMinutes} | " +
                        $"{visit.OverlapMinutes} | {visit.OverlapPercent.ToString("0.0", CultureInfo.InvariantCulture)} | " +
                        $"{visit.StartDifferenceMinutes} | {visit.EndDifferenceMinutes} |");
                }
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
