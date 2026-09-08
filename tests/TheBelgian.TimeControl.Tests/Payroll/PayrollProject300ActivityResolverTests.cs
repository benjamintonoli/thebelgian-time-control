using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Review;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollProject300ActivityResolverTests
{
    [Theory]
    [InlineData(23, "WWD", "Werkuren Wachtdienst", "WaitingTime", true)]
    [InlineData(9, "WUO", "Werkuren Onderhoudstechnicus", "CustomerWork", true)]
    [InlineData(30, "WUST", "Werkuren Service Interventie", "CustomerWork", true)]
    [InlineData(5, "verpl", "Verplaatsingen", "Travel", false)]
    [InlineData(14, "WUPT", "Werkuren Projecttechnicus", null, false)]
    public void Resolve_UsesVerifiedOrClassifierWithoutInventingIdMap(
        int hfd,
        string code,
        string description,
        string? expectedActivity,
        bool supported)
    {
        var perf = new NormalizedPerformanceEntry(
            SourceEntryId: 1,
            SourceEntryKey: "1",
            ResourceId: "10",
            Date: new DateOnly(2026, 8, 31),
            Start: new DateTimeOffset(2026, 8, 31, 8, 0, 0, TimeSpan.FromHours(2)),
            End: new DateTimeOffset(2026, 8, 31, 8, 20, 0, TimeSpan.FromHours(2)),
            AtlHoursRaw: 0.33m,
            AtlMinutesExact: 20m,
            GrossClockDuration: TimeSpan.FromMinutes(20),
            Pause: new PauseNormalizationResult(PauseParseStatus.Missing, null, PauseSourceKind.Unspecified, null),
            Km: null,
            HfdTaakId: hfd,
            ProjectId: "49432",
            ProjectNumber: 300,
            BonNr: "1",
            Description: null,
            Memo: null,
            Postcode: null,
            SortKey: 1);

        var resolved = PayrollProject300ActivityResolver.Resolve(
            perf,
            new HfdTaakDefinition(hfd, code, description));

        Assert.Equal(supported, resolved.Supported);
        if (expectedActivity is null)
        {
            Assert.True(resolved.ActivityType is null or "Unknown");
        }
        else
        {
            Assert.Equal(expectedActivity, resolved.ActivityType);
        }
    }
}
