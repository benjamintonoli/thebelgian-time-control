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
    StandbyPhoneExceeds15Min = 8,
    StandbyStartMismatch = 9,
    StandbyEndMismatch = 10,
    StandbyDurationMismatch = 11,
    StandbyPossibleWrongDossier = 12,
    StandbyAmbiguousEvidence = 13,
    StandbyNoGpsData = 14,
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

public enum StandbyGpsClassification
{
    PhoneOnly = 0,
    PhysicalIntervention = 1,
    Ambiguous = 2,
    NoGpsData = 3,
}
