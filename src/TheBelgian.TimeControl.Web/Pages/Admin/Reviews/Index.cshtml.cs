using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Services;

namespace TheBelgian.TimeControl.Web.Pages.Admin.Reviews;

public sealed class IndexModel(
    IAdminReviewService reviewService,
    ILogger<IndexModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public ReviewWorkTab? Tab { get; set; }

    [BindProperty(SupportsGet = true)]
    public ReviewWorkCategory? Category { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Technician { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? FromDate { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? ThroughDate { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public AdminReviewSearchResult Result { get; private set; } = new(
        [],
        0,
        1,
        SpotcheckPriorityCalculator.DefaultPageSize,
        new AdminReviewCategoryCounts(0, 0, 0, 0, 0, 0, 0),
        0,
        0,
        0);

    public string? Error { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            var filter = new AdminReviewFilter(
                Tab: Tab,
                Category: Category,
                Technician: Technician,
                FromDate: FromDate,
                ThroughDate: ThroughDate,
                Page: PageNumber <= 0 ? 1 : PageNumber,
                PageSize: SpotcheckPriorityCalculator.DefaultPageSize);
            Result = await reviewService.SearchAsync(filter, cancellationToken);
            var normalized = SpotcheckPriorityCalculator.NormalizeFilter(filter);
            Tab = normalized.Tab;
            Category = normalized.Category;
            PageNumber = Result.Page;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Admin review list failed.");
            Error = ex.Message;
        }
    }
}
