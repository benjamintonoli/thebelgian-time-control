using TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollSourceReconciliationTests
{
    [Fact]
    public void ReconcileResource_ExactMatch_WhenSourceFieldsAlign()
    {
        var pbi = new PowerBiDetailRow(
            "900001",
            "TECH001",
            "1001",
            9,
            "Work",
            new DateOnly(2026, 7, 1),
            "1899-12-30 08:00:00",
            "1899-12-30 16:30:00",
            8.5m,
            "1899-12-30 00:30:00",
            8.5m,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        var plenion = new PlenionSourceRow(
            900001,
            "1001",
            new DateOnly(2026, 7, 1),
            "08:00:00",
            "16:30:00",
            8.5m,
            null,
            9,
            null,
            30m);

        var result = PayrollSourceReconciliation.ReconcileResource(
            "TECH001",
            "1001",
            [pbi],
            [plenion]);

        Assert.Equal(1, result.ExactMatches);
        Assert.Equal(0, result.Unexplained);
    }

    [Fact]
    public void ReconcileResource_ClassifiesMissingInPlenion()
    {
        var pbi = new PowerBiDetailRow(
            "900002",
            "TECH001",
            "1001",
            9,
            "Work",
            new DateOnly(2026, 7, 2),
            "1899-12-30 08:00:00",
            "1899-12-30 16:00:00",
            8m,
            null,
            8m,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        var result = PayrollSourceReconciliation.ReconcileResource(
            "TECH001",
            "1001",
            [pbi],
            []);

        Assert.Equal(1, result.MissingInPlenion);
    }
}
