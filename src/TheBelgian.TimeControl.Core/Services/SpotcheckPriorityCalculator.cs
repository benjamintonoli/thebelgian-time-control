using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Core.Services;

/// <summary>
/// Spotcheck priority, categories, and worklist filtering. Pure rules; no matching changes.
/// </summary>
public static class SpotcheckPriorityCalculator
{
    public const int DefaultPageSize = 25;

    public static SpotcheckPriorityTier? FromDeviationMinutes(int? maxAbsDeviationMinutes)
    {
        if (maxAbsDeviationMinutes is null)
        {
            return null;
        }

        var abs = Math.Abs(maxAbsDeviationMinutes.Value);
        if (abs >= 30)
        {
            return SpotcheckPriorityTier.HighPriority;
        }

        if (abs >= 15)
        {
            return SpotcheckPriorityTier.IndividualException;
        }

        if (abs >= 5)
        {
            return SpotcheckPriorityTier.SmallDeviation;
        }

        return SpotcheckPriorityTier.Informational;
    }

    public static int? MaxDeviationMinutes(int? startDeviationMinutes, int? endDeviationMinutes)
    {
        if (startDeviationMinutes is null && endDeviationMinutes is null)
        {
            return null;
        }

        var start = startDeviationMinutes is null ? 0 : Math.Abs(startDeviationMinutes.Value);
        var end = endDeviationMinutes is null ? 0 : Math.Abs(endDeviationMinutes.Value);
        return Math.Max(start, end);
    }

    public static (int? Start, int? End, int? Max) DeviationsForVisit(
        ReviewVisitCandidate? visit,
        bool hasReliableVisitAnchor)
    {
        if (!hasReliableVisitAnchor || visit is null)
        {
            return (null, null, null);
        }

        var max = MaxDeviationMinutes(visit.StartDeviationMinutes, visit.EndDeviationMinutes);
        return (visit.StartDeviationMinutes, visit.EndDeviationMinutes, max);
    }

    public static ReviewVisitCandidate? ResolveEffectiveVisit(ReviewCase item)
    {
        if (!string.IsNullOrWhiteSpace(item.Admin.ChosenVisitCandidateId))
        {
            return item.Matcher.CandidateVisits.FirstOrDefault(visit =>
                string.Equals(
                    visit.VisitCandidateId,
                    item.Admin.ChosenVisitCandidateId,
                    StringComparison.Ordinal));
        }

        return item.Matcher.ProposedVisit;
    }

    public static bool HasReliableVisitAnchor(ReviewCase item) =>
        item.Matcher.ProposedVisit is not null ||
        !string.IsNullOrWhiteSpace(item.Admin.ChosenVisitCandidateId);

    public static ReviewWorkCategory Classify(ReviewCase item)
    {
        if (item.ReviewStatus is AdminReviewStatus.Confirmed
            or AdminReviewStatus.Rejected
            or AdminReviewStatus.NoReliableMatch)
        {
            return ReviewWorkCategory.Completed;
        }

        if (string.Equals(item.MatcherStatus, "Ambiguous", StringComparison.OrdinalIgnoreCase))
        {
            return ReviewWorkCategory.MatchUncertainty;
        }

        if (item.Matcher.ProposedVisit is null)
        {
            return ReviewWorkCategory.DataQuality;
        }

        var max = item.MaxDeviationMinutes;
        if (max is null)
        {
            return ReviewWorkCategory.DataQuality;
        }

        if (max >= 15)
        {
            return ReviewWorkCategory.ActionableDeviation;
        }

        if (max >= 5)
        {
            return ReviewWorkCategory.SmallDeviation;
        }

        return ReviewWorkCategory.Informational;
    }

    public static ReviewCase WithDerivedFields(ReviewCase item, bool recurringPattern)
    {
        var effective = ResolveEffectiveVisit(item);
        var hasAnchor = HasReliableVisitAnchor(item);
        var (start, end, max) = DeviationsForVisit(effective, hasAnchor);
        var priority = FromDeviationMinutes(max);
        var matcher = item.Matcher with
        {
            StartDeviationMinutes = start,
            EndDeviationMinutes = end,
            MaxDeviationMinutes = max,
        };
        var updated = item with
        {
            Matcher = matcher,
            Priority = priority,
            HasRecurringConfirmedPattern = recurringPattern &&
                item.ReviewStatus == AdminReviewStatus.Confirmed,
        };
        return updated with { Category = Classify(updated) };
    }

    public static AdminReviewCategoryCounts CountCategories(IReadOnlyList<ReviewCase> cases)
    {
        var open = cases.Where(IsOpenForWork).ToArray();
        var exceptions = open.Count(item => item.Category == ReviewWorkCategory.ActionableDeviation);
        var small = open.Count(item => item.Category == ReviewWorkCategory.SmallDeviation);
        return new AdminReviewCategoryCounts(
            OpenOutstanding: exceptions + small,
            Exceptions: exceptions,
            SmallDeviation: small,
            MatchUncertainty: open.Count(item => item.Category == ReviewWorkCategory.MatchUncertainty),
            DataQuality: open.Count(item => item.Category == ReviewWorkCategory.DataQuality),
            Completed: cases.Count(item => item.Category == ReviewWorkCategory.Completed),
            Informational: open.Count(item => item.Category == ReviewWorkCategory.Informational));
    }

    public static bool IsOpenForWork(ReviewCase item) =>
        item.ReviewStatus is AdminReviewStatus.Pending or AdminReviewStatus.NeedsMoreInformation;

    public static AdminReviewFilter DefaultWorklistFilter(int page = 1) =>
        new(
            Tab: ReviewWorkTab.Exceptions,
            Category: ReviewWorkCategory.ActionableDeviation,
            Page: Math.Max(1, page),
            PageSize: DefaultPageSize);

    public static AdminReviewSearchResult ApplyFilterAndPage(
        IReadOnlyList<ReviewCase> cases,
        AdminReviewFilter filter,
        int uniqueCaseCount,
        int duplicatesRemoved,
        int rawCaseCount)
    {
        var pageSize = filter.PageSize <= 0 ? DefaultPageSize : filter.PageSize;
        var page = filter.Page <= 0 ? 1 : filter.Page;
        var effective = NormalizeFilter(filter);
        var counts = CountCategories(cases);
        var filtered = ApplyFilter(cases, effective).ToArray();
        var items = filtered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArray();

        return new AdminReviewSearchResult(
            Items: items,
            TotalMatching: filtered.Length,
            Page: page,
            PageSize: pageSize,
            Counts: counts,
            UniqueCaseCount: uniqueCaseCount,
            DuplicatesRemoved: duplicatesRemoved,
            RawCaseCount: rawCaseCount);
    }

    public static AdminReviewFilter NormalizeFilter(AdminReviewFilter filter)
    {
        if (filter.Tab is null && filter.Category is null)
        {
            return DefaultWorklistFilter(filter.Page <= 0 ? 1 : filter.Page) with
            {
                Technician = filter.Technician,
                FromDate = filter.FromDate,
                ThroughDate = filter.ThroughDate,
                ReviewStatus = filter.ReviewStatus,
                MatcherStatus = filter.MatcherStatus,
                MinimumDeviationMinutes = filter.MinimumDeviationMinutes,
                HighPriorityOnly = filter.HighPriorityOnly,
                PageSize = filter.PageSize <= 0 ? DefaultPageSize : filter.PageSize,
            };
        }

        if (filter.Tab is { } tab && filter.Category is null)
        {
            return filter with
            {
                Category = CategoryForTab(tab),
                PageSize = filter.PageSize <= 0 ? DefaultPageSize : filter.PageSize,
                Page = filter.Page <= 0 ? 1 : filter.Page,
            };
        }

        return filter with
        {
            PageSize = filter.PageSize <= 0 ? DefaultPageSize : filter.PageSize,
            Page = filter.Page <= 0 ? 1 : filter.Page,
        };
    }

    public static ReviewWorkCategory CategoryForTab(ReviewWorkTab tab) =>
        tab switch
        {
            ReviewWorkTab.Exceptions => ReviewWorkCategory.ActionableDeviation,
            ReviewWorkTab.SmallDeviations => ReviewWorkCategory.SmallDeviation,
            ReviewWorkTab.MatchUncertainty => ReviewWorkCategory.MatchUncertainty,
            ReviewWorkTab.DataQuality => ReviewWorkCategory.DataQuality,
            ReviewWorkTab.Completed => ReviewWorkCategory.Completed,
            _ => ReviewWorkCategory.ActionableDeviation,
        };

    public static IEnumerable<ReviewCase> ApplyFilter(
        IReadOnlyList<ReviewCase> cases,
        AdminReviewFilter filter)
    {
        IEnumerable<ReviewCase> query = cases;

        if (filter.Category is { } category)
        {
            query = query.Where(item => item.Category == category);
            if (category is not ReviewWorkCategory.Completed)
            {
                query = query.Where(IsOpenForWork);
            }
        }

        // Default worklist: exceptions only.
        if (filter.Category is null && filter.Tab is null)
        {
            query = query.Where(item =>
                item.Category == ReviewWorkCategory.ActionableDeviation && IsOpenForWork(item));
        }

        if (!string.IsNullOrWhiteSpace(filter.Technician))
        {
            query = query.Where(item =>
                item.Technician.Contains(filter.Technician.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (filter.FromDate is { } from)
        {
            query = query.Where(item => item.Date >= from);
        }

        if (filter.ThroughDate is { } through)
        {
            query = query.Where(item => item.Date <= through);
        }

        if (filter.ReviewStatus is { } reviewStatus)
        {
            query = query.Where(item => item.ReviewStatus == reviewStatus);
        }

        if (!string.IsNullOrWhiteSpace(filter.MatcherStatus))
        {
            query = query.Where(item =>
                string.Equals(item.MatcherStatus, filter.MatcherStatus, StringComparison.OrdinalIgnoreCase));
        }

        if (filter.MinimumDeviationMinutes is { } minDev)
        {
            query = query.Where(item => item.MaxDeviationMinutes >= minDev);
        }

        if (filter.HighPriorityOnly)
        {
            query = query.Where(item => item.Priority == SpotcheckPriorityTier.HighPriority);
        }

        return query
            .OrderByDescending(item => item.Date)
            .ThenByDescending(item => item.MaxDeviationMinutes ?? -1)
            .ThenBy(item => item.PerformanceId);
    }
}
