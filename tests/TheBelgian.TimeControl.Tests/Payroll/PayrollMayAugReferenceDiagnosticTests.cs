using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Eligibility;
using TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;
using Xunit.Abstractions;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollMayAugReferenceDiagnosticTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(2026, 5, "Prestaties mei overzicht.csv")]
    [InlineData(2026, 6, "Prestaties juni overzicht.csv")]
    [InlineData(2026, 7, "Prestaties juli overzicht.csv")]
    [InlineData(2026, 8, "Prestaties augustus overzicht.csv")]
    public void ReferencePopulation_DoesNotSetFinalEligibility(int year, int month, string overviewFile)
    {
        var repoRoot = FindRepoRoot();
        var overviewPath = Path.Combine(
            repoRoot,
            "reference",
            "powerbi",
            $"{year}-{month:00}",
            overviewFile);
        if (!File.Exists(overviewPath))
        {
            output.WriteLine($"SKIP: {overviewPath} missing.");
            return;
        }

        var overview = PowerBiGoldenMasterReader.ReadOverview(overviewPath);
        var explicitIncluded = 0;
        var explicitExcluded = 0;
        var needsDecision = overview.Count;
        var suggestedOaOrIntern = 0;

        foreach (var row in overview)
        {
            var candidate = new PayrollEmployeeCandidate(
                row.ResourceId,
                row.ResourceId,
                row.Resource,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                AcertaIdentityStatus.Present);
            var resolution = PayrollEligibilityResolver.Resolve(
                candidate,
                new DateOnly(year, month, 1),
                new DateOnly(year, month, DateTime.DaysInMonth(year, month)),
                []);
            var suggestion = PayrollEligibilitySuggestionService.SuggestPowerBiPresence(
                candidate,
                new DateOnly(year, month, 1),
                presentInPowerBiOverview: true);

            Assert.Equal(PayrollEligibilityStatus.NeedsDecision, resolution.EligibilityStatus);
            if (suggestion.SuggestedEligibility == PayrollEligibilityStatus.Excluded
                && suggestion.SuggestedReason?.Contains("OA", StringComparison.OrdinalIgnoreCase) == true
                || suggestion.SuggestedReason?.Contains("stagiair", StringComparison.OrdinalIgnoreCase) == true)
            {
                suggestedOaOrIntern++;
            }
        }

        output.WriteLine(
            $"{year}-{month:00}: PBI={overview.Count} explicitIncluded={explicitIncluded} " +
            $"explicitExcluded={explicitExcluded} needsDecision={needsDecision} suggestedOaIntern={suggestedOaOrIntern}");
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "reference", "powerbi")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repo root not found.");
    }
}
