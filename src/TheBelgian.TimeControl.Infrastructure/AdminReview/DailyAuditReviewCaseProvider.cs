using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.AdminReview;

internal sealed class DailyAuditReviewCaseProvider(IConfiguration configuration)
{
    internal const string DefaultAuditPath =
        @"C:\Temp\timecontrol-daily-hours-audit-2026-07-final.json";

    private readonly object _gate = new();
    private IReadOnlyList<DailyReviewCase>? _cache;

    public string AuditPath =>
        configuration["DailyReviewData:AuditJsonPath"] ?? DefaultAuditPath;

    public Task<IReadOnlyList<DailyReviewCase>> GetCasesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _cache ??= Load();
            return Task.FromResult(_cache);
        }
    }

    private IReadOnlyList<DailyReviewCase> Load()
    {
        if (!File.Exists(AuditPath))
        {
            return [];
        }

        var createdAt = new DateTimeOffset(File.GetLastWriteTimeUtc(AuditPath), TimeSpan.Zero);
        return DailyReviewCaseMapper.Map(File.ReadAllText(AuditPath), createdAt);
    }
}

internal static class DailyReviewCaseMapper
{
    internal const string AlgorithmVersion = "DailyBoundaryAudit-WorksiteSession-v1-2026-07";

    public static IReadOnlyList<DailyReviewCase> Map(
        string json,
        DateTimeOffset createdAt)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("De Daily Boundary-export moet een JSON-array zijn.");
        }

        var result = new List<DailyReviewCase>();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            var confirmed = Number(row, "TotalConfirmedDeviation") ?? 0;
            var potential = Number(row, "TotalReviewPotentialDeviation") ?? 0;
            var reviewStatus = Text(row, "ReviewStatus") ?? "Unresolved";
            var dataQuality = reviewStatus.Contains("Unresolved", StringComparison.OrdinalIgnoreCase) ||
                              reviewStatus.Contains("Insufficient", StringComparison.OrdinalIgnoreCase) ||
                              reviewStatus.Contains("Ambiguous", StringComparison.OrdinalIgnoreCase) ||
                              reviewStatus.Contains("NoTrackAndTrace", StringComparison.OrdinalIgnoreCase);
            if (confirmed <= 0 && potential <= 0 && !dataQuality)
            {
                continue;
            }

            var date = DateOnly.Parse(RequiredText(row, "Date"), CultureInfo.InvariantCulture);
            var technician = RequiredText(row, "Technician");
            var first = MapBoundary(row, "First", "FirstEvidence", true);
            var last = MapBoundary(row, "Last", "LastEvidence", false);
            var evidence = first.IsReliable && last.IsReliable
                ? DailyReviewEvidenceLevel.Complete
                : first.IsReliable || last.IsReliable
                    ? DailyReviewEvidenceLevel.Partial
                    : DailyReviewEvidenceLevel.Insufficient;

            result.Add(new DailyReviewCase(
                CaseId: CreateCaseId(date, technician),
                TechnicianId: null,
                Technician: technician,
                Date: date,
                First: first,
                Last: last,
                EvidenceLevel: evidence,
                AuditReviewStatus: reviewStatus,
                AlgorithmVersion: AlgorithmVersion,
                CreatedAt: createdAt,
                EvidenceSnapshotJson: row.GetRawText(),
                Decision: new DailyReviewDecision(
                    DailyReviewWorkflowStatus.Open,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null),
                TripContext: DailyReviewTripContextMapper.Map(row, first, last)));
        }

        return result
            .OrderByDescending(item => item.MaximumAbsoluteDifferenceMinutes)
            .ThenBy(item => item.Date)
            .ThenBy(item => item.Technician, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static DailyReviewBoundaryEvidence MapBoundary(
        JsonElement row,
        string boundaryProperty,
        string evidenceProperty,
        bool isFirst)
    {
        var boundary = row.GetProperty(boundaryProperty);
        var evidence = row.GetProperty(evidenceProperty);
        var performanceId = boundary.GetProperty("PerformanceId").GetInt64();
        var performance = FindPerformance(row, performanceId);
        var plenion = DateTimeOffset.Parse(
            RequiredText(evidence, "PlenionBoundaryTime"),
            CultureInfo.InvariantCulture);
        var gps = FirstDate(
            evidence,
            "EffectiveBoundaryTime",
            "ContextBoundaryTime",
            "ExactSiteBoundaryTime");
        double? signed = null;
        if (gps is not null)
        {
            signed = isFirst
                ? (gps.Value - plenion).TotalMinutes
                : (plenion - gps.Value).TotalMinutes;
        }

        return new DailyReviewBoundaryEvidence(
            Side: isFirst ? "Start" : "Einde",
            PerformanceId: performanceId,
            Customer: performance is { } p ? Text(p, "Customer") : null,
            Address: Text(boundary, "PlenionAddress"),
            PlenionTime: plenion,
            GpsTime: gps,
            SignedDifferenceMinutes: signed,
            IsReliable: Boolean(evidence, "IsReliable"),
            EvidenceType: EvidenceType(evidence),
            MatcherStatus: Text(boundary, "MatcherStatus") ?? "Unresolved",
            Score: Number(boundary, "Score"),
            DistanceMeters: Number(boundary, "DistanceMeters"),
            OverlapMinutes: Integer(boundary, "OverlapMinutes"),
            SelectedVisitId: Text(boundary, "SelectedVisitId"),
            TechnicalReason: Text(evidence, "Reason"));
    }

    private static JsonElement? FindPerformance(JsonElement row, long performanceId)
    {
        if (!row.TryGetProperty("Performances", out var performances))
        {
            return null;
        }

        foreach (var performance in performances.EnumerateArray())
        {
            if (performance.TryGetProperty("PerformanceId", out var id) &&
                id.GetInt64() == performanceId)
            {
                return performance;
            }
        }

        return null;
    }

    private static DateTimeOffset? FirstDate(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var value = Text(element, name);
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string EvidenceType(JsonElement evidence)
    {
        if (!evidence.TryGetProperty("EvidenceType", out var value))
        {
            return "Unresolved";
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? "Unresolved";
        }

        return value.GetInt32() switch
        {
            0 => "ExactSite",
            1 => "ContextSupported",
            2 => "Review",
            4 => "WorksiteSession",
            _ => "Unresolved",
        };
    }

    private static string CreateCaseId(DateOnly date, string technician)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{date:yyyy-MM-dd}|{technician}")));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{date:yyyyMMdd}-{Convert.ToHexString(bytes)[..12].ToLowerInvariant()}");
    }

    private static string RequiredText(JsonElement element, string name) =>
        Text(element, name) ?? throw new InvalidDataException($"Verplicht veld {name} ontbreekt.");

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static int? Integer(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static bool Boolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.True;
}
