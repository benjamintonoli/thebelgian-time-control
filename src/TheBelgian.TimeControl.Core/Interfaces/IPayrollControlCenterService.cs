using TheBelgian.TimeControl.Core.Payroll.Review;

namespace TheBelgian.TimeControl.Core.Interfaces;

public interface IPayrollControlCenterService
{
    Task<PayrollControlCenterPage?> GetAsync(
        int year,
        int month,
        CancellationToken cancellationToken);
}
