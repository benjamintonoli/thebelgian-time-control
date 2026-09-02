namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollShadowAdminUiTests
{
    [Fact]
    public void AdminPayrollPages_DoNotExposeNationalRegisterField()
    {
        var repoRoot = FindRepoRoot();
        var files = Directory.GetFiles(
            Path.Combine(repoRoot, "src", "TheBelgian.TimeControl.Web", "Pages", "Admin", "Payroll"),
            "*.*",
            SearchOption.AllDirectories);
        foreach (var file in files)
        {
            var content = File.ReadAllText(file);
            Assert.DoesNotContain("PASS_IDRIJKSREG", content, StringComparison.Ordinal);
            Assert.DoesNotContain("IdRijksreg", content, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "src", "TheBelgian.TimeControl.Web")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repo root not found.");
    }
}
