using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Actions;
using TheBelgian.TimeControl.Core.Payroll.Findings;

namespace TheBelgian.TimeControl.Core.Interfaces;

public interface IPayrollActionService
{
    Task<IReadOnlyList<PayrollProposedActionRecord>> ProposeFromFindingsAsync(
        int year,
        int month,
        string? resourceId,
        string actor,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PayrollProposedActionRecord>> ListActionsAsync(
        int year,
        int month,
        string? resourceId,
        CancellationToken cancellationToken);

    Task<PayrollProposedActionRecord?> GetActionAsync(
        Guid actionId,
        CancellationToken cancellationToken);

    Task<PayrollActionConfirmationView?> PrepareConfirmationAsync(
        Guid actionId,
        CancellationToken cancellationToken);

    Task<PayrollActionExecutionResult> ExecuteAsync(
        Guid actionId,
        string comment,
        string actor,
        CancellationToken cancellationToken);

    Task CancelAsync(
        Guid actionId,
        string actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Proposes a human-approved delete for any finding/workbench source that has a concrete performance id.
    /// Propose works when PayrollActions:Enabled; execute is gated by DeletePerformanceEnabled.
    /// </summary>
    Task<PayrollActionProposeResult> ProposeDeleteForPerformanceAsync(
        int year,
        int month,
        string resourceId,
        DateOnly workDate,
        long performanceId,
        string reason,
        string actor,
        PayrollFindingType findingType,
        string? actionKey = null,
        string? sourceFindingKey = null,
        int? sourceFindingId = null,
        IReadOnlyList<string>? sourceFindingKeys = null,
        IReadOnlyList<int>? sourceFindingIds = null,
        string? prestOmschr = null,
        string? prestMemo = null,
        string? bonTechnicianRemark = null,
        string? projectLabel = null,
        string? expectedActivityType = null,
        CancellationToken cancellationToken = default);
}

public sealed record PayrollActionProposeResult(
    bool Ok,
    string Message,
    Guid? ActionId,
    string? BlockReason);
