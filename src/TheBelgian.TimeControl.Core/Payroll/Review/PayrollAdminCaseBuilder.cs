using System.Globalization;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Findings;

namespace TheBelgian.TimeControl.Core.Payroll.Review;

public static class PayrollAdminCaseBuilder
{
    public static IReadOnlyList<PayrollAdminCase> Build(
        IReadOnlyList<PayrollReviewCase> reviewCases,
        IReadOnlyDictionary<string, string?>? decisionCodesByFindingKey = null,
        IReadOnlyDictionary<string, string?>? decisionLabelsByFindingKey = null)
    {
        return reviewCases
            .GroupBy(AdminGroupKey, StringComparer.Ordinal)
            .Select(group => ToAdminCase(
                group.Key,
                group.OrderByDescending(item => item.Severity)
                    .ThenByDescending(item => item.Date)
                    .ThenBy(item => item.CaseKey, StringComparer.Ordinal)
                    .ToList(),
                decisionCodesByFindingKey,
                decisionLabelsByFindingKey))
            .OrderByDescending(item => item.Severity)
            .ThenByDescending(item => PayrollReviewCategories.IsUnresolved(item.WorkflowStatus))
            .ThenByDescending(item => item.Date)
            .ThenBy(item => item.DisplayName ?? item.ResourceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.AdminCaseKey, StringComparer.Ordinal)
            .ToList();
    }

    public static IReadOnlyList<PayrollAdminCase> ApplyFilter(
        IReadOnlyList<PayrollAdminCase> cases,
        PayrollReviewQueueFilter filter)
    {
        IEnumerable<PayrollAdminCase> query = cases;
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
            "datum" or "date" => query
                .OrderByDescending(item => item.Date)
                .ThenBy(item => item.DisplayName ?? item.ResourceId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.AdminCaseKey, StringComparer.Ordinal),
            "medewerker" or "employee" => query
                .OrderBy(item => item.DisplayName ?? item.ResourceId, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(item => item.Date)
                .ThenBy(item => item.AdminCaseKey, StringComparer.Ordinal),
            "ernst" or "severity" => query
                .OrderByDescending(item => item.Severity)
                .ThenByDescending(item => item.Date)
                .ThenBy(item => item.AdminCaseKey, StringComparer.Ordinal),
            "categorie" or "category" => query
                .OrderBy(item => item.Category)
                .ThenByDescending(item => item.Severity)
                .ThenBy(item => item.AdminCaseKey, StringComparer.Ordinal),
            _ => query
                .OrderByDescending(item => item.Severity)
                .ThenByDescending(item => PayrollReviewCategories.IsUnresolved(item.WorkflowStatus))
                .ThenByDescending(item => item.Date)
                .ThenBy(item => item.DisplayName ?? item.ResourceId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.AdminCaseKey, StringComparer.Ordinal),
        };

        return query.ToList();
    }

    public static PayrollAdminQueueSummary Summarize(
        IReadOnlyList<PayrollReviewCase> reviewCases,
        IReadOnlyList<PayrollAdminCase> adminCases)
    {
        var categories = Enum.GetValues<PayrollReviewCategory>()
            .Where(item => item != PayrollReviewCategory.All)
            .ToArray();

        var openHoursByCategory = categories.ToDictionary(
            item => item,
            item => adminCases
                .Where(c =>
                    c.Category == item
                    && c.WorkflowStatus == PayrollFindingStatus.Open)
                .Sum(c => c.TotalBookedHours ?? 0m));

        return new PayrollAdminQueueSummary(
            reviewCases.Count,
            adminCases.Count,
            adminCases.Count(item => item.WorkflowStatus == PayrollFindingStatus.Open),
            adminCases.Count(item => item.WorkflowStatus == PayrollFindingStatus.NeedsFollowUp),
            adminCases.Count(item => item.WorkflowStatus is PayrollFindingStatus.Reviewed or PayrollFindingStatus.Resolved),
            adminCases.Count(item => item.WorkflowStatus == PayrollFindingStatus.Dismissed),
            adminCases.Count(item =>
                item.Severity == PayrollFindingSeverity.High
                && PayrollReviewCategories.IsUnresolved(item.WorkflowStatus)),
            categories.ToDictionary(item => item, item => reviewCases.Count(c => c.Category == item)),
            categories.ToDictionary(item => item, item => adminCases.Count(c => c.Category == item)),
            categories.ToDictionary(
                item => item,
                item => adminCases.Count(c =>
                    c.Category == item && c.WorkflowStatus == PayrollFindingStatus.Open)),
            categories.ToDictionary(
                item => item,
                item => adminCases.Count(c =>
                    c.Category == item && c.WorkflowStatus == PayrollFindingStatus.NeedsFollowUp)),
            categories.ToDictionary(
                item => item,
                item => adminCases.Count(c =>
                    c.Category == item
                    && c.WorkflowStatus is PayrollFindingStatus.Reviewed
                        or PayrollFindingStatus.Resolved
                        or PayrollFindingStatus.Dismissed)),
            openHoursByCategory.Values.Sum(),
            openHoursByCategory);
    }

    public static string AdminGroupKey(PayrollReviewCase reviewCase)
    {
        return reviewCase.Category switch
        {
            PayrollReviewCategory.Project300 or PayrollReviewCategory.Project200 or PayrollReviewCategory.Project100 =>
                $"admin:{reviewCase.Category}:{reviewCase.ResourceId}:{reviewCase.Date:yyyyMMdd}:{BusinessContext(reviewCase)}",
            _ => $"admin:{reviewCase.CaseKey}",
        };
    }

    public static bool AllowsBulkDisposition(PayrollAdminCase adminCase)
    {
        if (adminCase.Category is PayrollReviewCategory.Standby or PayrollReviewCategory.Overlap)
        {
            return false;
        }

        if (adminCase.Category == PayrollReviewCategory.MissingPerformance
            && string.Equals(adminCase.FriendlyState, "Sterk bewijs", StringComparison.Ordinal))
        {
            return false;
        }

        return adminCase.Category is PayrollReviewCategory.Project300
            or PayrollReviewCategory.Project200
            or PayrollReviewCategory.Project100
            or PayrollReviewCategory.Other
            or PayrollReviewCategory.MissingPerformance;
    }

    private static string BusinessContext(PayrollReviewCase reviewCase)
    {
        if (!string.IsNullOrWhiteSpace(reviewCase.ProjectId))
        {
            return "p:" + reviewCase.ProjectId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(reviewCase.BonNr))
        {
            return "b:" + reviewCase.BonNr.Trim();
        }

        // Same-day internal work without project/BON shares one admin decision.
        return "internal";
    }

    private static PayrollAdminCase ToAdminCase(
        string adminCaseKey,
        List<PayrollReviewCase> cases,
        IReadOnlyDictionary<string, string?>? decisionCodesByFindingKey,
        IReadOnlyDictionary<string, string?>? decisionLabelsByFindingKey)
    {
        var primary = cases[0];
        var hybrid = cases.Select(item => item.HybridScenarioNote).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
        var findingIds = cases.SelectMany(item => item.FindingIds).Distinct().OrderBy(id => id).ToArray();
        var findingKeys = cases.SelectMany(item => item.FindingKeys).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var perfCount = cases
            .Select(item => item.PrimaryPerformanceId)
            .Where(id => id is > 0)
            .Distinct()
            .Count();
        if (perfCount == 0)
        {
            perfCount = Math.Max(1, cases.Count);
        }

        var bookedHours = SumBookedHours(cases);
        var workflow = ResolveWorkflow(cases);
        var decisionCode = FindDecision(findingKeys, decisionCodesByFindingKey);
        var decisionLabel = FindDecision(findingKeys, decisionLabelsByFindingKey);
        var reviewed = cases
            .Where(item => item.ReviewedAtUtc is not null)
            .OrderByDescending(item => item.ReviewedAtUtc)
            .FirstOrDefault();

        var issue = BuildIssueSummary(primary.Category, cases, bookedHours, perfCount);
        var keyFact = BuildKeyFact(primary, bookedHours, perfCount, hybrid);
        var performances = cases.Select(ToPerformanceDetail).ToArray();
        var timeSummary = BuildTimeIntervalSummary(performances);
        var descriptionSummary = performances
            .Select(item => item.PerformanceDescription)
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item))
            ?? performances.Select(item => item.PerformanceMemo).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
        var chips = PayrollTriageEvidence.BuildChips(
            performances.Any(item => item.DescriptionPresent),
            performances.Any(item => item.PlanningPresent),
            cases.Select(item => item.EvidenceSummary).FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(item)
                && !item.Contains(' ', StringComparison.Ordinal)
                && item.Length < 40),
            timeSummary,
            bookedHours);

        var admin = new PayrollAdminCase(
            adminCaseKey,
            primary.Category,
            primary.ResourceId,
            primary.DisplayName,
            primary.Date,
            cases.Max(item => item.Severity),
            workflow,
            PayrollGuidedDecisions.BusinessQuestion(primary.Category, hybrid is not null),
            issue,
            keyFact,
            cases.Select(item => item.BonNr).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)),
            cases.Select(item => item.ProjectId).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)),
            AggregateBookedSummary(cases, bookedHours),
            cases.Select(item => item.PlannedSummary).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)),
            cases.Select(item => item.DifferenceSummary).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)),
            cases.Select(item => item.EvidenceSummary).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)),
            hybrid,
            cases.Select(item => item.FriendlyState).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)),
            cases.Select(item => item.RuleHint).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)),
            PayrollGuidedDecisions.ActionabilityHint(
                cases.Select(item => item.Actionability).DefaultIfEmpty(PayrollReviewCaseActionability.None).Max(),
                hybrid),
            cases.Count,
            perfCount,
            bookedHours,
            cases,
            findingIds,
            findingKeys,
            decisionCode,
            decisionLabel,
            reviewed?.ReviewComment,
            reviewed?.ReviewedAtUtc,
            reviewed?.ReviewedBy,
            AllowsBulkDisposition: false, // set below
            PayrollGuidedDecisions.ChoicesFor(primary.Category),
            performances,
            timeSummary,
            descriptionSummary,
            chips);

        return admin with { AllowsBulkDisposition = AllowsBulkDisposition(admin) };
    }

    private static PayrollAdminPerformanceDetail ToPerformanceDetail(PayrollReviewCase reviewCase)
    {
        var chips = PayrollTriageEvidence.BuildChips(
            reviewCase.DescriptionPresent,
            reviewCase.PlanningPresent,
            reviewCase.EvidenceSummary,
            reviewCase.TimeInterval,
            reviewCase.BookedHours);
        return new PayrollAdminPerformanceDetail(
            reviewCase.CaseKey,
            reviewCase.PrimaryPerformanceId,
            reviewCase.TimeInterval,
            reviewCase.BookedHours,
            reviewCase.BonNr,
            reviewCase.ProjectId,
            reviewCase.PerformanceDescription,
            reviewCase.PerformanceMemo,
            reviewCase.FindingDescription ?? reviewCase.ProblemLabel,
            reviewCase.FriendlyState ?? reviewCase.RuleHint,
            reviewCase.DescriptionPresent,
            reviewCase.PlanningPresent,
            reviewCase.FindingEvidence,
            chips);
    }

    private static string? BuildTimeIntervalSummary(IReadOnlyList<PayrollAdminPerformanceDetail> performances)
    {
        var intervals = performances
            .Select(item => item.TimeInterval)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (intervals.Length == 0)
        {
            return null;
        }

        if (intervals.Length == 1)
        {
            return intervals[0];
        }

        return string.Join(" · ", intervals.Take(3));
    }

    private static string BuildIssueSummary(
        PayrollReviewCategory category,
        List<PayrollReviewCase> cases,
        decimal? bookedHours,
        int perfCount)
    {
        var nl = CultureInfo.GetCultureInfo("nl-BE");
        return category switch
        {
            PayrollReviewCategory.Project300 when perfCount > 1 =>
                $"{perfCount} prestaties · totaal {(bookedHours ?? 0m).ToString("0.##", nl)} u zonder reservatie",
            PayrollReviewCategory.Project300 =>
                cases[0].ProblemLabel,
            PayrollReviewCategory.Project200 when perfCount > 1 =>
                $"{perfCount} prestaties · totaal {(bookedHours ?? 0m).ToString("0.##", nl)} u",
            PayrollReviewCategory.Project200 =>
                cases[0].ProblemLabel,
            _ => cases[0].ProblemLabel,
        };
    }

    private static string? BuildKeyFact(
        PayrollReviewCase primary,
        decimal? bookedHours,
        int perfCount,
        string? hybrid)
    {
        if (!string.IsNullOrWhiteSpace(hybrid))
        {
            return "Mogelijk telefoon + fysieke interventie";
        }

        if (!string.IsNullOrWhiteSpace(primary.DifferenceSummary))
        {
            return primary.DifferenceSummary;
        }

        if (perfCount > 1 && bookedHours is { } hours)
        {
            return $"{perfCount} prestaties · {hours.ToString("0.##", CultureInfo.GetCultureInfo("nl-BE"))} u";
        }

        return primary.BookedSummary ?? primary.FriendlyState ?? primary.RuleHint;
    }

    private static string? AggregateBookedSummary(List<PayrollReviewCase> cases, decimal? bookedHours)
    {
        if (cases.Count == 1)
        {
            return cases[0].BookedSummary;
        }

        if (bookedHours is { } hours)
        {
            return $"{hours.ToString("0.##", CultureInfo.GetCultureInfo("nl-BE"))} u totaal";
        }

        return string.Join(" · ", cases.Select(item => item.BookedSummary).Where(item => !string.IsNullOrWhiteSpace(item)).Take(3));
    }

    private static decimal? SumBookedHours(List<PayrollReviewCase> cases)
    {
        if (!cases.Any(item => item.BookedHours is not null))
        {
            return null;
        }

        return cases.Sum(item => item.BookedHours ?? 0m);
    }

    private static PayrollFindingStatus ResolveWorkflow(List<PayrollReviewCase> cases)
    {
        if (cases.Any(item => item.WorkflowStatus == PayrollFindingStatus.Open))
        {
            return PayrollFindingStatus.Open;
        }

        if (cases.Any(item => item.WorkflowStatus == PayrollFindingStatus.NeedsFollowUp))
        {
            return PayrollFindingStatus.NeedsFollowUp;
        }

        if (cases.Any(item => item.WorkflowStatus == PayrollFindingStatus.Resolved))
        {
            return PayrollFindingStatus.Resolved;
        }

        if (cases.Any(item => item.WorkflowStatus == PayrollFindingStatus.Reviewed))
        {
            return PayrollFindingStatus.Reviewed;
        }

        if (cases.Any(item => item.WorkflowStatus == PayrollFindingStatus.Dismissed))
        {
            return PayrollFindingStatus.Dismissed;
        }

        return PayrollFindingStatus.Open;
    }

    private static string? FindDecision(
        IReadOnlyList<string> findingKeys,
        IReadOnlyDictionary<string, string?>? map)
    {
        if (map is null || map.Count == 0)
        {
            return null;
        }

        foreach (var key in findingKeys)
        {
            if (map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool MatchesSearch(PayrollAdminCase item, string term) =>
        (item.DisplayName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
        || item.ResourceId.Contains(term, StringComparison.OrdinalIgnoreCase)
        || (item.BonNr?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
        || (item.ProjectId?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
        || item.IssueSummary.Contains(term, StringComparison.OrdinalIgnoreCase)
        || item.BusinessQuestion.Contains(term, StringComparison.OrdinalIgnoreCase)
        || item.FindingKeys.Any(key => key.Contains(term, StringComparison.OrdinalIgnoreCase))
        || item.UnderlyingCases.Any(c =>
            (c.BonNr?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
            || (c.ProjectId?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
            || (c.PerformanceDescription?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
            || (c.FindingDescription?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
            || c.FindingKeys.Any(key => key.Contains(term, StringComparison.OrdinalIgnoreCase)))
        || item.Date.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("nl-BE")).Contains(term, StringComparison.OrdinalIgnoreCase)
        || item.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture).Contains(term, StringComparison.OrdinalIgnoreCase);
}
