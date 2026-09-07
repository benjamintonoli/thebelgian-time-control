using TheBelgian.TimeControl.Core.Payroll.Findings;

namespace TheBelgian.TimeControl.Core.Payroll.Interfaces;

public interface IPayrollPlanningSource
{
    Task<IReadOnlyList<PayrollPlanningReservation>> ReadWorkReservationsAsync(
        DateOnly fromDate,
        DateOnly throughDate,
        IReadOnlyCollection<string> resourceIds,
        CancellationToken cancellationToken = default);
}
