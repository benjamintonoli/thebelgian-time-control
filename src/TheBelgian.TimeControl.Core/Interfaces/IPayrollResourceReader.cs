using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Core.Interfaces;

public interface IPayrollResourceReader
{
    Task<IReadOnlyList<PayrollEmployeeCandidate>> ReadCandidatesAsync(
        CancellationToken cancellationToken);
}
