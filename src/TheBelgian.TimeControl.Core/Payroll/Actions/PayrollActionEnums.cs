namespace TheBelgian.TimeControl.Core.Payroll.Actions;

public enum PayrollProposedActionType
{
    CreateMissingPerformance = 1,
    AdjustExistingPerformanceTime = 2,
}

public enum PayrollProposedActionStatus
{
    Proposed = 0,
    Blocked = 1,
    ReadyForApproval = 2,
    Executing = 3,
    Applied = 4,
    Failed = 5,
    Stale = 6,
    Cancelled = 7,
}

/// <summary>
/// Whether a suggested interval represents proven payable work or only supporting GPS/travel evidence.
/// </summary>
public enum PayrollIntervalSemantics
{
    /// <summary>Interval is proven bookable payable work.</summary>
    PayableWork = 0,

    /// <summary>Interval reflects GPS travel/arrival evidence only; not proven payable work.</summary>
    GpsTravelOnly = 1,

    /// <summary>Interval cannot be classified safely.</summary>
    Ambiguous = 2,
}

public enum PayrollActionBlockReasonCode
{
    None = 0,
    NotExecutable = 1,
    ExcludedEmployee = 2,
    MonthFinalized = 3,
    SeverityInsufficient = 4,
    IncompleteTarget = 5,
    GpsTravelOnlyInterval = 6,
    AmbiguousInterval = 7,
    NoGpsData = 8,
    ConflictingPerformance = 9,
    MatchingPerformanceExists = 10,
    UnsupportedFindingType = 11,
    AmbiguousStandbyEvidence = 12,
    OverlapWithoutDeterministicTarget = 13,
    WrongDossierReviewOnly = 14,
    MissingMainTaskId = 15,
    PeerMainTaskNotCopied = 16,
    RelatedPerformanceAmbiguous = 17,
    SourceChanged = 18,
    IncompleteCallout = 19,
    IntermediateStopEnd = 20,
    MultiLegAmbiguous = 21,
    WrongActivityType = 22,
    NonPositiveDuration = 23,
    ConflictingDossierAmbiguity = 24,
    PossiblePhoneThenPhysical = 25,
    RequiresSplitOrProvenBookingMethod = 26,
}
