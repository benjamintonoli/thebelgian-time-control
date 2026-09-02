using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Configuration;

namespace TheBelgian.TimeControl.Web.Pages.Admin.Payroll;

public sealed class EmployeeModel(
    IPayrollShadowService payrollShadowService,
    ICurrentUserContext currentUser,
    IOptions<PayrollShadowOptions> payrollOptions,
    IOptions<AdminReviewWorkflowOptions> reviewOptions,
    ILogger<EmployeeModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)] public int Year { get; set; }
    [BindProperty(SupportsGet = true)] public int Month { get; set; }
    [BindProperty(SupportsGet = true)] public string ResourceId { get; set; } = string.Empty;

    [BindProperty] public DateOnly ValidFrom { get; set; }
    [BindProperty] public DateOnly? ValidTo { get; set; }
    [BindProperty] public string ReasonCode { get; set; } = string.Empty;
    [BindProperty] public string? Comment { get; set; }
    [BindProperty] public string? ReviewComment { get; set; }

    public PayrollShadowEmployeeDetail? Detail { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostIncludeAsync(CancellationToken cancellationToken) =>
        await SaveEligibilityAsync(PayrollEligibilityStatus.Included, cancellationToken);

    public async Task<IActionResult> OnPostExcludeAsync(CancellationToken cancellationToken) =>
        await SaveEligibilityAsync(PayrollEligibilityStatus.Excluded, cancellationToken);

    public async Task<IActionResult> OnPostResetEligibilityAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        try
        {
            await payrollShadowService.ResetEligibilityAsync(
                new SetPayrollEligibilityResetRequest(ResourceId, ValidFrom, ValidTo, ReasonCode, Comment),
                RequireActor().AuditIdentity,
                cancellationToken);
            Message = "Eligibility reset opgeslagen.";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Payroll eligibility reset failed.");
            Error = exception.Message;
        }

        return await OnGetAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAcceptAsync(CancellationToken cancellationToken) =>
        await SaveReviewAsync(PayrollEmployeeReviewStatus.Accepted, cancellationToken);

    public async Task<IActionResult> OnPostNeedsFollowUpAsync(CancellationToken cancellationToken) =>
        await SaveReviewAsync(PayrollEmployeeReviewStatus.NeedsFollowUp, cancellationToken);

    public async Task<IActionResult> OnPostResetReviewAsync(CancellationToken cancellationToken) =>
        await SaveReviewAsync(PayrollEmployeeReviewStatus.Pending, cancellationToken);

    private async Task<IActionResult> SaveEligibilityAsync(
        PayrollEligibilityStatus status,
        CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        try
        {
            await payrollShadowService.SetEligibilityAsync(
                new SetPayrollEligibilityRequest(ResourceId, ValidFrom, ValidTo, status, ReasonCode, Comment),
                RequireActor().AuditIdentity,
                cancellationToken);
            Message = "Eligibility opgeslagen.";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Payroll eligibility save failed.");
            Error = exception.Message;
        }

        return await OnGetAsync(cancellationToken);
    }

    private async Task<IActionResult> SaveReviewAsync(
        PayrollEmployeeReviewStatus status,
        CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        try
        {
            await payrollShadowService.SetReviewStatusAsync(
                new SetPayrollReviewStatusRequest(Year, Month, ResourceId, status, ReviewComment),
                RequireActor().AuditIdentity,
                cancellationToken);
            Message = "Reviewstatus opgeslagen.";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Payroll review save failed.");
            Error = exception.Message;
        }

        return await OnGetAsync(cancellationToken);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Detail = await payrollShadowService.GetEmployeeDetailAsync(
            Year,
            Month,
            ResourceId,
            cancellationToken);
        if (Detail is not null && ValidFrom == default)
        {
            ValidFrom = Detail.Month.PeriodStart;
        }
    }

    private bool EnsureUiEnabled() =>
        payrollOptions.Value.Enabled && payrollOptions.Value.AdminUiEnabled;

    private AuthenticatedActor RequireActor() =>
        currentUser.RequireActor(reviewOptions.Value.DefaultReviewer);
}
