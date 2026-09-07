using System.Globalization;
using System.Text.Json;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Actions;
using TheBelgian.TimeControl.Core.Payroll.Findings;

namespace TheBelgian.TimeControl.Core.Payroll.Review;

public static class PayrollReviewCaseBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<PayrollReviewCase> Build(
        IReadOnlyList<PayrollFindingRecord> findings,
        IReadOnlyDictionary<string, PayrollShadowEmployeeResult> employeesByResource,
        IReadOnlyList<PayrollProposedActionRecord> actions)
    {
        var actionsByFindingKey = actions
            .GroupBy(item => item.FindingKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.Id).First(), StringComparer.Ordinal);

        var groups = findings
            .GroupBy(GroupKey)
            .Select(group => ToCase(group.Key, group.ToList(), employeesByResource, actionsByFindingKey))
            .OrderByDescending(item => item.Severity)
            .ThenByDescending(item => item.Actionability == PayrollReviewCaseActionability.ReadyProposal)
            .ThenByDescending(item => item.Actionability == PayrollReviewCaseActionability.NeedsControl)
            .ThenByDescending(item => item.Date)
            .ThenBy(item => item.DisplayName ?? item.ResourceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.CaseKey, StringComparer.Ordinal)
            .ToList();

        return groups;
    }

    public static IReadOnlyList<PayrollReviewCase> ApplyFilter(
        IReadOnlyList<PayrollReviewCase> cases,
        PayrollReviewQueueFilter filter)
    {
        IEnumerable<PayrollReviewCase> query = cases;
        if (filter.Category != PayrollReviewCategory.All)
        {
            query = query.Where(item => item.Category == filter.Category);
        }

        if (filter.WorkflowStatus is { } status)
        {
            query = query.Where(item => item.WorkflowStatus == status);
        }

        if (filter.Severity is { } severity)
        {
            query = query.Where(item => item.Severity == severity);
        }

        if (filter.Actionability is { } actionability)
        {
            query = query.Where(item => item.Actionability == actionability);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            query = query.Where(item => MatchesSearch(item, term));
        }

        query = filter.Sort?.ToLowerInvariant() switch
        {
            "datum" or "date" => query.OrderByDescending(item => item.Date)
                .ThenBy(item => item.DisplayName ?? item.ResourceId, StringComparer.OrdinalIgnoreCase),
            "medewerker" or "employee" => query.OrderBy(item => item.DisplayName ?? item.ResourceId, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(item => item.Date),
            "ernst" or "severity" => query.OrderByDescending(item => item.Severity)
                .ThenByDescending(item => item.Date),
            "categorie" or "category" => query.OrderBy(item => item.Category)
                .ThenByDescending(item => item.Severity)
                .ThenByDescending(item => item.Date),
            _ => query.OrderByDescending(item => item.Severity)
                .ThenByDescending(item => item.Actionability == PayrollReviewCaseActionability.ReadyProposal)
                .ThenByDescending(item => item.Date)
                .ThenBy(item => item.DisplayName ?? item.ResourceId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.CaseKey, StringComparer.Ordinal),
        };

        return query.ToList();
    }

    public static PayrollReviewQueueSummary Summarize(
        IReadOnlyList<PayrollReviewCase> allCases,
        IReadOnlyList<PayrollShadowEmployeeResult> includedEmployees,
        IReadOnlyList<PayrollProposedActionRecord> actions)
    {
        var withFindings = includedEmployees
            .Where(item => allCases.Any(c =>
                string.Equals(c.ResourceId, item.ResourceId, StringComparison.Ordinal)
                && c.WorkflowStatus is PayrollFindingStatus.Open or PayrollFindingStatus.NeedsFollowUp))
            .Select(item => item.ResourceId)
            .ToHashSet(StringComparer.Ordinal);

        // Employees with any finding (open or not) for the "with findings" count
        var anyFindingResources = allCases.Select(item => item.ResourceId).ToHashSet(StringComparer.Ordinal);
        var withAny = includedEmployees.Count(item => anyFindingResources.Contains(item.ResourceId));
        var without = includedEmployees.Count - withAny;

        var byCategory = Enum.GetValues<PayrollReviewCategory>()
            .Where(item => item != PayrollReviewCategory.All)
            .ToDictionary(item => item, item => allCases.Count(c => c.Category == item));

        return new PayrollReviewQueueSummary(
            includedEmployees.Count,
            withAny,
            without,
            allCases.Count,
            allCases.Count(item => item.WorkflowStatus == PayrollFindingStatus.Open),
            allCases.Count(item => item.WorkflowStatus == PayrollFindingStatus.NeedsFollowUp),
            allCases.Count(item => item.WorkflowStatus == PayrollFindingStatus.Reviewed),
            allCases.Count(item => item.WorkflowStatus == PayrollFindingStatus.Resolved),
            allCases.Count(item => item.WorkflowStatus == PayrollFindingStatus.Dismissed),
            actions.Count(item =>
                item.Status == PayrollProposedActionStatus.ReadyForApproval
                && item.ActionType == PayrollProposedActionType.AdjustExistingPerformanceTime),
            actions.Count(item =>
                item.Status == PayrollProposedActionStatus.Blocked
                && item.ActionType == PayrollProposedActionType.AdjustExistingPerformanceTime),
            byCategory);
    }

    public static string GroupKey(PayrollFindingRecord finding)
    {
        var category = PayrollReviewCategories.Map(finding.FindingType);
        var related = PayrollActionEligibility.ParseRelatedIds(finding.RelatedPerformanceIdsJson);
        return category switch
        {
            PayrollReviewCategory.Standby when related.Count >= 1 =>
                $"standby:{finding.ResourceId}:{finding.Date:yyyyMMdd}:{related[0]}",
            PayrollReviewCategory.Overlap =>
                $"overlap:{finding.FindingKey}",
            PayrollReviewCategory.MissingPerformance =>
                $"missing:{finding.FindingKey}",
            PayrollReviewCategory.Project100 or PayrollReviewCategory.Project200 or PayrollReviewCategory.Project300
                when related.Count >= 1 =>
                $"{category}:{finding.ResourceId}:{finding.Date:yyyyMMdd}:{related[0]}",
            PayrollReviewCategory.Project100 or PayrollReviewCategory.Project200 or PayrollReviewCategory.Project300 =>
                $"{category}:{finding.ResourceId}:{finding.Date:yyyyMMdd}:{finding.FindingKey}",
            _ => $"other:{finding.FindingKey}",
        };
    }

    private static PayrollReviewCase ToCase(
        string caseKey,
        List<PayrollFindingRecord> findings,
        IReadOnlyDictionary<string, PayrollShadowEmployeeResult> employeesByResource,
        Dictionary<string, PayrollProposedActionRecord> actionsByFindingKey)
    {
        findings = findings
            .OrderBy(item => item.FindingType)
            .ThenBy(item => item.FindingKey, StringComparer.Ordinal)
            .ToList();
        var primary = findings[0];
        employeesByResource.TryGetValue(primary.ResourceId, out var employee);
        var category = PayrollReviewCategories.Map(primary.FindingType);
        var related = findings
            .SelectMany(item => PayrollActionEligibility.ParseRelatedIds(item.RelatedPerformanceIdsJson))
            .Distinct()
            .ToList();
        long? perfId = related.Count == 1 ? related[0] : related.FirstOrDefault();

        PayrollProposedActionRecord? action = null;
        if (category == PayrollReviewCategory.Standby && perfId is > 0)
        {
            var aggregateKey = PayrollStandbyActivityTypes.AdjustActionKey(
                primary.ResourceId,
                primary.Date,
                perfId.Value);
            actionsByFindingKey.TryGetValue(aggregateKey, out action);
        }

        if (action is null)
        {
            foreach (var finding in findings)
            {
                if (actionsByFindingKey.TryGetValue(finding.FindingKey, out action))
                {
                    break;
                }
            }
        }

        var (actionability, actionLabel, hybridNote, proposalSummary) = ResolveActionability(findings, action);
        var workflow = ResolveWorkflowStatus(findings);
        var reviewed = findings
            .Where(item => item.ReviewedAtUtc is not null)
            .OrderByDescending(item => item.ReviewedAtUtc)
            .FirstOrDefault();

        var problemLabel = !string.IsNullOrWhiteSpace(hybridNote)
            ? "Hybride telefoon + fysieke interventie"
            : PayrollReviewCategories.ProblemLabel(findings);

        return new PayrollReviewCase(
            caseKey,
            category,
            primary.ResourceId,
            employee?.DisplayNameSnapshot,
            primary.Date,
            findings.Max(item => item.Severity),
            workflow,
            problemLabel,
            findings.Select(item => item.SuggestedBonNr).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)),
            findings.Select(item => item.SuggestedProjectId).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)),
            BuildBookedSummary(findings, action),
            BuildEvidenceSummary(findings, action, hybridNote),
            proposalSummary,
            actionability,
            actionLabel,
            findings.Select(item => item.Id).Where(id => id > 0).Distinct().OrderBy(id => id).ToArray(),
            findings.Select(item => item.FindingKey).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            findings.Select(item => item.FindingType).Distinct().OrderBy(item => item).ToArray(),
            perfId is > 0 ? perfId : null,
            action?.ActionId,
            hybridNote,
            reviewed?.ReviewedAtUtc,
            reviewed?.ReviewedBy,
            reviewed?.ReviewComment);
    }

    private static (PayrollReviewCaseActionability, string, string?, string?) ResolveActionability(
        List<PayrollFindingRecord> findings,
        PayrollProposedActionRecord? action)
    {
        string? hybrid = null;
        if (action is not null
            && action.Status == PayrollProposedActionStatus.Blocked
            && !string.IsNullOrWhiteSpace(action.BlockReason)
            && action.BlockReason.Contains("telefonische", StringComparison.OrdinalIgnoreCase))
        {
            hybrid = BuildHybridScenarioNote(action);
            return (PayrollReviewCaseActionability.Blocked, "Geblokkeerd", hybrid,
                "Eenvoudige GPS-correctie geblokkeerd — hybride telefoon+fysiek mogelijk");
        }

        if (action is not null && action.Status == PayrollProposedActionStatus.ReadyForApproval)
        {
            var proposal = TryReadAdjust(action.ProposalSnapshotJson);
            var summary = proposal is null
                ? "Voorstel klaar"
                : $"{proposal.CurrentStart:HH:mm}–{proposal.CurrentEnd:HH:mm} → {proposal.ProposedStart:HH:mm}–{proposal.ProposedEnd:HH:mm}";
            return (PayrollReviewCaseActionability.ReadyProposal, "Voorstel klaar", null, summary);
        }

        if (action is not null && action.Status == PayrollProposedActionStatus.Blocked)
        {
            return (PayrollReviewCaseActionability.Blocked, "Geblokkeerd", null, action.BlockReason);
        }

        if (findings.Any(item =>
                string.Equals(item.GpsClassification, nameof(StandbyGpsClassification.PossiblePhoneThenPhysical), StringComparison.Ordinal)))
        {
            hybrid = "Scenario indien telefonisch contact bevestigd wordt: max 0:15 telefoon + fysieke interventie (split vereist).";
            return (PayrollReviewCaseActionability.NeedsControl, "Controle nodig", hybrid, "Geen automatisch voorstel");
        }

        return (PayrollReviewCaseActionability.NeedsControl, "Controle nodig", null, "Geen voorstel");
    }

    private static string? BuildHybridScenarioNote(PayrollProposedActionRecord action)
    {
        try
        {
            var evidence = JsonSerializer.Deserialize<PayrollActionEvidenceSnapshot>(action.EvidenceSnapshotJson, JsonOptions);
            var callout = evidence?.CalloutEvidence ?? evidence?.Evidence ?? string.Empty;
            var proposal = TryReadAdjust(action.ProposalSnapshotJson);
            if (proposal is null)
            {
                return "Scenario indien telefonisch contact bevestigd wordt: max 0:15 + fysieke callout (niet-aaneengesloten).";
            }

            var physicalHours = (proposal.ProposedEnd - proposal.ProposedStart).TotalHours;
            return string.Create(
                CultureInfo.GetCultureInfo("nl-BE"),
                $"Geboekt {proposal.CurrentStart:HH:mm}–{proposal.CurrentEnd:HH:mm}. "
                + $"Fysiek GPS {proposal.ProposedStart:HH:mm}–{proposal.ProposedEnd:HH:mm}. "
                + $"Mogelijke telefoon vanaf {proposal.CurrentStart:HH:mm} (max 0:15). "
                + $"Scenario indien telefonisch contact bevestigd wordt: 0:15 + {physicalHours:0.##} u fysiek "
                + $"(totaal illustratief; gap niet betaalbaar via één VAN/TOT).");
        }
        catch (JsonException)
        {
            return "Scenario indien telefonisch contact bevestigd wordt: max 0:15 + fysieke callout.";
        }
    }

    private static string? BuildBookedSummary(
        List<PayrollFindingRecord> findings,
        PayrollProposedActionRecord? action)
    {
        var adjust = action is null ? null : TryReadAdjust(action.ProposalSnapshotJson);
        if (adjust is not null)
        {
            return $"{adjust.CurrentStart:HH:mm}–{adjust.CurrentEnd:HH:mm}";
        }

        var start = findings.Select(item => item.SuggestedPayableStart).FirstOrDefault(item => item.HasValue);
        var end = findings.Select(item => item.SuggestedPayableEnd).FirstOrDefault(item => item.HasValue);
        if (start is not null && end is not null)
        {
            return $"voorstel {start:HH:mm}–{end:HH:mm}";
        }

        var booked = findings.Select(item => item.BookedHours).FirstOrDefault(item => item.HasValue);
        return booked is null ? null : $"{booked:0.##} u geboekt";
    }

    private static string? BuildEvidenceSummary(
        List<PayrollFindingRecord> findings,
        PayrollProposedActionRecord? action,
        string? hybridNote)
    {
        if (!string.IsNullOrWhiteSpace(hybridNote))
        {
            return hybridNote;
        }

        var adjust = action is null ? null : TryReadAdjust(action.ProposalSnapshotJson);
        if (adjust is not null
            && action!.Status is PayrollProposedActionStatus.ReadyForApproval or PayrollProposedActionStatus.Blocked)
        {
            return $"GPS {adjust.ProposedStart:HH:mm}–{adjust.ProposedEnd:HH:mm}";
        }

        var gps = findings.Select(item => item.GpsClassification).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
        return gps;
    }

    private static PayrollFindingStatus ResolveWorkflowStatus(List<PayrollFindingRecord> findings)
    {
        // Most "open" wins for workflow: Open > NeedsFollowUp > others (prefer actionable)
        if (findings.Any(item => item.Status == PayrollFindingStatus.Open))
        {
            return PayrollFindingStatus.Open;
        }

        if (findings.Any(item => item.Status == PayrollFindingStatus.NeedsFollowUp))
        {
            return PayrollFindingStatus.NeedsFollowUp;
        }

        if (findings.Any(item => item.Status == PayrollFindingStatus.Resolved))
        {
            return PayrollFindingStatus.Resolved;
        }

        if (findings.Any(item => item.Status == PayrollFindingStatus.Reviewed))
        {
            return PayrollFindingStatus.Reviewed;
        }

        if (findings.Any(item => item.Status == PayrollFindingStatus.Dismissed))
        {
            return PayrollFindingStatus.Dismissed;
        }

        return PayrollFindingStatus.Open;
    }

    private static bool MatchesSearch(PayrollReviewCase item, string term) =>
        (item.DisplayName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
        || item.ResourceId.Contains(term, StringComparison.OrdinalIgnoreCase)
        || (item.BonNr?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
        || (item.ProjectId?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
        || item.ProblemLabel.Contains(term, StringComparison.OrdinalIgnoreCase)
        || item.Date.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("nl-BE")).Contains(term, StringComparison.OrdinalIgnoreCase)
        || item.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture).Contains(term, StringComparison.OrdinalIgnoreCase)
        || item.FindingKeys.Any(key => key.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static PayrollActionAdjustProposal? TryReadAdjust(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PayrollActionAdjustProposal>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
