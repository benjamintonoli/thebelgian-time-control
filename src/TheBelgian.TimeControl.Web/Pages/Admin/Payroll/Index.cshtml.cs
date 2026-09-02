using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Configuration;

namespace TheBelgian.TimeControl.Web.Pages.Admin.Payroll;

public sealed class IndexModel(
    IPayrollShadowService payrollShadowService,
    ICurrentUserContext currentUser,
    IOptions<PayrollShadowOptions> payrollOptions,
    IOptions<AdminReviewWorkflowOptions> reviewOptions,
    ILogger<IndexModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)] public int? Year { get; set; }
    [BindProperty(SupportsGet = true)] public int? Month { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? EvaluationDate { get; set; }

    public IReadOnlyList<PayrollShadowMonthSummary> Months { get; private set; } = [];
    public bool FeatureEnabled { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        try
        {
            Months = await payrollShadowService.ListMonthsAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Payroll shadow month list failed.");
            Error = exception.Message;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        if (Year is null or < 2000 or > 2100 || Month is null or < 1 or > 12)
        {
            Error = "Ongeldige maand.";
            return await OnGetAsync(cancellationToken);
        }

        try
        {
            var actor = RequireActor();
            var evaluationDate = EvaluationDate
                ?? new DateOnly(Year.Value, Month.Value, 1).AddMonths(1);
            await payrollShadowService.CreateSnapshotAsync(
                Year.Value,
                Month.Value,
                evaluationDate,
                actor.AuditIdentity,
                cancellationToken);
            Message = $"Shadow-maand {Month:00}/{Year} aangemaakt.";
            return RedirectToPage("./Month", new { year = Year, month = Month });
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Payroll shadow snapshot creation failed.");
            Error = exception.Message;
            return await OnGetAsync(cancellationToken);
        }
    }

    private bool EnsureUiEnabled()
    {
        payrollOptions.Value.Validate();
        FeatureEnabled = payrollOptions.Value.Enabled && payrollOptions.Value.AdminUiEnabled;
        return FeatureEnabled;
    }

    private AuthenticatedActor RequireActor() =>
        currentUser.RequireActor(reviewOptions.Value.DefaultReviewer);
}
