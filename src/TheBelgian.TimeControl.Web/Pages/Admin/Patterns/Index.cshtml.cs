using Microsoft.AspNetCore.Mvc.RazorPages;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Services;

namespace TheBelgian.TimeControl.Web.Pages.Admin.Patterns;

public sealed class IndexModel(
    IAdminReviewService reviewService,
    ILogger<IndexModel> logger) : PageModel
{
    public IReadOnlyList<ReviewCase> PatternCases { get; private set; } = [];

    public string? Error { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Completed tab includes Confirmed cases; pattern flag only on Confirmed.
            var result = await reviewService.SearchAsync(
                new AdminReviewFilter(
                    Tab: ReviewWorkTab.Completed,
                    Category: ReviewWorkCategory.Completed,
                    Page: 1,
                    PageSize: 500),
                cancellationToken);
            PatternCases = result.Items
                .Where(item => item.HasRecurringConfirmedPattern)
                .OrderBy(item => item.Technician)
                .ThenByDescending(item => item.Date)
                .ToArray();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Patterns page failed.");
            Error = ex.Message;
        }
    }
}
