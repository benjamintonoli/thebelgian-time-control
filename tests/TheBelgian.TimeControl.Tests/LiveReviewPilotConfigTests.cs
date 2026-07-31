using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.AdminReview;
using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Tests;

public sealed class LiveReviewPilotConfigTests
{
    [Fact]
    public void Offline_IsDefaultMode()
    {
        var options = new ReviewDataOptions();
        Assert.Equal(ReviewDataModes.Offline, options.Mode);
        Assert.True(options.IsOffline);
        Assert.False(options.IsLivePilot);
        Assert.False(LiveReviewCaseProvider.IsEnabledByDefault);
        options.Validate();
    }

    [Fact]
    public void LivePilot_RequiresExplicitConfiguration()
    {
        var missingTech = new ReviewDataOptions
        {
            Mode = ReviewDataModes.LivePilot,
            DateFrom = new DateOnly(2026, 7, 27),
            DateTo = new DateOnly(2026, 7, 31),
        };
        Assert.Throws<InvalidOperationException>(() => missingTech.Validate());

        var missingDates = new ReviewDataOptions
        {
            Mode = ReviewDataModes.LivePilot,
            TechnicianResourceId = "14",
        };
        Assert.Throws<InvalidOperationException>(() => missingDates.Validate());
    }

    [Fact]
    public void LivePilot_RejectsPeriodLongerThanFiveCalendarDays()
    {
        var options = new ReviewDataOptions
        {
            Mode = ReviewDataModes.LivePilot,
            TechnicianResourceId = "14",
            DateFrom = new DateOnly(2026, 7, 27),
            DateTo = new DateOnly(2026, 8, 2),
            MaxDays = 5,
        };
        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("kalenderdagen", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LivePilot_RejectsWritebackOrAutomaticCorrections()
    {
        var writeback = new ReviewDataOptions
        {
            Mode = ReviewDataModes.LivePilot,
            TechnicianResourceId = "14",
            DateFrom = new DateOnly(2026, 7, 27),
            DateTo = new DateOnly(2026, 7, 31),
            AllowWriteback = true,
        };
        Assert.Throws<InvalidOperationException>(() => writeback.Validate());

        var auto = new ReviewDataOptions
        {
            Mode = ReviewDataModes.LivePilot,
            TechnicianResourceId = "14",
            DateFrom = new DateOnly(2026, 7, 27),
            DateTo = new DateOnly(2026, 7, 31),
            AllowAutomaticCorrections = true,
        };
        Assert.Throws<InvalidOperationException>(() => auto.Validate());
    }

    [Fact]
    public void LivePilot_AcceptsFiveCalendarDayWindow()
    {
        var options = new ReviewDataOptions
        {
            Mode = ReviewDataModes.LivePilot,
            TechnicianResourceId = "14",
            PowerfleetDriverId = "19725",
            DateFrom = new DateOnly(2026, 7, 27),
            DateTo = new DateOnly(2026, 7, 31),
            MaxDays = 5,
        };
        options.Validate();
        Assert.True(options.IsLivePilot);
    }

    [Fact]
    public void LiveProvider_DoesNotLoadHoldout_AndUsesFrozenMatcherConfig()
    {
        var live = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "TheBelgian.TimeControl.Infrastructure", "AdminReview", "LiveReviewCaseProvider.cs"));
        Assert.DoesNotContain("location-matching-holdout.json", live, StringComparison.Ordinal);
        Assert.DoesNotContain("LockedHoldoutEvaluation", live, StringComparison.Ordinal);
        Assert.Contains("FrozenMatcherVerificationService.ComputeConfigurationHash", live, StringComparison.Ordinal);
        Assert.Contains("AdaptiveLocationMatchingOptions", live, StringComparison.Ordinal);
        Assert.Contains(LiveReviewCaseProvider.ReadOnlyBanner, live, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyPilotService", live, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteToPlenion", live, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ", live, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReviewCaseFactory_UsesSameContractAsOffline()
    {
        var factory = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "TheBelgian.TimeControl.Infrastructure", "AdminReview", "ReviewCaseFactory.cs"));
        Assert.Contains("SourceEvidence", factory, StringComparison.Ordinal);
        Assert.Contains("MatcherAssessment", factory, StringComparison.Ordinal);
        Assert.Contains("AdminDecision", factory, StringComparison.Ordinal);
        Assert.Contains("AdminReviewDecisionRules.InitialReviewStatus", factory, StringComparison.Ordinal);
        Assert.Contains("OfflineHybridPredictor.Predict", factory, StringComparison.Ordinal);
    }

    [Fact]
    public void Di_RegistersProviderBehindReviewDataMode()
    {
        var di = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "TheBelgian.TimeControl.Infrastructure", "DependencyInjection.cs"));
        Assert.Contains("reviewData.IsLivePilot", di, StringComparison.Ordinal);
        Assert.Contains("LiveReviewCaseProvider", di, StringComparison.Ordinal);
        Assert.Contains("OfflineReviewCaseProvider", di, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAI", di, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TheBelgian.TimeControl.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repo root not found.");
    }
}
