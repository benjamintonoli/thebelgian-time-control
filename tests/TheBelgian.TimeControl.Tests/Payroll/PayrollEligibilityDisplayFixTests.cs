using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using TheBelgian.TimeControl.Web.Pages.Admin.Payroll;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollEligibilityDisplayFixTests
{
    private sealed class RosterPostForm
    {
        public List<PayrollRosterSelectionRow> Rows { get; set; } = [];
    }

    [Fact]
    public async Task RosterPost_CheckboxBeforeHidden_BindsCheckedCheckedUnchecked()
    {
        // Mimics Razor markup order: checkbox value=true then hidden value=false.
        // ASP.NET Core uses FirstValue → checked posts true,false → true.
        var form = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["Rows[0].ResourceId"] = "A",
            ["Rows[0].IsOnPayroll"] = new StringValues(["true", "false"]),
            ["Rows[1].ResourceId"] = "B",
            ["Rows[1].IsOnPayroll"] = new StringValues(["true", "false"]),
            ["Rows[2].ResourceId"] = "C",
            ["Rows[2].IsOnPayroll"] = new StringValues(["false"]),
        };

        var model = await BindFormAsync(form);
        Assert.Equal(3, model.Rows.Count);
        Assert.True(model.Rows[0].IsOnPayroll);
        Assert.True(model.Rows[1].IsOnPayroll);
        Assert.False(model.Rows[2].IsOnPayroll);

        var (included, excluded) = PayrollRosterSelectionSplitter.Split(model.Rows);
        Assert.Equal(["A", "B"], included);
        Assert.Equal(["C"], excluded);
    }

    [Fact]
    public async Task RosterPost_HiddenBeforeCheckbox_BindsAllFalse_Regression()
    {
        // Documents the production bug: hidden false BEFORE checkbox → FirstValue=false when checked.
        var form = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["Rows[0].ResourceId"] = "A",
            ["Rows[0].IsOnPayroll"] = new StringValues(["false", "true"]),
            ["Rows[1].ResourceId"] = "B",
            ["Rows[1].IsOnPayroll"] = new StringValues(["false", "true"]),
            ["Rows[2].ResourceId"] = "C",
            ["Rows[2].IsOnPayroll"] = new StringValues(["false"]),
        };

        var model = await BindFormAsync(form);
        Assert.All(model.Rows, row => Assert.False(row.IsOnPayroll));
    }

    [Fact]
    public void DisplayFormatting_Hours_TwoDecimalsBelgian()
    {
        Assert.Equal("190,62", PayrollDisplayFormatting.Hours(190.61666666666667m));
        Assert.Equal("26,62", PayrollDisplayFormatting.Hours(26.61666666666669m));
        Assert.Equal("—", PayrollDisplayFormatting.Hours((decimal?)null));
    }

    [Fact]
    public void DisplayFormatting_Euro_TwoDecimalsBelgian()
    {
        Assert.Equal("€ 134,92", PayrollDisplayFormatting.Euro(134.92464m));
        Assert.Equal("€ 229,92", PayrollDisplayFormatting.Euro(229.92464m));
    }

    [Fact]
    public void DisplayFormatting_DoesNotMutateSourceDecimal()
    {
        var raw = 190.61666666666667m;
        _ = PayrollDisplayFormatting.Hours(raw);
        Assert.Equal(190.61666666666667m, raw);
    }

    private static async Task<RosterPostForm> BindFormAsync(Dictionary<string, StringValues> form)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMvcCore();
        await using var provider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext { RequestServices = provider };
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Request.Form = new FormCollection(form);

        var metadataProvider = provider.GetRequiredService<IModelMetadataProvider>();
        var binderFactory = provider.GetRequiredService<IModelBinderFactory>();
        var modelMetadata = metadataProvider.GetMetadataForType(typeof(RosterPostForm));
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var bindingContext = DefaultModelBindingContext.CreateBindingContext(
            actionContext,
            new FormValueProvider(BindingSource.Form, httpContext.Request.Form, CultureInfo.InvariantCulture),
            modelMetadata,
            bindingInfo: null,
            modelName: string.Empty);

        var binder = binderFactory.CreateBinder(new ModelBinderFactoryContext
        {
            Metadata = modelMetadata,
            BindingInfo = new BindingInfo { BindingSource = BindingSource.Form },
            CacheToken = modelMetadata,
        });
        await binder.BindModelAsync(bindingContext);
        Assert.True(bindingContext.Result.IsModelSet);
        return Assert.IsType<RosterPostForm>(bindingContext.Result.Model);
    }
}
