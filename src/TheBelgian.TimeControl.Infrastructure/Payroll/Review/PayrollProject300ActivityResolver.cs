using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Actions;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Review;

/// <summary>
/// Resolves PWS ExpectedActivityType for Project300 corrections using the same
/// verified HFDTAAK semantics and PerformanceActivityClassifier path as Daily Boundary.
/// Does not invent ID→activity rows beyond VerifiedMainTaskSemantics + classifier text.
/// </summary>
internal static class PayrollProject300ActivityResolver
{
    private static readonly HashSet<string> PwsSupported = new(StringComparer.Ordinal)
    {
        nameof(PerformanceActivityType.CustomerWork),
        nameof(PerformanceActivityType.SiteWork),
        nameof(PerformanceActivityType.OfficeWork),
        nameof(PerformanceActivityType.WaitingTime),
    };

    public static (string? ActivityType, string? FriendlyTaskName, bool Supported, string Message) Resolve(
        NormalizedPerformanceEntry performance,
        HfdTaakDefinition? hfdTaak)
    {
        var friendly = FormatFriendly(hfdTaak, performance.HfdTaakId);
        if (performance.HfdTaakId is null)
        {
            return (null, friendly, false,
                "Deze prestatie kan nog niet veilig vanuit TimeControl aangepast worden (geen HFDTAAK).");
        }

        var mainTask = performance.HfdTaakId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var description = FirstNonEmpty(performance.Description, hfdTaak?.Description, performance.Memo);
        var pilot = new NormalizedPilotPerformance(
            ExternalId: performance.SourceEntryId,
            ResourceExternalId: performance.ResourceId,
            Date: performance.Date,
            StartDateTime: performance.Start ?? new DateTimeOffset(performance.Date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            EndDateTime: performance.End ?? new DateTimeOffset(performance.Date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            PauseMinutes: 0,
            GrossMinutes: 0,
            NetMinutes: 0,
            DistanceKilometres: 0m,
            ProjectExternalId: performance.ProjectId,
            MainTaskExternalId: mainTask,
            WorkOrderNumber: performance.BonNr,
            Description: description,
            Comment: performance.Memo,
            ProjectNumber: performance.ProjectNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ProjectName: null,
            DeliveryAddressExternalId: null,
            CustomerOrSiteName: null,
            Street: null,
            PostalCode: performance.Postcode,
            City: null,
            Country: null,
            ProjectCandidateCount: 0,
            WorkOrderCandidateCount: 0,
            AddressCandidateCount: 0,
            JoinAssessment: "project300-workbench",
            Normalization: "project300-workbench");

        var classification = PerformanceActivityClassifier.Classify(pilot, performance.ResourceId, null);
        var activity = classification.ActivityType.ToString();

        if (PwsSupported.Contains(activity))
        {
            return (
                activity,
                friendly,
                true,
                $"VAN/TOT-correctie beschikbaar ({activity} via HFDTAAK {performance.HfdTaakId}).");
        }

        return (
            activity == nameof(PerformanceActivityType.Unknown) ? null : activity,
            friendly,
            false,
            $"Deze prestatie kan nog niet veilig vanuit TimeControl aangepast worden. ({friendly}; type={activity})");
    }

    private static string FormatFriendly(HfdTaakDefinition? hfdTaak, int? id)
    {
        if (hfdTaak is null)
        {
            return id is null ? "HFDTAAK onbekend" : $"HFDTAAK {id}";
        }

        var code = string.IsNullOrWhiteSpace(hfdTaak.Code) ? null : hfdTaak.Code.Trim();
        var desc = string.IsNullOrWhiteSpace(hfdTaak.Description) ? null : hfdTaak.Description.Trim();
        if (code is null && desc is null)
        {
            return $"HFDTAAK {hfdTaak.Id}";
        }

        if (code is null)
        {
            return $"HFDTAAK {hfdTaak.Id}: {desc}";
        }

        if (desc is null)
        {
            return $"HFDTAAK {hfdTaak.Id} ({code})";
        }

        return $"HFDTAAK {hfdTaak.Id} ({code}: {desc})";
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value) && value.Trim() is not ("—" or "-"))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
