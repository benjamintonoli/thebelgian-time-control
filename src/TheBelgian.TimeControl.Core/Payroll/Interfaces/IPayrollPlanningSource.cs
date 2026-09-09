using TheBelgian.TimeControl.Core.Payroll.Findings;

namespace TheBelgian.TimeControl.Core.Payroll.Interfaces;

public interface IPayrollPlanningSource
{
    Task<IReadOnlyList<PayrollPlanningReservation>> ReadWorkReservationsAsync(
        DateOnly fromDate,
        DateOnly throughDate,
        IReadOnlyCollection<string> resourceIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as <see cref="ReadWorkReservationsAsync"/> but emits all attendees on calendars
    /// that include any of the filter resources (peer discovery for shared training sessions).
    /// </summary>
    Task<IReadOnlyList<PayrollPlanningReservation>> ReadSharedAttendeeReservationsAsync(
        DateOnly fromDate,
        DateOnly throughDate,
        IReadOnlyCollection<string> resourceIds,
        CancellationToken cancellationToken = default);
}
