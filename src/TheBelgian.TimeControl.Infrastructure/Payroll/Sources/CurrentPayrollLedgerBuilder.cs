using TheBelgian.TimeControl.Core.Payroll;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Sources;

public static class CurrentPayrollLedgerBuilder
{
    public static CurrentPayrollLedger Build(
        IReadOnlyList<NormalizedPerformanceEntry> performances,
        IReadOnlyList<CalendarSyntheticEntry> syntheticAbsences) =>
        new(performances, syntheticAbsences);
}
