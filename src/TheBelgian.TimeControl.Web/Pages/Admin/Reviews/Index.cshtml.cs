using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Web.Pages.Admin.Reviews;

public sealed class IndexModel(
    IAdminReviewService reviewService,
    ILogger<IndexModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Technician { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? FromDate { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? ThroughDate { get; set; }

    [BindProperty(SupportsGet = true)]
    public AdminReviewStatus? ReviewStatus { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? MatcherStatus { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? MinimumDeviationMinutes { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool HighPriorityOnly { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool ProposedMatchesOnly { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool AmbiguousOrUnresolvedOnly { get; set; }

    public IReadOnlyList<ReviewCase> Cases { get; private set; } = [];

    public string DataSourceName { get; private set; } = string.Empty;

    public string? Error { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        DataSourceName = reviewService.DataSourceName;
        try
        {
            Cases = await reviewService.SearchAsync(
                new AdminReviewFilter(
                    Technician,
                    FromDate,
                    ThroughDate,
                    ReviewStatus,
                    MatcherStatus,
                    MinimumDeviationMinutes,
                    HighPriorityOnly,
                    ProposedMatchesOnly,
                    AmbiguousOrUnresolvedOnly),
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Admin review list failed.");
            Error = ex.Message;
            Cases = [];
        }
    }
}
