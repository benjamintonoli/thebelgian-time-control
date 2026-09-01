using System.Reflection;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;
using TheBelgian.TimeControl.Infrastructure.Payroll.Sources;
using TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class CurrentSourceSeparationTests
{
    [Fact]
    public void CurrentPayrollLegacyAdapter_DoesNotReferenceHistoricalKmResolver()
    {
        var source = typeof(CurrentPayrollLegacyAdapter).Assembly.GetName().Name;
        var references = typeof(CurrentPayrollLegacyAdapter).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(typeof(HistoricalKmResolver).Assembly.GetName().Name, references);
        Assert.DoesNotContain(typeof(HistoricalLegacyParityAdapter).Assembly.GetName().Name, references);
        Assert.Equal("TheBelgian.TimeControl.Infrastructure", source);
    }

    [Fact]
    public void LegacyMonthlyHoursPipeline_DoesNotReferenceHistoricalTestAssembly()
    {
        var references = typeof(LegacyMonthlyHoursPipeline).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(typeof(HistoricalKmResolver).Assembly.GetName().Name, references);
    }

    [Fact]
    public void Infrastructure_DoesNotContainLegacyKmAllowanceCalculator()
    {
        Assert.Null(typeof(LegacyMonthlyHoursPipeline).Assembly.GetType(
            "TheBelgian.TimeControl.Infrastructure.Payroll.Legacy.LegacyKmAllowanceCalculator",
            throwOnError: false));
    }

    [Fact]
    public void PlenionPostcodeResolver_DoesNotReferenceHistoricalTestAssembly()
    {
        var references = typeof(PlenionPostcodeResolver).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(typeof(HistoricalKmResolver).Assembly.GetName().Name, references);
    }

    [Fact]
    public void CurrentPayrollLegacyAdapter_HasNoHistoricalOverrideParameters()
    {
        var methods = typeof(CurrentPayrollLegacyAdapter)
            .GetMethods(BindingFlags.Public | BindingFlags.Static);
        Assert.All(methods, method =>
        {
            Assert.DoesNotContain(
                method.GetParameters(),
                parameter => parameter.ParameterType.Name.Contains("LegacyDailyComponentOverrides", StringComparison.Ordinal));
        });
    }
}
