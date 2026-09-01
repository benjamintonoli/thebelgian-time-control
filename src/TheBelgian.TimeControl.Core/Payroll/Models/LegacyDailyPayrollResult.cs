namespace TheBelgian.TimeControl.Core.Payroll.Models;

public sealed record LegacyDailyPayrollResult(
    string ResourceId,
    DateOnly Date,
    decimal TheoreticalDayHours,
    decimal RegisteredWorkHours,
    decimal TravelStartDeductionHours,
    decimal TravelEndDeductionHours,
    decimal PauseCorrectionHours,
    decimal OverlapCorrectionHours,
    decimal Extra15Hours,
    decimal Extra75KmTotal,
    decimal Extra75AsHours,
    decimal PayableWorkHours,
    decimal RawAbsenceHours,
    decimal PayableAbsenceHours,
    bool HasAbsence,
    decimal FinalDailyTotalHours);
