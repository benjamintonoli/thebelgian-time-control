using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Core.Payroll.Interfaces;

public interface IPayrollCalendarSource
{
    Task<IReadOnlyList<PlenionCalendarRow>> ReadCalendarRowsAsync(
        DateOnly fromDate,
        DateOnly throughDate,
        CancellationToken cancellationToken = default);
}
