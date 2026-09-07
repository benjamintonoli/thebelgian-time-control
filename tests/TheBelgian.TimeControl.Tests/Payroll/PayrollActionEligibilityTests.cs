using System.Text.Json;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Actions;
using TheBelgian.TimeControl.Core.Payroll.Findings;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollActionEligibilityTests
{
    private static readonly DateOnly Day = new(2026, 8, 5);
    private static readonly DateTimeOffset CurrentStart = new(2026, 8, 5, 16, 18, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CurrentEnd = new(2026, 8, 5, 18, 33, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ProposedStart = new(2026, 8, 5, 16, 18, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ProposedEnd = new(2026, 8, 5, 18, 33, 0, TimeSpan.Zero);

    [Fact]
    public void Options_Defaults_AreDisabled_AndExecutionRequiresEnabled()
    {
        var options = new PayrollActionsOptions();
        Assert.False(options.Enabled);
        Assert.False(options.ExecutionEnabled);
        options.ExecutionEnabled = true;
        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Fact]
    public void Hfdtask23_MapsToWaitingTime()
    {
        Assert.Equal(
            PayrollStandbyActivityTypes.WaitingTime,
            PayrollStandbyActivityTypes.FromMainTaskExternalId(23));
        Assert.Null(PayrollStandbyActivityTypes.FromMainTaskExternalId(14));
        Assert.True(PayrollStandbyActivityTypes.IsWaitingTimePerformance(23, "WaitingTime"));
    }

    [Fact]
    public void AyrtonLike_GpsTravelInterval_IsBlocked_NotExecutable()
    {
        var finding = MissingTechHigh(
            resourceId: "388",
            date: new DateOnly(2026, 8, 31),
            start: new DateTimeOffset(2026, 8, 31, 8, 35, 0, TimeSpan.Zero),
            end: new DateTimeOffset(2026, 8, 31, 10, 6, 0, TimeSpan.Zero),
            hours: 1.51m,
            projectId: "65274",
            bonNr: "26601949",
            gpsClass: nameof(MissingTechnicianEvidenceClass.PlanningPlusPeerPlusGps));

        Assert.Equal(PayrollIntervalSemantics.GpsTravelOnly,
            PayrollActionEligibility.ClassifyMissingTechnicianInterval(finding));

        var result = PayrollActionEligibility.Evaluate(finding, IncludedOpen());
        Assert.Equal(PayrollProposedActionStatus.Blocked, result.Status);
        Assert.Equal(PayrollActionBlockReasonCode.GpsTravelOnlyInterval, result.BlockReasonCode);
        Assert.Contains("reis-naar-werf", result.BlockReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Peer-IDHFDTAAK", result.BlockReason!, StringComparison.Ordinal);
        Assert.Null(result.CreateProposal);
    }

    [Fact]
    public void Create_HighCompletePayableWork_ReadyForApproval()
    {
        var finding = MissingTechHigh(
            "100",
            new DateOnly(2026, 8, 10),
            new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero),
            3m,
            "65274",
            "BON1",
            nameof(MissingTechnicianEvidenceClass.PlanningPlusPeerPlusGps));

        var result = PayrollActionEligibility.Evaluate(
            finding,
            IncludedOpen() with
            {
                ProvenMainTaskId = 14,
                IntervalSemanticsOverride = PayrollIntervalSemantics.PayableWork,
            });

        Assert.Equal(PayrollProposedActionStatus.ReadyForApproval, result.Status);
        Assert.NotNull(result.CreateProposal);
        Assert.Equal(14, result.CreateProposal!.MainTaskId);
        Assert.Equal(PayrollIntervalSemantics.PayableWork, result.CreateProposal.IntervalSemantics);
    }

    [Fact]
    public void Create_HighMissingTask_Blocked()
    {
        var finding = MissingTechHigh(
            "100",
            new DateOnly(2026, 8, 10),
            new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero),
            3m,
            "65274",
            "BON1",
            nameof(MissingTechnicianEvidenceClass.PlanningPlusPeerPlusGps));

        var result = PayrollActionEligibility.Evaluate(
            finding,
            IncludedOpen() with { IntervalSemanticsOverride = PayrollIntervalSemantics.PayableWork });

        Assert.Equal(PayrollProposedActionStatus.Blocked, result.Status);
        Assert.Equal(PayrollActionBlockReasonCode.MissingMainTaskId, result.BlockReasonCode);
        Assert.Contains("niet gekopieerd", result.BlockReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_NoGps_Blocked()
    {
        var finding = MissingTech(
            "100",
            new DateOnly(2026, 8, 10),
            PayrollFindingSeverity.Review,
            nameof(MissingTechnicianEvidenceClass.NoGpsData));
        var result = PayrollActionEligibility.Evaluate(finding, IncludedOpen());
        Assert.Equal(PayrollProposedActionStatus.Blocked, result.Status);
        Assert.Equal(PayrollActionBlockReasonCode.NoGpsData, result.BlockReasonCode);
    }

    [Fact]
    public void Create_FinalizedMonth_Blocked()
    {
        var finding = MissingTechHigh(
            "100",
            new DateOnly(2026, 8, 10),
            new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero),
            3m,
            "65274",
            "BON1",
            nameof(MissingTechnicianEvidenceClass.PlanningPlusPeerPlusGps));
        var result = PayrollActionEligibility.Evaluate(
            finding,
            IncludedOpen() with
            {
                IsMonthFinalized = true,
                IntervalSemanticsOverride = PayrollIntervalSemantics.PayableWork,
                ProvenMainTaskId = 1,
            });
        Assert.Equal(PayrollProposedActionStatus.Blocked, result.Status);
        Assert.Equal(PayrollActionBlockReasonCode.MonthFinalized, result.BlockReasonCode);
    }

    [Fact]
    public void Adjust_CompleteHomeSiteHome_ReadyForApproval()
    {
        var finding = StandbyStart(55, ProposedStart, ProposedEnd);
        var result = PayrollActionEligibility.Evaluate(finding, WaitingContext(CompleteCalloutTrips()));
        Assert.Equal(PayrollProposedActionStatus.ReadyForApproval, result.Status);
        Assert.Equal(PayrollStandbyActivityTypes.WaitingTime, result.AdjustProposal!.ExpectedActivityType);
        Assert.Equal(23, result.AdjustProposal.ExpectedMainTaskExternalId);
        Assert.Contains("callout=complete", result.EvidenceSnapshot.CalloutEvidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Adjust_PossiblePhoneThenPhysical_BlocksSimpleCorrection()
    {
        var bookedStart = new DateTimeOffset(2026, 8, 5, 15, 25, 0, TimeSpan.Zero);
        var finding = StandbyStart(55, ProposedStart, ProposedEnd);
        var context = WaitingContext(CompleteCalloutTrips()) with
        {
            ExistingPerformanceStart = bookedStart,
            ExistingPerformanceEnd = ProposedEnd,
        };
        var result = PayrollActionEligibility.Evaluate(finding, context);
        Assert.Equal(PayrollProposedActionStatus.Blocked, result.Status);
        Assert.Equal(PayrollActionBlockReasonCode.PossiblePhoneThenPhysical, result.BlockReasonCode);
        Assert.Contains("telefonische", result.BlockReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hybrid=PossiblePhoneThenPhysical", result.EvidenceSnapshot.CalloutEvidence, StringComparison.Ordinal);
        Assert.Null(result.AdjustProposal);
    }

    [Fact]
    public void Hybrid_Assessment_ExposesPhoneAllowanceAndPhysicalInterval()
    {
        var bookedStart = new DateTimeOffset(2026, 8, 5, 15, 25, 0, TimeSpan.Zero);
        var assessment = StandbyCalloutEvidence.Assess(
            ProposedStart,
            ProposedEnd,
            bookedStart,
            ProposedEnd,
            CompleteCalloutTrips());
        Assert.True(assessment.IsPossiblePhoneThenPhysical);
        Assert.False(assessment.IsComplete);
        Assert.Equal(15m, assessment.MaxTelephoneAllowanceMinutes);
        Assert.Equal(ProposedStart, assessment.OutboundStart);
        Assert.Equal(ProposedEnd, assessment.ReturnEnd);
        Assert.DoesNotContain("definitely", assessment.EvidenceNote, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("indien telefonisch contact bevestigd", assessment.EvidenceNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Adjust_OutboundOnly_Blocked()
    {
        var trips = new[]
        {
            Trip("1", ProposedStart, ProposedStart.AddHours(1),
                "Kapellestraat 1, 1880 Kapelle-op-den-Bos, België",
                "Stationsstraat 1, 1770 Liedekerke, België"),
        };
        var result = PayrollActionEligibility.Evaluate(StandbyStart(55, ProposedStart, ProposedEnd), WaitingContext(trips));
        Assert.Equal(PayrollProposedActionStatus.Blocked, result.Status);
        Assert.Equal(PayrollActionBlockReasonCode.IncompleteCallout, result.BlockReasonCode);
    }

    [Fact]
    public void Adjust_SiteArrivalOnly_Blocked()
    {
        var trips = new[]
        {
            Trip("1", ProposedStart, ProposedStart.AddMinutes(40),
                "Kapellestraat 1, 1880 Kapelle-op-den-Bos, België",
                "Stationsstraat 1, 1770 Liedekerke, België"),
        };
        var result = PayrollActionEligibility.Evaluate(
            StandbyStart(55, ProposedStart, ProposedStart.AddMinutes(40)),
            WaitingContext(trips));
        Assert.Equal(PayrollProposedActionStatus.Blocked, result.Status);
        Assert.Equal(PayrollActionBlockReasonCode.IncompleteCallout, result.BlockReasonCode);
    }

    [Fact]
    public void Adjust_IntermediateStopEnd_Blocked()
    {
        var trips = new[]
        {
            Trip("1", ProposedStart, ProposedStart.AddHours(1),
                "Thuisstraat 1, 1000 Brussel, België",
                "Werfstraat 1, 1500 Halle, België"),
            Trip("2", ProposedStart.AddHours(2), ProposedEnd,
                "Werfstraat 1, 1500 Halle, België",
                "Stopstraat 9, 1790 Affligem, België"),
        };
        var result = PayrollActionEligibility.Evaluate(StandbyEnd(55, ProposedStart, ProposedEnd), WaitingContext(trips));
        Assert.Equal(PayrollProposedActionStatus.Blocked, result.Status);
        Assert.Equal(PayrollActionBlockReasonCode.IntermediateStopEnd, result.BlockReasonCode);
    }

    [Fact]
    public void Adjust_AmbiguousMultiLeg_Blocked()
    {
        var trips = new[]
        {
            Trip("1", ProposedStart, ProposedStart.AddMinutes(30),
                "Thuisstraat 1, 1000 Brussel, België",
                "Werf A, 2000 Antwerpen, België"),
            Trip("2", ProposedStart.AddHours(1), ProposedStart.AddHours(2),
                "Werf A, 2000 Antwerpen, België",
                "Andere Werf, 9000 Gent, België"),
            Trip("3", ProposedStart.AddHours(2), ProposedEnd,
                "Andere Werf, 9000 Gent, België",
                "Thuisstraat 1, 1000 Brussel, België"),
        };
        var result = PayrollActionEligibility.Evaluate(StandbyStart(55, ProposedStart, ProposedEnd), WaitingContext(trips));
        Assert.Equal(PayrollProposedActionStatus.Blocked, result.Status);
        Assert.Equal(PayrollActionBlockReasonCode.MultiLegAmbiguous, result.BlockReasonCode);
    }

    [Fact]
    public void Adjust_NoGps_Blocked()
    {
        var finding = StandbyStart(55, ProposedStart, ProposedEnd);
        finding.GpsClassification = nameof(StandbyGpsClassification.NoGpsData);
        var result = PayrollActionEligibility.Evaluate(finding, WaitingContext([]));
        Assert.Equal(PayrollProposedActionStatus.Blocked, result.Status);
        Assert.Equal(PayrollActionBlockReasonCode.NoGpsData, result.BlockReasonCode);
    }

    [Fact]
    public void Adjust_StartAndEndSamePerformance_ExactlyOneAggregatedActionProposal()
    {
        var start = StandbyStart(55, ProposedStart, ProposedEnd);
        var end = StandbyEnd(55, ProposedStart, ProposedEnd);
        end.FindingKey = "StandbyEndMismatch:100:20260805:55";
        var result = PayrollActionEligibility.EvaluateStandbyAdjustGroup([start, end], WaitingContext(CompleteCalloutTrips()));
        Assert.Equal(PayrollProposedActionStatus.ReadyForApproval, result.Status);
        Assert.Equal("standby-adjust:100:20260805:55", result.EvidenceSnapshot.FindingKey);
        Assert.Equal(2, result.EvidenceSnapshot.SourceFindingKeys!.Count);
        Assert.NotNull(result.AdjustProposal);
    }

    [Fact]
    public void Adjust_OneBoundary_WithIndependentlyProvenOther_Ready()
    {
        // Start changes; existing end matches return home.
        var result = PayrollActionEligibility.Evaluate(
            StandbyStart(55, ProposedStart, ProposedEnd),
            WaitingContext(CompleteCalloutTrips()));
        Assert.Equal(PayrollProposedActionStatus.ReadyForApproval, result.Status);
    }

    [Fact]
    public void Adjust_OneBoundary_WithUnsafeOtherBoundary_Blocked()
    {
        // Proposed end lands on Affligem stop, not home return.
        var unsafeEnd = new DateTimeOffset(2026, 8, 5, 17, 0, 0, TimeSpan.Zero);
        var trips = new[]
        {
            Trip("1", ProposedStart, ProposedStart.AddMinutes(25),
                "Thuisstraat 1, 1000 Brussel, België",
                "Werfstraat 1, 1500 Halle, België"),
            Trip("2", ProposedStart.AddMinutes(30), unsafeEnd,
                "Werfstraat 1, 1500 Halle, België",
                "Stopstraat 9, 1790 Affligem, België"),
        };
        var finding = StandbyStart(55, ProposedStart, unsafeEnd);
        var context = WaitingContext(trips) with
        {
            ExistingPerformanceStart = CurrentStart,
            ExistingPerformanceEnd = unsafeEnd,
        };
        var result = PayrollActionEligibility.Evaluate(finding, context);
        Assert.Equal(PayrollProposedActionStatus.Blocked, result.Status);
        Assert.Equal(PayrollActionBlockReasonCode.IntermediateStopEnd, result.BlockReasonCode);
    }

    [Fact]
    public void Adjust_FinalizedMonth_Blocked()
    {
        var result = PayrollActionEligibility.Evaluate(
            StandbyStart(55, ProposedStart, ProposedEnd),
            WaitingContext(CompleteCalloutTrips()) with { IsMonthFinalized = true });
        Assert.Equal(PayrollProposedActionStatus.Blocked, result.Status);
        Assert.Equal(PayrollActionBlockReasonCode.MonthFinalized, result.BlockReasonCode);
    }

    [Fact]
    public void Adjust_Non23_BlockedWrongActivity()
    {
        var result = PayrollActionEligibility.Evaluate(
            StandbyStart(55, ProposedStart, ProposedEnd),
            WaitingContext(CompleteCalloutTrips()) with
            {
                ExistingMainTaskExternalId = 14,
                ExistingActivityType = "CustomerWork",
            });
        Assert.Equal(PayrollProposedActionStatus.Blocked, result.Status);
        Assert.Equal(PayrollActionBlockReasonCode.WrongActivityType, result.BlockReasonCode);
    }

    [Fact]
    public void Adjust_AmbiguousStandby_Blocked()
    {
        var finding = new PayrollFindingRecord
        {
            FindingKey = "standby-ambig:100:20260805:55",
            ResourceId = "100",
            Date = Day,
            FindingType = PayrollFindingType.StandbyAmbiguousEvidence,
            Severity = PayrollFindingSeverity.Review,
            Title = "ambig",
            Description = "desc",
            Evidence = "ev",
            SuggestedAction = "act",
            RelatedPerformanceIdsJson = "[55]",
            GpsClassification = nameof(StandbyGpsClassification.Ambiguous),
        };
        var result = PayrollActionEligibility.Evaluate(finding, IncludedOpen());
        Assert.Equal(PayrollProposedActionStatus.Blocked, result.Status);
        Assert.Equal(PayrollActionBlockReasonCode.AmbiguousStandbyEvidence, result.BlockReasonCode);
    }

    [Fact]
    public void Adjust_Overlap_Blocked()
    {
        var finding = new PayrollFindingRecord
        {
            FindingKey = "overlap:100:1",
            ResourceId = "100",
            Date = Day,
            FindingType = PayrollFindingType.OverlappingPerformances,
            Severity = PayrollFindingSeverity.High,
            Title = "overlap",
            Description = "desc",
            Evidence = "ev",
            SuggestedAction = "act",
            RelatedPerformanceIdsJson = "[1,2]",
        };
        var result = PayrollActionEligibility.Evaluate(finding, IncludedOpen());
        Assert.Equal(PayrollProposedActionStatus.Blocked, result.Status);
        Assert.Equal(PayrollActionBlockReasonCode.OverlapWithoutDeterministicTarget, result.BlockReasonCode);
    }

    [Theory]
    [InlineData(PayrollFindingType.Project100TrainingHours)]
    [InlineData(PayrollFindingType.Project200WithoutPlanning)]
    [InlineData(PayrollFindingType.Project300WithoutPlanning)]
    public void SpecialProjects_100_200_300_Blocked(PayrollFindingType type)
    {
        var finding = new PayrollFindingRecord
        {
            FindingKey = $"special:{type}",
            ResourceId = "100",
            Date = Day,
            FindingType = type,
            Severity = PayrollFindingSeverity.Review,
            Title = "t",
            Description = "d",
            Evidence = "e",
            SuggestedAction = "a",
            RelatedPerformanceIdsJson = "[]",
        };
        var result = PayrollActionEligibility.Evaluate(finding, IncludedOpen());
        Assert.Equal(PayrollProposedActionStatus.Blocked, result.Status);
        Assert.Equal(PayrollActionBlockReasonCode.UnsupportedFindingType, result.BlockReasonCode);
    }

    private static PayrollActionEligibilityContext IncludedOpen() =>
        new(
            IsEmployeeIncluded: true,
            IsMonthFinalized: false,
            HasMatchingExistingPerformance: false,
            ProvenMainTaskId: null,
            ExistingPerformanceStart: null,
            ExistingPerformanceEnd: null,
            ExistingPerformanceId: null);

    private static PayrollActionEligibilityContext WaitingContext(
        IReadOnlyList<StandbyGpsTripEvidence> trips) =>
        IncludedOpen() with
        {
            ExistingPerformanceId = 55,
            ExistingPerformanceStart = CurrentStart,
            ExistingPerformanceEnd = CurrentEnd,
            ExistingMainTaskExternalId = 23,
            ExistingActivityType = PayrollStandbyActivityTypes.WaitingTime,
            StandbyDayTrips = trips,
        };

    private static StandbyGpsTripEvidence[] CompleteCalloutTrips() =>
    [
        Trip("out", ProposedStart, ProposedStart.AddMinutes(25),
            "Kapellestraat 12, 1880 Kapelle-op-den-Bos, België",
            "Stationsstraat 5, 1770 Liedekerke, België"),
        Trip("back", ProposedEnd.AddMinutes(-20), ProposedEnd,
            "Stationsstraat 5, 1770 Liedekerke, België",
            "Dorpsstraat 3, 1880 Kapelle-op-den-Bos, België"),
    ];

    private static StandbyGpsTripEvidence Trip(
        string id,
        DateTimeOffset start,
        DateTimeOffset end,
        string from,
        string to) =>
        new(id, start, end, 12m, 20, from, to, "obj", "1-ABC-123");

    private static PayrollFindingRecord StandbyStart(
        long performanceId,
        DateTimeOffset proposedStart,
        DateTimeOffset proposedEnd) =>
        new()
        {
            FindingKey = $"StandbyStartMismatch:100:{Day:yyyyMMdd}:{performanceId}",
            ResourceId = "100",
            Date = Day,
            FindingType = PayrollFindingType.StandbyStartMismatch,
            Severity = PayrollFindingSeverity.High,
            Title = "start",
            Description = "desc",
            Evidence = "ev",
            SuggestedAction = "act",
            RelatedPerformanceIdsJson = JsonSerializer.Serialize(new[] { performanceId }),
            SuggestedPayableStart = proposedStart,
            SuggestedPayableEnd = proposedEnd,
            GpsClassification = nameof(StandbyGpsClassification.PhysicalIntervention),
        };

    private static PayrollFindingRecord StandbyEnd(
        long performanceId,
        DateTimeOffset proposedStart,
        DateTimeOffset proposedEnd) =>
        new()
        {
            FindingKey = $"StandbyEndMismatch:100:{Day:yyyyMMdd}:{performanceId}",
            ResourceId = "100",
            Date = Day,
            FindingType = PayrollFindingType.StandbyEndMismatch,
            Severity = PayrollFindingSeverity.High,
            Title = "end",
            Description = "desc",
            Evidence = "ev",
            SuggestedAction = "act",
            RelatedPerformanceIdsJson = JsonSerializer.Serialize(new[] { performanceId }),
            SuggestedPayableStart = proposedStart,
            SuggestedPayableEnd = proposedEnd,
            GpsClassification = nameof(StandbyGpsClassification.PhysicalIntervention),
        };

    private static PayrollFindingRecord MissingTechHigh(
        string resourceId,
        DateOnly date,
        DateTimeOffset start,
        DateTimeOffset end,
        decimal hours,
        string projectId,
        string bonNr,
        string gpsClass) =>
        new()
        {
            FindingKey = $"missing-tech:1:{date:yyyyMMdd}:{resourceId}",
            ResourceId = resourceId,
            Date = date,
            FindingType = PayrollFindingType.MissingPlannedTechnicianPerformance,
            Severity = PayrollFindingSeverity.High,
            Title = "Mogelijk ontbrekende prestatie",
            Description = "desc",
            Evidence = "evidence",
            SuggestedAction = "act",
            RelatedPerformanceIdsJson = "[14]",
            SuggestedPayableStart = start,
            SuggestedPayableEnd = end,
            SuggestedPayableHours = hours,
            SuggestedProjectId = projectId,
            SuggestedBonNr = bonNr,
            GpsClassification = gpsClass,
        };

    private static PayrollFindingRecord MissingTech(
        string resourceId,
        DateOnly date,
        PayrollFindingSeverity severity,
        string gpsClass) =>
        new()
        {
            FindingKey = $"missing-tech:1:{date:yyyyMMdd}:{resourceId}",
            ResourceId = resourceId,
            Date = date,
            FindingType = PayrollFindingType.MissingPlannedTechnicianPerformance,
            Severity = severity,
            Title = "Mogelijk ontbrekende prestatie",
            Description = "desc",
            Evidence = "evidence",
            SuggestedAction = "act",
            RelatedPerformanceIdsJson = "[]",
            GpsClassification = gpsClass,
        };
}
