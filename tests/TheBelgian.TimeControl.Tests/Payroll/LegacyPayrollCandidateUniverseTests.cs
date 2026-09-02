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
    public void TechnicianFunction_IsAutoCandidate(string function)
    {
        var candidate = Candidate("10", "Ada Technician", function);
        Assert.True(LegacyPayrollAutoCandidateSelector.IsAutoCandidate(candidate, PeriodStart, NoTask23));
    }

    [Fact]
    public void Designer_WithOrdinaryPerformances_IsNotAutoCandidate()
    {
        var candidate = Candidate("20", "Dana Designer", "Designer");
        Assert.False(LegacyPayrollAutoCandidateSelector.IsAutoCandidate(candidate, PeriodStart, NoTask23));
    }

    [Fact]
    public void Technician_WithOaMarker_IsNotAutoCandidate()
    {
        var candidate = Candidate("25", "Kevin De Coster (OA)", "Technieker");
        Assert.False(LegacyPayrollAutoCandidateSelector.IsAutoCandidate(candidate, PeriodStart, NoTask23));
        Assert.True(LegacyPayrollNameMarkers.IsLegacyOaMarker(candidate.DisplayName));
    }

    [Fact]
    public void Technician_WithStagiairMarker_IsNotAutoCandidate()
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
    public void ProjectLeider_WithTask23_IsAutoCandidate()
    {
        var candidate = Candidate("30", "Pat Leader", LegacyPayrollTechnicianFunctions.ProjectLeider);
        var task23 = new HashSet<string>(StringComparer.Ordinal) { "30" };
        Assert.True(LegacyPayrollAutoCandidateSelector.IsAutoCandidate(candidate, PeriodStart, task23));
    }

    [Fact]
    public void EndedBeforePeriod_IsNotAutoCandidate()
    {
        var candidate = Candidate("40", "Former Tech", "Technieker") with
        {
            EmploymentEndDate = new DateOnly(2026, 7, 31),
        };
        Assert.False(LegacyPayrollAutoCandidateSelector.IsAutoCandidate(candidate, PeriodStart, NoTask23));
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
