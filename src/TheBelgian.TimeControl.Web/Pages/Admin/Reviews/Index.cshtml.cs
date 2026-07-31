using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Web.Pages.Admin.Reviews;

public sealed class IndexModel(
    IAdminReviewService reviewService,
    IWebHostEnvironment environment,
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

    [BindProperty(SupportsGet = true)]
    public bool Load { get; set; }

    public IReadOnlyList<AdminReviewCase> Cases { get; private set; } = [];
    public string? ErrorMessage { get; private set; }
    public bool Loaded { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
        {
            return NotFound();
        }

        FromDate ??= DateOnly.FromDateTime(DateTime.Today.AddDays(-7));
        ThroughDate ??= DateOnly.FromDateTime(DateTime.Today);

        if (!Load)
        {
            return Page();
        }

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
            Loaded = true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Admin review search failed.");
            ErrorMessage = exception.Message;
        }

        return Page();
    }
}
