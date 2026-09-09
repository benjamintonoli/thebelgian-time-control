using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Interfaces;

namespace TheBelgian.TimeControl.Tests.Payroll;

internal sealed class EmptyPayrollPlanningSource : IPayrollPlanningSource
{
    public Task<IReadOnlyList<PayrollPlanningReservation>> ReadWorkReservationsAsync(
        DateOnly fromDate,
        DateOnly throughDate,
        IReadOnlyCollection<string> resourceIds,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PayrollPlanningReservation>>([]);

    public Task<IReadOnlyList<PayrollPlanningReservation>> ReadSharedAttendeeReservationsAsync(
        DateOnly fromDate,
        DateOnly throughDate,
        IReadOnlyCollection<string> resourceIds,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PayrollPlanningReservation>>([]);
}
