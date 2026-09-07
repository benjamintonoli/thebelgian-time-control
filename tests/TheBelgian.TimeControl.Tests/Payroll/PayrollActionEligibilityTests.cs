using System.Text.Json;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Actions;
using TheBelgian.TimeControl.Core.Payroll.Findings;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollActionEligibilityTests
{
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
    public void Create_ConflictingPerformance_Blocked()
    {
        var finding = MissingTech(
            "100",
            new DateOnly(2026, 8, 10),
            PayrollFindingSeverity.Review,
            nameof(MissingTechnicianEvidenceClass.ContradictedByExistingPerformance));
        var result = PayrollActionEligibility.Evaluate(finding, IncludedOpen());
        Assert.Equal(PayrollProposedActionStatus.Blocked, result.Status);
        Assert.Equal(PayrollActionBlockReasonCode.ConflictingPerformance, result.BlockReasonCode);
    }

    [Fact]
    public void Create_Ambiguous_Blocked()
    {
        var finding = MissingTech(
            "100",
            new DateOnly(2026, 8, 10),
            PayrollFindingSeverity.Review,
            nameof(MissingTechnicianEvidenceClass.Ambiguous));
        var result = PayrollActionEligibility.Evaluate(finding, IncludedOpen());
        Assert.Equal(PayrollProposedActionStatus.Blocked, result.Status);
        Assert.Equal(PayrollActionBlockReasonCode.AmbiguousInterval, result.BlockReasonCode);
    }

    [Fact]
    public void Create_Excluded_Blocked()
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
                IsEmployeeIncluded = false,
                IntervalSemanticsOverride = PayrollIntervalSemantics.PayableWork,
                ProvenMainTaskId = 1,
            });
        Assert.Equal(PayrollProposedActionStatus.Blocked, result.Status);
        Assert.Equal(PayrollActionBlockReasonCode.ExcludedEmployee, result.BlockReasonCode);
    }

    [Fact]
    public void Create_Finalized_Blocked()
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
    public void Adjust_ReliableStandby_Ready()
    {
        var start = new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 8, 5, 13, 0, 0, TimeSpan.Zero);
        var proposedStart = new DateTimeOffset(2026, 8, 5, 9, 42, 0, TimeSpan.Zero);
        var proposedEnd = new DateTimeOffset(2026, 8, 5, 12, 21, 0, TimeSpan.Zero);
        var finding = new PayrollFindingRecord
        {
            FindingKey = "StandbyStartMismatch:100:20260805:55",
            ResourceId = "100",
            Date = new DateOnly(2026, 8, 5),
            FindingType = PayrollFindingType.StandbyStartMismatch,
            Severity = PayrollFindingSeverity.High,
            Title = "start",
            Description = "desc",
            Evidence = "ev",
            SuggestedAction = "act",
            RelatedPerformanceIdsJson = JsonSerializer.Serialize(new long[] { 55 }),
            SuggestedPayableStart = proposedStart,
            SuggestedPayableEnd = proposedEnd,
            GpsClassification = nameof(StandbyGpsClassification.PhysicalIntervention),
        };

        var result = PayrollActionEligibility.Evaluate(
            finding,
            IncludedOpen() with
            {
                ExistingPerformanceId = 55,
                ExistingPerformanceStart = start,
                ExistingPerformanceEnd = end,
                ExistingMainTaskExternalId = 23,
            });

        Assert.Equal(PayrollProposedActionStatus.ReadyForApproval, result.Status);
        Assert.NotNull(result.AdjustProposal);
        Assert.Equal(55, result.AdjustProposal!.PerformanceId);
    }

    [Fact]
    public void Adjust_AmbiguousStandby_Blocked()
    {
        var finding = new PayrollFindingRecord
        {
            FindingKey = "standby-ambig:100:20260805:55",
            ResourceId = "100",
            Date = new DateOnly(2026, 8, 5),
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
            Date = new DateOnly(2026, 8, 5),
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
            Date = new DateOnly(2026, 8, 5),
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
