using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Actions;

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
}
