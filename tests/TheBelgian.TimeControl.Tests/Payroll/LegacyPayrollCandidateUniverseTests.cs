using TheBelgian.TimeControl.Core.Payroll.Configuration;
using TheBelgian.TimeControl.Core.Payroll.Legacy;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class LegacyPayrollAutoCandidateSelectorTests
{
    private static readonly DateOnly PeriodStart = new(2026, 8, 1);
    private static readonly IReadOnlySet<string> NoTask23 = new HashSet<string>(StringComparer.Ordinal);

    [Theory]
    [InlineData("Technieker")]
    [InlineData("Service Technieker")]
    [InlineData("Service technieker")]
    [InlineData("CCTV engineer")]
    [InlineData("CCTV Technieker")]
    [InlineData("Project Technician")]
    [InlineData("Project Technieker")]
    public void TechnicianFunction_WithAcertaPresent_IsAutoCandidate(string function)
    {
        var candidate = Candidate("10", "Ada Technician", function);
        Assert.True(LegacyPayrollAutoCandidateSelector.IsAutoCandidate(candidate, PeriodStart, NoTask23));
    }

    [Fact]
    public void Technician_WithAcertaMissing_IsNotAutoCandidate()
    {
        var candidate = Candidate("11", "Vendor Transport", "Technieker") with
        {
            AcertaIdentityStatus = AcertaIdentityStatus.Missing,
        };
        Assert.False(LegacyPayrollAutoCandidateSelector.IsAutoCandidate(candidate, PeriodStart, NoTask23));
    }

    [Fact]
    public void Designer_WithAcertaPresent_IsNotAutoCandidate()
    {
        var candidate = Candidate("20", "Dana Designer", "Designer");
        Assert.False(LegacyPayrollAutoCandidateSelector.IsAutoCandidate(candidate, PeriodStart, NoTask23));
    }

    [Fact]
    public void Technician_WithOaMarker_AndAcertaPresent_IsNotAutoCandidate()
    {
        var candidate = Candidate("25", "Kevin De Coster (OA)", "Technieker");
        Assert.False(LegacyPayrollAutoCandidateSelector.IsAutoCandidate(candidate, PeriodStart, NoTask23));
        Assert.True(LegacyPayrollNameMarkers.IsLegacyOaMarker(candidate.DisplayName));
    }

    [Fact]
    public void Technician_WithStagiairMarker_AndAcertaPresent_IsNotAutoCandidate()
    {
        var candidate = Candidate("637", "Bedi Mavinga (Stagair)", "Technieker");
        Assert.False(LegacyPayrollAutoCandidateSelector.IsAutoCandidate(candidate, PeriodStart, NoTask23));
        Assert.True(LegacyPayrollNameMarkers.IsLegacyStagiairMarker(candidate.DisplayName));
    }

    [Fact]
    public void ProjectLeider_OrdinaryOnly_IsNotAutoCandidate()
    {
        var candidate = Candidate("30", "Pat Leader", LegacyPayrollTechnicianFunctions.ProjectLeider);
        Assert.False(LegacyPayrollAutoCandidateSelector.IsAutoCandidate(candidate, PeriodStart, NoTask23));
    }

    [Fact]
    public void ProjectLeider_WithTask23_AndAcertaPresent_IsAutoCandidate()
    {
        var candidate = Candidate("30", "Pat Leader", LegacyPayrollTechnicianFunctions.ProjectLeider);
        var task23 = new HashSet<string>(StringComparer.Ordinal) { "30" };
        Assert.True(LegacyPayrollAutoCandidateSelector.IsAutoCandidate(candidate, PeriodStart, task23));
    }

    [Fact]
    public void ProjectLeider_WithTask23_AndAcertaMissing_IsNotAutoCandidate()
    {
        var candidate = Candidate("30", "Pat Leader", LegacyPayrollTechnicianFunctions.ProjectLeider) with
        {
            AcertaIdentityStatus = AcertaIdentityStatus.Missing,
        };
        var task23 = new HashSet<string>(StringComparer.Ordinal) { "30" };
        Assert.False(LegacyPayrollAutoCandidateSelector.IsAutoCandidate(candidate, PeriodStart, task23));
    }

    [Fact]
    public void Departure_August15_EligibleThroughSeptember_NotFromOctober()
    {
        var candidate = Candidate("40", "Former Tech", "Technieker") with
        {
            EmploymentEndDate = new DateOnly(2026, 8, 15),
        };
        Assert.True(LegacyPayrollAutoCandidateSelector.IsAutoCandidate(
            candidate, new DateOnly(2026, 9, 30), NoTask23));
        Assert.False(LegacyPayrollAutoCandidateSelector.IsAutoCandidate(
            candidate, new DateOnly(2026, 10, 1), NoTask23));
    }

    [Fact]
    public void Departure_August31_EligibleThroughSeptember_NotFromOctober()
    {
        var candidate = Candidate("41", "Former Tech EndMonth", "Technieker") with
        {
            EmploymentEndDate = new DateOnly(2026, 8, 31),
        };
        Assert.True(LegacyPayrollAutoCandidateSelector.IsAutoCandidate(
            candidate, new DateOnly(2026, 9, 30), NoTask23));
        Assert.False(LegacyPayrollAutoCandidateSelector.IsAutoCandidate(
            candidate, new DateOnly(2026, 10, 1), NoTask23));
    }

    [Fact]
    public void EndedBeyondGrace_IsNotAutoCandidate()
    {
        var candidate = Candidate("42", "Long Gone", "Technieker") with
        {
            EmploymentEndDate = new DateOnly(2026, 7, 31),
        };
        // Grace through August 31; September 1 is outside.
        Assert.False(LegacyPayrollAutoCandidateSelector.IsAutoCandidate(
            candidate, new DateOnly(2026, 9, 1), NoTask23));
        Assert.True(LegacyPayrollAutoCandidateSelector.IsAutoCandidate(
            candidate, new DateOnly(2026, 8, 1), NoTask23));
    }

    [Fact]
    public void SnapshotUniverse_IncludesExplicitConfigOutsideAutoSet()
    {
        var designer = Candidate("20", "Dana Designer", "Designer");
        var tech = Candidate("10", "Ada Technician", "Technieker");
        var configs = new[]
        {
            new PayrollEmployeeConfiguration(
                "20",
                PeriodStart,
                null,
                PayrollEligibilityStatus.Included,
                "ManualPayrollInclusion",
                null,
                PayrollEligibilityDecisionSource.Admin),
        };

        var selected = LegacyPayrollAutoCandidateSelector.SelectSnapshotCandidates(
            [designer, tech],
            PeriodStart,
            new DateOnly(2026, 8, 31),
            NoTask23,
            configs);

        Assert.Contains(selected, item => item.ResourceId == "10");
        Assert.Contains(selected, item => item.ResourceId == "20");
    }

    [Fact]
    public void SnapshotUniverse_ExplicitIncluded_OverridesAcertaMissingAutoExclusion()
    {
        var missingTech = Candidate("11", "Vendor Transport", "Technieker") with
        {
            AcertaIdentityStatus = AcertaIdentityStatus.Missing,
        };
        Assert.False(LegacyPayrollAutoCandidateSelector.IsAutoCandidate(missingTech, PeriodStart, NoTask23));

        var configs = new[]
        {
            new PayrollEmployeeConfiguration(
                "11",
                PeriodStart,
                null,
                PayrollEligibilityStatus.Included,
                "ManualPayrollInclusion",
                null,
                PayrollEligibilityDecisionSource.Admin),
        };

        var selected = LegacyPayrollAutoCandidateSelector.SelectSnapshotCandidates(
            [missingTech],
            PeriodStart,
            new DateOnly(2026, 8, 31),
            NoTask23,
            configs);

        Assert.Contains(selected, item => item.ResourceId == "11");
    }

    [Fact]
    public void SnapshotUniverse_DoesNotUseArbitraryPerformanceActivity()
    {
        var designer = Candidate("20", "Dana Designer", "Designer");
        var selected = LegacyPayrollAutoCandidateSelector.SelectSnapshotCandidates(
            [designer],
            PeriodStart,
            new DateOnly(2026, 8, 31),
            NoTask23,
            []);
        Assert.Empty(selected);
    }

    [Fact]
    public void SoortAndRestype_AreNotUsedForAutoSelection()
    {
        var candidate = Candidate("99", "Random", "Manager") with { Soort = 1, ResourceType = "52164" };
        Assert.False(LegacyPayrollAutoCandidateSelector.IsAutoCandidate(candidate, PeriodStart, NoTask23));
    }

    private static PayrollEmployeeCandidate Candidate(string id, string name, string? function) =>
        new(
            id,
            $"R{id}",
            name,
            null,
            null,
            null,
            null,
            function,
            null,
            null,
            AcertaIdentityStatus.Present);
}

public sealed class PayrollRosterEmploymentWindowTests
{
    [Theory]
    [InlineData(2026, 8, 15, 2026, 9, 30)]
    [InlineData(2026, 8, 31, 2026, 9, 30)]
    [InlineData(2026, 9, 30, 2026, 10, 31)]
    public void AutoEligibleThrough_IsEndOfMonthAfterDeparture(
        int endY, int endM, int endD,
        int throughY, int throughM, int throughD)
    {
        var through = PayrollRosterEmploymentWindow.AutoEligibleThrough(new DateOnly(endY, endM, endD));
        Assert.Equal(new DateOnly(throughY, throughM, throughD), through);
    }

    [Fact]
    public void AutoEligibleThrough_NullEmployment_HasNoEnd()
    {
        Assert.Null(PayrollRosterEmploymentWindow.AutoEligibleThrough(null));
        Assert.True(PayrollRosterEmploymentWindow.IsAutoEligibleOn(null, new DateOnly(2099, 1, 1)));
    }
}

public sealed class LegacyPayrollPerformanceEligibilityTests
{
    [Fact]
    public void NonProjectLeider_AlwaysIncluded()
    {
        Assert.True(LegacyPayrollPerformanceEligibility.IsIncluded("Technieker", 10));
        Assert.True(LegacyPayrollPerformanceEligibility.IsIncluded("Designer", 99));
        Assert.True(LegacyPayrollPerformanceEligibility.IsIncluded(null, 23));
    }

    [Fact]
    public void ProjectLeider_Task23_Included()
    {
        Assert.True(LegacyPayrollPerformanceEligibility.IsIncluded(
            LegacyPayrollTechnicianFunctions.ProjectLeider,
            LegacyPayrollPerformanceEligibility.ProjectLeiderIncludedHfdTaakId));
    }

    [Fact]
    public void ProjectLeider_OtherTasks_Excluded()
    {
        Assert.False(LegacyPayrollPerformanceEligibility.IsIncluded(
            LegacyPayrollTechnicianFunctions.ProjectLeider,
            10));
        Assert.False(LegacyPayrollPerformanceEligibility.IsIncluded(
            LegacyPayrollTechnicianFunctions.ProjectLeider,
            null));
    }
}
