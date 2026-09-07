namespace TheBelgian.TimeControl.Core.Payroll.Findings;

public enum PayrollFindingType
{
    Project300WithoutPlanning = 1,
    Project200WithoutPlanning = 2,
    Project200ExceedsPlanning = 3,
    Project100TrainingHours = 4,
    Project100TrainingInOvertime = 5,
    Project100ExceedsPlannedDuration = 6,
    OverlappingPerformances = 7,
}

public enum PayrollFindingSeverity
{
    Info = 0,
    Review = 1,
    High = 2,
}

public enum PayrollFindingStatus
{
    Open = 0,
    Reviewed = 1,
    Dismissed = 2,
    Resolved = 3,
}

public enum PayrollPlanningClassification
{
    WorkReservation = 0,
    Absence = 1,
    Standby = 2,
    Ambiguous = 3,
}
