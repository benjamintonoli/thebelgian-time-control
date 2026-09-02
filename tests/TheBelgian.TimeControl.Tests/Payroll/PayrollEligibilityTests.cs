using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Interfaces;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Eligibility;
using TheBelgian.TimeControl.Infrastructure.Payroll.Shadow;
using TheBelgian.TimeControl.Infrastructure.Persistence;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollEligibilityTests
{
    [Fact]
    public void NoConfiguration_ReturnsNeedsDecision()
    {
        var candidate = SampleCandidate("1", "Ada Admin");
        var resolution = PayrollEligibilityResolver.Resolve(
            candidate,
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 31),
            []);

        Assert.Equal(PayrollEligibilityStatus.NeedsDecision, resolution.EligibilityStatus);
        Assert.False(resolution.HasExplicitConfiguration);
    }

    [Fact]
    public void ExplicitIncludedActiveForPeriod_ReturnsIncluded()
    {
        var candidate = SampleCandidate("1", "Ada Admin");
        var configs = new[]
        {
            new PayrollEmployeeConfiguration(
                "1",
                new DateOnly(2026, 1, 1),
                null,
                PayrollEligibilityStatus.Included,
                "AdminConfirmed",
                null,
                PayrollEligibilityDecisionSource.Admin),
        };

        var resolution = PayrollEligibilityResolver.Resolve(
            candidate,
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 31),
            configs);

        Assert.Equal(PayrollEligibilityStatus.Included, resolution.EligibilityStatus);
        Assert.True(resolution.HasExplicitConfiguration);
    }

    [Fact]
    public void FutureConfiguration_IsNotAppliedEarly()
    {
        var candidate = SampleCandidate("1", "Ada Admin");
        var configs = new[]
        {
            new PayrollEmployeeConfiguration(
                "1",
                new DateOnly(2026, 9, 1),
                null,
                PayrollEligibilityStatus.Included,
                "Future",
                null,
                PayrollEligibilityDecisionSource.Admin),
        };

        var resolution = PayrollEligibilityResolver.Resolve(
            candidate,
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 31),
            configs);

        Assert.Equal(PayrollEligibilityStatus.NeedsDecision, resolution.EligibilityStatus);
    }

    [Fact]
    public void ExpiredConfiguration_IsNotAppliedLate()
    {
        var candidate = SampleCandidate("1", "Ada Admin");
        var configs = new[]
        {
            new PayrollEmployeeConfiguration(
                "1",
                new DateOnly(2026, 1, 1),
                new DateOnly(2026, 5, 31),
                PayrollEligibilityStatus.Included,
                "Expired",
                null,
                PayrollEligibilityDecisionSource.Admin),
        };

        var resolution = PayrollEligibilityResolver.Resolve(
            candidate,
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 31),
            configs);

        Assert.Equal(PayrollEligibilityStatus.NeedsDecision, resolution.EligibilityStatus);
    }

    [Fact]
    public void OverlappingConfigurations_AreRejected()
    {
        var existing = new PayrollEmployeeConfiguration(
            "1",
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 12, 31),
            PayrollEligibilityStatus.Included,
            "Existing",
            null,
            PayrollEligibilityDecisionSource.Admin);
        var candidate = new PayrollEmployeeConfiguration(
            "1",
            new DateOnly(2026, 6, 1),
            null,
            PayrollEligibilityStatus.Excluded,
            "Overlap",
            null,
            PayrollEligibilityDecisionSource.Admin);

        Assert.Throws<InvalidOperationException>(() =>
            PayrollEligibilityResolver.EnsureNoOverlap([existing], candidate));
    }

    [Fact]
    public void PartTimeCandidate_DoesNotAutoExclude()
    {
        var candidate = SampleCandidate("19", "Ivo Van Breedam");
        var resolution = PayrollEligibilityResolver.Resolve(
            candidate,
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 31),
            []);

        Assert.Equal(PayrollEligibilityStatus.NeedsDecision, resolution.EligibilityStatus);
        Assert.Null(resolution.SuggestedEligibility);
    }

    [Fact]
    public void EndedResource_SuggestsExcluded_ButFinalDecisionRemainsNeedsDecision()
    {
        var candidate = SampleCandidate("99", "Former Employee") with
        {
            EmploymentEndDate = new DateOnly(2026, 5, 31),
        };

        var resolution = PayrollEligibilityResolver.Resolve(
            candidate,
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 31),
            []);

        Assert.Equal(PayrollEligibilityStatus.NeedsDecision, resolution.EligibilityStatus);
        Assert.Equal(PayrollEligibilityStatus.Excluded, resolution.SuggestedEligibility);
    }

    [Fact]
    public void NameMarkerSuggestion_DoesNotBecomeFinalDecision()
    {
        var candidate = SampleCandidate("77", "Vendor (OA)");
        var resolution = PayrollEligibilityResolver.Resolve(
            candidate,
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 31),
            []);

        Assert.Equal(PayrollEligibilityStatus.NeedsDecision, resolution.EligibilityStatus);
        Assert.Equal(PayrollEligibilityStatus.Excluded, resolution.SuggestedEligibility);
    }

    [Fact]
    public void PowerBiPresence_DoesNotBecomeFinalDecision()
    {
        var candidate = SampleCandidate("1", "Ada Admin");
        var suggestion = PayrollEligibilitySuggestionService.SuggestPowerBiPresence(
            candidate,
            new DateOnly(2026, 7, 1),
            presentInPowerBiOverview: true);
        var resolution = PayrollEligibilityResolver.Resolve(
            candidate,
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 31),
            []);

        Assert.Equal(PayrollEligibilityStatus.NeedsDecision, resolution.EligibilityStatus);
        Assert.Contains("Power BI", suggestion.SuggestedReason, StringComparison.OrdinalIgnoreCase);
    }

    private static PayrollEmployeeCandidate SampleCandidate(string id, string name) =>
        new(
            id,
            $"R{id}",
            name,
            $"{id}@example.test",
            "1",
            null,
            null,
            null,
            1,
            null,
            AcertaIdentityStatus.Present);
}
