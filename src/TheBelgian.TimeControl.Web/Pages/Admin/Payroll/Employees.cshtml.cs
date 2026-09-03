using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Configuration;

namespace TheBelgian.TimeControl.Web.Pages.Admin.Payroll;

public sealed class EmployeesModel(
    IPayrollShadowService payrollShadowService,
    IPayrollResourceReader resourceReader,
    ICurrentUserContext currentUser,
    IOptions<PayrollShadowOptions> payrollOptions,
    IOptions<AdminReviewWorkflowOptions> reviewOptions,
    ILogger<EmployeesModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)] public PayrollRosterFilterKind Kind { get; set; } = PayrollRosterFilterKind.All;
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? AsOfDate { get; set; }

    [BindProperty] public DateOnly ValidFrom { get; set; }
    [BindProperty] public string ReasonCode { get; set; } = "RosterConfirmation";
    [BindProperty] public string? Comment { get; set; }
    [BindProperty] public List<PayrollRosterSelectionRow> Rows { get; set; } = [];

    [BindProperty] public string? ManualResourceId { get; set; }
    [BindProperty] public DateOnly ManualValidFrom { get; set; }
    [BindProperty] public string ManualReasonCode { get; set; } = "ManualPayrollInclusion";
    [BindProperty] public string? ManualComment { get; set; }

    public PayrollRosterPage? Roster { get; private set; }
    public IReadOnlyList<PayrollEmployeeCandidate> ManualSearchResults { get; private set; } = [];
    public string? Message { get; private set; }
    public string? Error { get; private set; }
    public int PreviewIncludeCount { get; private set; }
    public int PreviewExcludeCount { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        ValidFrom = AsOfDate ?? DateOnly.FromDateTime(DateTime.Today);
        ManualValidFrom = ValidFrom;
        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostConfirmAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        try
        {
            var (included, excluded) = PayrollRosterSelectionSplitter.Split(Rows);
            PreviewIncludeCount = included.Count;
            PreviewExcludeCount = excluded.Count;

            await payrollShadowService.ConfirmPayrollRosterSelectionAsync(
                new ConfirmPayrollRosterSelectionRequest(
                    ValidFrom,
                    included,
                    excluded,
                    ReasonCode,
                    Comment),
                RequireActor().AuditIdentity,
                cancellationToken);
            Message = $"Payrollselectie opgeslagen: {included.Count} inbegrepen, {excluded.Count} uitgesloten.";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Payroll roster confirmation failed.");
            Error = exception.Message;
            await LoadAsync(cancellationToken);
            return Page();
        }

        return await OnGetAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAddManualAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        try
        {
            await payrollShadowService.AddManualPayrollEmployeeAsync(
                new AddManualPayrollEmployeeRequest(
                    ManualResourceId ?? string.Empty,
                    ManualValidFrom,
                    null,
                    ManualReasonCode,
                    ManualComment),
                RequireActor().AuditIdentity,
                cancellationToken);
            Message = $"Medewerker {ManualResourceId} handmatig Included.";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Manual payroll employee add failed.");
            Error = exception.Message;
        }

        return await OnGetAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostSearchManualAsync(CancellationToken cancellationToken)
    {
        if (!EnsureUiEnabled())
        {
            return NotFound();
        }

        await LoadAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(Search))
        {
            return Page();
        }

        var all = await resourceReader.ReadCandidatesAsync(cancellationToken);
        var rosterIds = Roster?.Rows.Select(item => item.ResourceId).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        ManualSearchResults = all
            .Where(item => !rosterIds.Contains(item.ResourceId))
            .Where(item =>
                item.DisplayName.Contains(Search, StringComparison.OrdinalIgnoreCase)
                || (item.Function?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false)
                || item.ResourceId.Contains(Search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(25)
            .ToList();
        return Page();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Roster = await payrollShadowService.GetPayrollRosterAsync(
            new PayrollRosterFilter(Kind, Search, AsOfDate ?? ValidFrom),
            cancellationToken);
    }

    private bool EnsureUiEnabled()
    {
        payrollOptions.Value.Validate();
        return payrollOptions.Value.Enabled && payrollOptions.Value.AdminUiEnabled;
    }

    private AuthenticatedActor RequireActor() =>
        currentUser.RequireActor(reviewOptions.Value.DefaultReviewer);
}
