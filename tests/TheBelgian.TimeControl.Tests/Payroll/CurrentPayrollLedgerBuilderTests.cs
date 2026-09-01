using System.Globalization;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Sources;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class CurrentPayrollLedgerBuilderTests
{
    private static readonly DateOnly Day = new(2026, 7, 6);

    [Fact]
    public void Build_WorkOnly_PreservesPerformanceRows()
    {
        var performance = Performance("1001", hfdTaak: 1, atl: 8m);
        var ledger = CurrentPayrollLedgerBuilder.Build([performance], []);
        Assert.Single(ledger.Performances);
        Assert.Empty(ledger.SyntheticAbsences);
    }

    [Fact]
    public void Build_AbsenceOnly_PreservesSyntheticRows()
    {
        var absence = Synthetic("KL100_20260706_19", hfdTaak: 10, hours: 8m);
        var ledger = CurrentPayrollLedgerBuilder.Build([], [absence]);
        Assert.Empty(ledger.Performances);
        Assert.Single(ledger.SyntheticAbsences);
    }

    [Fact]
    public void Build_WorkAndAbsence_KeepsBothPopulations()
    {
        var performance = Performance("1001", hfdTaak: 1, atl: 6m);
        var absence = Synthetic("KL100_20260706_19", hfdTaak: 10, hours: 8m);
        var ledger = CurrentPayrollLedgerBuilder.Build([performance], [absence]);
        var dailyInputs = CurrentPayrollLegacyAdapter.ToDailyInputs(ledger);
        Assert.Equal(2, dailyInputs.Count);
        Assert.Contains(dailyInputs, row => row.SourceEntryKey == "1001");
        Assert.Contains(dailyInputs, row => row.SourceEntryKey == "KL100_20260706_19");
    }

    [Fact]
    public void Build_TravelWorkAbsence_AllRetainedForDailyPipeline()
    {
        var work = Performance("2001", hfdTaak: 1, atl: 7m);
        var travel = Performance("2002", hfdTaak: 2, atl: 1m);
        var absence = Synthetic("KL200_20260706_19", hfdTaak: 18, hours: 8m);
        var ledger = CurrentPayrollLedgerBuilder.Build([work, travel], [absence]);
        var dailyInputs = CurrentPayrollLegacyAdapter.ToDailyInputs(ledger);
        Assert.Equal(3, dailyInputs.Count);
    }

    private static NormalizedPerformanceEntry Performance(string sourceKey, int hfdTaak, decimal atl) =>
        new(
            SourceEntryId: long.Parse(sourceKey, CultureInfo.InvariantCulture),
            SourceEntryKey: sourceKey,
            ResourceId: "19",
            Date: Day,
            Start: new DateTimeOffset(Day.ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero),
            End: new DateTimeOffset(Day.ToDateTime(new TimeOnly(16, 0)), TimeSpan.Zero),
            AtlHoursRaw: atl,
            AtlMinutesExact: atl * 60m,
            GrossClockDuration: TimeSpan.FromHours(8),
            Pause: new PauseNormalizationResult(PauseParseStatus.Missing, null, PauseSourceKind.Unspecified, null),
            Km: null,
            HfdTaakId: hfdTaak,
            ProjectId: null,
            ProjectNumber: null,
            BonNr: null,
            Description: null,
            Memo: null,
            Postcode: null,
            SortKey: long.Parse(sourceKey, CultureInfo.InvariantCulture));

    private static CalendarSyntheticEntry Synthetic(string stableKey, int hfdTaak, decimal hours) =>
        new(
            100,
            stableKey,
            "19",
            Day,
            5,
            hfdTaak,
            hours,
            true,
            false,
            "IDRESOURCE",
            null);
}
