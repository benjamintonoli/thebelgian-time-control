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

        query = filter.Scope switch
        {
            PayrollReviewQueueScope.Open => query.Where(item => PayrollReviewCategories.IsUnresolved(item.WorkflowStatus)),
            PayrollReviewQueueScope.Closed => query.Where(item => PayrollReviewCategories.IsClosed(item.WorkflowStatus)),
            _ => query,
        };

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            query = query.Where(item => MatchesSearch(item, term));
        }

        query = filter.Sort?.ToLowerInvariant() switch
        {
            "datum" or "date" => query.OrderByDescending(item => item.Date)
                .ThenBy(item => item.DisplayName ?? item.ResourceId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.CaseKey, StringComparer.Ordinal),
            "medewerker" or "employee" => query.OrderBy(item => item.DisplayName ?? item.ResourceId, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(item => item.Date)
                .ThenBy(item => item.CaseKey, StringComparer.Ordinal),
            "ernst" or "severity" => query.OrderByDescending(item => item.Severity)
                .ThenByDescending(item => item.Date)
                .ThenBy(item => item.CaseKey, StringComparer.Ordinal),
            "categorie" or "category" => query.OrderBy(item => item.Category)
                .ThenByDescending(item => item.Severity)
                .ThenByDescending(item => item.Date)
                .ThenBy(item => item.CaseKey, StringComparer.Ordinal),
            _ => query.OrderByDescending(item => item.Severity)
                .ThenByDescending(item => item.Actionability == PayrollReviewCaseActionability.ReadyProposal)
                .ThenByDescending(item => item.Actionability == PayrollReviewCaseActionability.NeedsControl)
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
        var unresolvedByCategory = Enum.GetValues<PayrollReviewCategory>()
            .Where(item => item != PayrollReviewCategory.All)
            .ToDictionary(
                item => item,
                item => allCases.Count(c =>
                    c.Category == item && PayrollReviewCategories.IsUnresolved(c.WorkflowStatus)));

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
            byCategory,
            unresolvedByCategory,
            allCases.Count(item =>
                item.Severity == PayrollFindingSeverity.High
                && PayrollReviewCategories.IsUnresolved(item.WorkflowStatus)));
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
            PayrollReviewCategory.WrongDossier =>
                $"wrong-dossier:{finding.FindingKey}",
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

        var (planned, difference, ruleHint, friendlyState) = BuildCategoryPresentation(
            category,
            findings,
            action,
            hybridNote,
            actionability);

        var bookedHours = findings.Any(item => item.BookedHours is not null)
            ? findings.Sum(item => item.BookedHours ?? 0m)
            : (decimal?)null;
        var plannedHours = findings.Select(item => item.PlannedHours).FirstOrDefault(item => item.HasValue);
        var decisionCode = findings.Select(item => item.DecisionCode).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
        var decisionLabel = findings.Select(item => item.DecisionLabel).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
        var findingDescription = primary.Description;
        var findingEvidence = primary.Evidence;
        var timeInterval = PayrollTriageEvidence.ParseTimeInterval(findingDescription);
        var performanceDescription = PayrollTriageEvidence.ExtractEvidenceField(findingEvidence, "desc");
        var performanceMemo = PayrollTriageEvidence.ExtractEvidenceField(findingEvidence, "memo");
        var descriptionPresent = !string.IsNullOrWhiteSpace(performanceDescription);
        var planningPresent = PayrollTriageEvidence.PlanningPresent(findingEvidence, friendlyState, ruleHint);

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
            BuildBookedSummary(findings, action, timeInterval),
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
            reviewed?.ReviewComment,
            planned,
            difference,
            ruleHint,
            friendlyState,
            bookedHours,
            plannedHours,
            decisionCode,
            decisionLabel,
            timeInterval,
            findingDescription,
            findingEvidence,
            performanceDescription,
            performanceMemo,
            descriptionPresent,
            planningPresent);
    }

    private static (string? Planned, string? Difference, string? RuleHint, string? FriendlyState) BuildCategoryPresentation(
        PayrollReviewCategory category,
        List<PayrollFindingRecord> findings,
        PayrollProposedActionRecord? action,
        string? hybridNote,
        PayrollReviewCaseActionability actionability)
    {
        var primary = findings[0];
        return category switch
        {
            PayrollReviewCategory.Project300 => (
                null,
                null,
                "Geen reservatie in planning gevonden.",
                "Zonder reservatie"),
            PayrollReviewCategory.Project200 => (
                primary.PlannedHours is { } ph ? $"{ph:0.##} u gepland" : null,
                FormatDifference(primary),
                primary.FindingType == PayrollFindingType.Project200WithoutPlanning
                    ? "Geen planning gevonden."
                    : "Geboekt wijkt af van gepland.",
                primary.FindingType == PayrollFindingType.Project200WithoutPlanning
                    ? "Zonder planning"
                    : "Meer geboekt"),
            PayrollReviewCategory.Project100 => (
                primary.PlannedHours is { } ph ? $"{ph:0.##} u gepland" : null,
                primary.SuggestedOvertimeAdjustmentHours is { } ot && ot > 0
                    ? $"mogelijk +{ot:0.##} u overuren"
                    : null,
                "Toolbox / opleiding — controleer impact op overuren.",
                "Toolbox / opleiding"),
            PayrollReviewCategory.Standby => (
                null,
                null,
                hybridNote is not null
                    ? "Telefonisch gedeelte eerst bevestigen."
                    : "GPS- of dossiercontrole vereist.",
                StandbyFriendlyState(findings, hybridNote, actionability)),
            PayrollReviewCategory.MissingPerformance => (
                primary.PlannedHours is { } ph ? $"{ph:0.##} u gepland" : null,
                null,
                "Controleer of een prestatie ontbreekt.",
                MissingFriendlyState(primary)),
            _ => (null, null, null, null),
        };
    }

    private static string? FormatDifference(PayrollFindingRecord finding)
    {
        if (finding.BookedHours is { } booked && finding.PlannedHours is { } planned)
        {
            return $"{booked:0.##} u geboekt vs {planned:0.##} u gepland (Δ {(booked - planned):0.##} u)";
        }

        return null;
    }

    private static string StandbyFriendlyState(
        List<PayrollFindingRecord> findings,
        string? hybridNote,
        PayrollReviewCaseActionability actionability)
    {
        if (!string.IsNullOrWhiteSpace(hybridNote))
        {
            return "Mogelijk telefoon + fysieke interventie";
        }

        if (findings.Any(item => item.FindingType == PayrollFindingType.StandbyPhoneExceeds15Min))
        {
            return "Telefonisch >15 min";
        }

        if (findings.Any(item => item.FindingType == PayrollFindingType.StandbyPossibleWrongDossier))
        {
            return "Mogelijk verkeerd dossier";
        }

        if (findings.Any(item => item.FindingType == PayrollFindingType.StandbyNoGpsData)
            || findings.Any(item => string.Equals(item.GpsClassification, nameof(StandbyGpsClassification.NoGpsData), StringComparison.Ordinal)))
        {
            return "Onvoldoende GPS";
        }

        if (findings.Any(item => item.FindingType == PayrollFindingType.StandbyAmbiguousEvidence))
        {
            return "Onvoldoende GPS";
        }

        if (actionability == PayrollReviewCaseActionability.ReadyProposal)
        {
            return "Volledige fysieke interventie";
        }

        if (actionability == PayrollReviewCaseActionability.Blocked)
        {
            return "Onvolledige ritketen";
        }

        return "Wachtdienst controle";
    }

    private static string MissingFriendlyState(PayrollFindingRecord finding)
    {
        if (finding.FindingType == PayrollFindingType.WrongProjectBooking
            || string.Equals(
                finding.GpsClassification,
                nameof(MissingTechnicianConflictClass.PlannedJobSupportedExistingBookingWrong),
                StringComparison.Ordinal))
        {
            return "Mogelijk verkeerd dossier";
        }

        var gps = finding.GpsClassification ?? string.Empty;
        var siteMatch = PayrollIntelligenceWorkbenchBuilder.ParseToken(finding.Evidence, "siteMatch");
        var conflictClass = PayrollIntelligenceWorkbenchBuilder.ParseToken(finding.Evidence, "conflictClass");
        var travelRaw = PayrollIntelligenceWorkbenchBuilder.ParseToken(finding.Evidence, "travelMode");
        if (Enum.TryParse<MissingTechnicianTravelMode>(travelRaw, ignoreCase: true, out var travelMode))
        {
            if (travelMode == MissingTechnicianTravelMode.SharedTravelProven
                && (gps.Contains("PeerPlusGps", StringComparison.OrdinalIgnoreCase)
                    || gps.Equals(nameof(MissingTechnicianEvidenceClass.PlanningPlusPeerPlusGps), StringComparison.Ordinal)
                    || gps.Equals(nameof(MissingTechnicianEvidenceClass.PlanningPlusPeer), StringComparison.Ordinal)))
            {
                return "Sterk bewijs";
            }

            if (travelMode == MissingTechnicianTravelMode.SeparateVehicleProven
                && string.Equals(siteMatch, nameof(MissingTechnicianSiteMatch.PlannedJobSiteMatch), StringComparison.Ordinal)
                && gps.Equals(nameof(MissingTechnicianEvidenceClass.PlanningPlusPeerPlusGps), StringComparison.Ordinal))
            {
                return "Sterk bewijs";
            }

            if (travelMode == MissingTechnicianTravelMode.SeparateVehicleProven)
            {
                return string.IsNullOrWhiteSpace(conflictClass) || conflictClass == "None"
                    ? "Apart gereden"
                    : "Conflict / apart gereden";
            }

            if (travelMode == MissingTechnicianTravelMode.SharedTravelPossible
                && gps.Equals(nameof(MissingTechnicianEvidenceClass.PlanningPlusPeer), StringComparison.Ordinal))
            {
                return "Planning + collega (mogelijk samen)";
            }
        }

        if (gps.Contains("PeerPlusGps", StringComparison.OrdinalIgnoreCase)
            || gps.Equals(nameof(MissingTechnicianEvidenceClass.PlanningPlusPeerPlusGps), StringComparison.Ordinal))
        {
            return "Sterk bewijs";
        }

        if (gps.Equals(nameof(MissingTechnicianEvidenceClass.PlanningPlusPeer), StringComparison.Ordinal))
        {
            return "Planning + collega";
        }

        if (gps.Equals(nameof(MissingTechnicianEvidenceClass.NoGpsData), StringComparison.Ordinal))
        {
            return "Geen GPS";
        }

        if (gps.Contains("Contradict", StringComparison.OrdinalIgnoreCase))
        {
            return "Tegenstrijdige prestatie";
        }

        return "Ambigu";
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
            return (PayrollReviewCaseActionability.Blocked, "Correctie vereist verdere controle", hybrid,
                "Eenvoudige GPS-correctie niet beschikbaar — hybride telefoon+fysiek mogelijk");
        }

        if (action is not null && action.Status == PayrollProposedActionStatus.ReadyForApproval)
        {
            var proposal = TryReadAdjust(action.ProposalSnapshotJson);
            var summary = proposal is null
                ? "Voorstel klaar"
                : $"{proposal.CurrentStart:HH:mm}–{proposal.CurrentEnd:HH:mm} → {proposal.ProposedStart:HH:mm}–{proposal.ProposedEnd:HH:mm}";
            return (PayrollReviewCaseActionability.ReadyProposal, "Voorstel klaar voor controle", null, summary);
        }

        if (action is not null && action.Status == PayrollProposedActionStatus.Blocked)
        {
            return (PayrollReviewCaseActionability.Blocked, "Nog niet klaar om automatisch voor te stellen", null, action.BlockReason);
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

            var nl = CultureInfo.GetCultureInfo("nl-BE");
            var physicalHours = (proposal.ProposedEnd - proposal.ProposedStart).TotalHours;
            var scenarioHours = 0.25 + physicalHours;
            return string.Create(
                nl,
                $"Geboekt: {proposal.CurrentStart:HH:mm}–{proposal.CurrentEnd:HH:mm}. Fysieke interventie: {proposal.ProposedStart:HH:mm}–{proposal.ProposedEnd:HH:mm}. Mogelijk telefonisch: max 0:15. Possible payable scenario: 0:15 + {physicalHours.ToString("0.##", nl)} u = {scenarioHours.ToString("0.##", nl)} u. Telefonisch gedeelte eerst bevestigen. (Niet bewezen waarheid; split vereist.)");
        }
        catch (JsonException)
        {
            return "Scenario indien telefonisch contact bevestigd wordt: max 0:15 + fysieke callout.";
        }
    }

    private static string? BuildBookedSummary(
        List<PayrollFindingRecord> findings,
        PayrollProposedActionRecord? action,
        string? timeInterval = null)
    {
        var adjust = action is null ? null : TryReadAdjust(action.ProposalSnapshotJson);
        if (adjust is not null)
        {
            return $"{adjust.CurrentStart:HH:mm}–{adjust.CurrentEnd:HH:mm}";
        }

        if (!string.IsNullOrWhiteSpace(timeInterval))
        {
            var booked = findings.Select(item => item.BookedHours).FirstOrDefault(item => item.HasValue);
            return booked is null
                ? timeInterval
                : $"{timeInterval} · {booked:0.##} u";
        }

        var start = findings.Select(item => item.SuggestedPayableStart).FirstOrDefault(item => item.HasValue);
        var end = findings.Select(item => item.SuggestedPayableEnd).FirstOrDefault(item => item.HasValue);
        if (start is not null && end is not null)
        {
            return $"voorstel {start:HH:mm}–{end:HH:mm}";
        }

        var bookedOnly = findings.Select(item => item.BookedHours).FirstOrDefault(item => item.HasValue);
        return bookedOnly is null ? null : $"{bookedOnly:0.##} u geboekt";
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
