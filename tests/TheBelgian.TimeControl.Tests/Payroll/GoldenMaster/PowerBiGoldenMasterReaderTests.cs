using System.Globalization;
using TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;

namespace TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;

public sealed class PowerBiGoldenMasterReaderTests
{
    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Payroll", fileName);

    [Fact]
    public void SyntheticOverview_ParsesEuropeanDecimalsAndHeaders()
    {
        var rows = PowerBiGoldenMasterReader.ReadOverview(FixturePath("overview-synthetic.csv"));

        Assert.Equal(2, rows.Count);
        Assert.Equal("1001", rows[0].ResourceId);
        Assert.Equal("TECH001", rows[0].Resource);
        Assert.Equal(16.5m, rows[0].TotalHours);
        Assert.Equal("TECH002", rows[1].Resource);
        Assert.Equal(8.75m, rows[1].TotalHours);
        Assert.Equal(8.75m, rows[1].AtlHours);
    }

    [Fact]
    public void SyntheticDetail_HandlesDuplicateZiekteControleHeader()
    {
        var (headers, rows) = PowerBiGoldenMasterReader.ReadCsv(FixturePath("detail-synthetic.csv"));

        Assert.Contains("ziekte controle", headers);
        Assert.Contains("ziekte controle__1", headers);
        Assert.Equal(4, rows.Count);

        var detail = PowerBiGoldenMasterReader.ReadDetail(FixturePath("detail-synthetic.csv"));
        Assert.Equal(4, detail.Count);
        Assert.Equal(9, detail[0].HfdTaakId);
        Assert.Equal(8.50m, detail[0].AtlHours);
        Assert.Equal(new DateOnly(2026, 7, 1), detail[0].Date);
        Assert.Equal("1899-12-30 00:30:00", detail[0].PauseRaw);
        Assert.Null(detail[2].PauseRaw);
    }

    [Fact]
    public void SyntheticDetail_DailyMaxAggregationMatchesOverviewTotals()
    {
        var overview = PowerBiGoldenMasterReader.ReadOverview(FixturePath("overview-synthetic.csv"));
        var detail = PowerBiGoldenMasterReader.ReadDetail(FixturePath("detail-synthetic.csv"));

        foreach (var row in overview)
        {
            var dailyMaxSum = PowerBiGoldenMasterReader.SumDailyMaxTotalHours(detail, row.Resource);
            Assert.NotNull(row.TotalHours);
            Assert.Equal(row.TotalHours.Value, dailyMaxSum);
        }
    }

    [Fact]
    public void DeduplicateHeaders_AppendsSuffixForDuplicates()
    {
        var headers = PowerBiGoldenMasterReader.DeduplicateHeaders(
            ["A", "ziekte controle", "B", "ziekte controle"]);

        Assert.Equal(["A", "ziekte controle", "B", "ziekte controle__1"], headers);
    }

    [Theory]
    [InlineData("8.5", "8.5")]
    [InlineData("8,75", "8.75")]
    [InlineData("186.40000000001942", "186.40000000001942")]
    [InlineData("", null)]
    public void ParseDecimal_SupportsInvariantAndBelgian(string raw, string? expectedText)
    {
        var parsed = PowerBiGoldenMasterReader.ParseDecimal(raw);
        if (expectedText is null)
        {
            Assert.Null(parsed);
        }
        else
        {
            Assert.Equal(decimal.Parse(expectedText, CultureInfo.InvariantCulture), parsed);
        }
    }
}
