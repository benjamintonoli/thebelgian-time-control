using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Actions;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Review;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Web.Pages.Admin.Payroll;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollDeleteBrowserFlowTests
{
    [Fact]
    public void ActionConfirmMarkup_PostsActionIdWithExecuteHandler()
    {
        var markup = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "TheBelgian.TimeControl.Web",
            "Pages",
            "Admin",
            "Payroll",
            "ActionConfirm.cshtml"));

        Assert.Contains("method=\"post\"", markup, StringComparison.Ordinal);
        Assert.Contains("asp-route-ActionId=\"@Model.ActionId\"", markup, StringComparison.Ordinal);
        Assert.Contains("asp-for=\"ActionId\"", markup, StringComparison.Ordinal);
        Assert.Contains("asp-page-handler=\"Execute\"", markup, StringComparison.Ordinal);
        Assert.Contains("Definitief verwijderen", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<form method=\"post\" class=\"vstack", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkbenchMarkup_HoursWrongUsesInlineActionChoiceWithoutPromptGate()
    {
        var workbench = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "TheBelgian.TimeControl.Web",
            "Pages",
            "Admin",
            "Payroll",
            "Workbench.cshtml"));
        var detail = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "TheBelgian.TimeControl.Web",
            "Pages",
            "Admin",
            "Payroll",
            "_Project300Detail.cshtml"));

        Assert.Contains("Wat is fout?", detail, StringComparison.Ordinal);
        Assert.Contains("Tijd aanpassen", detail, StringComparison.Ordinal);
        Assert.Contains("Prestatie verwijderen", detail, StringComparison.Ordinal);
        Assert.Contains("wb-show-adjust", detail, StringComparison.Ordinal);
        Assert.Contains("wb-show-delete", detail, StringComparison.Ordinal);
        Assert.Contains("wb-propose-delete", detail, StringComparison.Ordinal);
        Assert.Contains("delete-perf-radio", detail, StringComparison.Ordinal);
        Assert.Contains("Admin-toelichting (TimeControl — schrijft niet terug naar BON)", detail, StringComparison.Ordinal);

        Assert.Contains("P300HoursWrong", workbench, StringComparison.Ordinal);
        Assert.Contains("correction-panel", workbench, StringComparison.Ordinal);
        Assert.Contains("proposeDelete", workbench, StringComparison.Ordinal);
        Assert.Contains("ActionId=", workbench, StringComparison.Ordinal);
        // Prompt may remain for other decisions (Onzeker), but hours-wrong opens panel first.
        Assert.Contains("openCorrection", workbench, StringComparison.Ordinal);
    }

    [Fact]
    public void Project300_HoursWrong_DoesNotRequireComment()
    {
        var choice = Assert.Single(
            PayrollGuidedDecisions.ChoicesFor(PayrollReviewCategory.Project300)
                .Where(item => item.DecisionCode == PayrollGuidedDecisionCodes.P300HoursWrong));
        Assert.False(choice.RequiresComment);
        Assert.Contains(
            PayrollGuidedDecisions.ChoicesFor(PayrollReviewCategory.Project300),
            item => item.DecisionCode == PayrollGuidedDecisionCodes.P300WorkValid);
        Assert.Contains(
            PayrollGuidedDecisions.ChoicesFor(PayrollReviewCategory.Project300),
            item => item.DecisionCode == PayrollGuidedDecisionCodes.P300PlanningMissing);
        Assert.Contains(
            PayrollGuidedDecisions.ChoicesFor(PayrollReviewCategory.Project300),
            item => item.DecisionCode == PayrollGuidedDecisionCodes.P300Uncertain && item.RequiresComment);
    }

    [Fact]
    public async Task ActionConfirm_Get_WithActionId_ReturnsPage()
    {
        var actionId = Guid.Parse("f420a912-cdf6-4298-80ae-ba8b0ea6ea41");
        var actions = new FakeActions
        {
            Confirmation = SampleConfirmation(actionId),
        };
        var page = CreatePage(actions);
        page.ActionId = actionId;

        var result = await page.OnGetAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(page.View);
        Assert.Equal(0, actions.ExecuteCalls);
    }

    [Fact]
    public async Task ActionConfirm_Get_WithoutActionId_ReturnsNotFound()
    {
        var page = CreatePage(new FakeActions());
        page.ActionId = Guid.Empty;

        var result = await page.OnGetAsync(default);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task ActionConfirm_PostExecute_WithoutActionId_DoesNotMutate()
    {
        var actions = new FakeActions();
        var page = CreatePage(actions);
        page.ActionId = Guid.Empty;
        page.Comment = "reden";

        var result = await page.OnPostExecuteAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.Equal(0, actions.ExecuteCalls);
        Assert.Contains("Actie niet gevonden", page.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActionConfirm_PostExecute_MissingReason_DoesNotMutate()
    {
        var actionId = Guid.NewGuid();
        var actions = new FakeActions
        {
            Confirmation = SampleConfirmation(actionId),
        };
        var page = CreatePage(actions);
        page.ActionId = actionId;
        page.Comment = "   ";

        var result = await page.OnPostExecuteAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.Equal(0, actions.ExecuteCalls);
        Assert.Contains("Reden is verplicht", page.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActionConfirm_PostExecute_Applied_Project200_RedirectsToProject200Workbench()
    {
        var actionId = Guid.NewGuid();
        var confirmation = SampleConfirmation(actionId) with
        {
            Evidence = SampleConfirmation(actionId).Evidence with
            {
                FindingKey = "p200-delete:100:20260821:1",
                FindingType = PayrollFindingType.Project200WithoutPlanning,
            },
            DeleteProposal = SampleConfirmation(actionId).DeleteProposal! with
            {
                ProjectLabel = "200",
            },
        };
        var actions = new FakeActions
        {
            Confirmation = confirmation,
            ExecuteResult = new PayrollActionExecutionResult(
                actionId,
                PayrollProposedActionStatus.Applied,
                "ok",
                null,
                1),
        };
        var workbench200 = new FakeWorkbench200 { NextFocus = "admin:Project200:10:20260828:p:200" };
        var page = CreatePage(actions, project200: workbench200);
        page.ActionId = actionId;
        page.Comment = "reden";

        var result = await page.OnPostExecuteAsync(default);
        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("./Project200Workbench", redirect.PageName);
        Assert.Equal(workbench200.NextFocus, redirect.RouteValues?["Focus"]);
        Assert.True(workbench200.InvalidateCalled);
    }

    [Fact]
    public async Task ActionConfirm_PostExecute_Applied_RedirectsToWorkbenchWithNextFocus()
    {
        var actionId = Guid.NewGuid();
        var actions = new FakeActions
        {
            Confirmation = SampleConfirmation(actionId),
            ExecuteResult = new PayrollActionExecutionResult(
                actionId,
                PayrollProposedActionStatus.Applied,
                "ok",
                "pws-ref",
                283272),
        };
        var workbench = new FakeWorkbench
        {
            NextFocus = "admin:Project300:999:20260821:p:49432",
        };
        var page = CreatePage(actions, workbench);
        page.ActionId = actionId;
        page.Comment = "Project 300-prestatie foutief aangemaakt.";

        var result = await page.OnPostExecuteAsync(default);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("./Workbench", redirect.PageName);
        Assert.Equal(1, actions.ExecuteCalls);
        Assert.Equal(actionId, actions.LastExecutedActionId);
        Assert.Equal("Project 300-prestatie foutief aangemaakt.", actions.LastComment);
        Assert.Equal("benjamin.tonoli@thebelgian.be", actions.LastActor);
        Assert.Equal(workbench.NextFocus, redirect.RouteValues?["Focus"]);
        Assert.Null(redirect.RouteValues?["Search"]);
        Assert.Equal(PayrollReviewQueueScope.Open, redirect.RouteValues?["Scope"]);
        Assert.True(workbench.InvalidateCalled);
        Assert.Contains("verwijderd uit Plenion", page.TempData["FlashSuccess"]?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActionConfirm_PostExecute_Stale_ShowsSafeError()
    {
        var actionId = Guid.NewGuid();
        var actions = new FakeActions
        {
            Confirmation = SampleConfirmation(actionId),
            ExecuteResult = new PayrollActionExecutionResult(
                actionId,
                PayrollProposedActionStatus.Stale,
                "Prestatie wijkt af van snapshot",
                null,
                null),
        };
        var page = CreatePage(actions);
        page.ActionId = actionId;
        page.Comment = "reden";

        var result = await page.OnPostExecuteAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.Equal(1, actions.ExecuteCalls);
        Assert.Contains("INTUSSEN GEWIJZIGD", page.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActionConfirm_PostExecute_DuplicateSubmit_DoesNotCallTwiceInOneRequest()
    {
        var actionId = Guid.NewGuid();
        var actions = new FakeActions
        {
            Confirmation = SampleConfirmation(actionId),
            ExecuteResult = new PayrollActionExecutionResult(
                actionId,
                PayrollProposedActionStatus.Applied,
                "ok",
                null,
                283272),
        };
        var page = CreatePage(actions);
        page.ActionId = actionId;
        page.Comment = "reden";

        await page.OnPostExecuteAsync(default);
        Assert.Equal(1, actions.ExecuteCalls);
    }

    private static ActionConfirmModel CreatePage(
        FakeActions actions,
        FakeWorkbench? workbench = null,
        FakeWorkbench200? project200 = null)
    {
        var http = new DefaultHttpContext();
        var temp = new TempDataDictionary(http, new FakeTempDataProvider());
        var page = new ActionConfirmModel(
            actions,
            workbench ?? new FakeWorkbench(),
            project200 ?? new FakeWorkbench200(),
            new FakeWorkbench100(),
            new FakeWorkbenchStandby(),
            new FakeIntelligenceWorkbench(),
            new FakeUser(),
            Options.Create(new PayrollShadowOptions { Enabled = true, AdminUiEnabled = true }),
            Options.Create(new PayrollActionsOptions
            {
                Enabled = true,
                ExecutionEnabled = true,
                DeletePerformanceEnabled = true,
            }),
            Options.Create(new AdminReviewWorkflowOptions
            {
                DefaultReviewer = "benjamin.tonoli@thebelgian.be",
            }),
            NullLogger<ActionConfirmModel>.Instance)
        {
            PageContext = new PageContext
            {
                HttpContext = http,
            },
            TempData = temp,
        };
        return page;
    }

    private static PayrollActionConfirmationView SampleConfirmation(Guid actionId)
    {
        var start = new DateTimeOffset(2026, 8, 20, 8, 40, 0, TimeSpan.FromHours(2));
        var end = new DateTimeOffset(2026, 8, 20, 8, 50, 0, TimeSpan.FromHours(2));
        return new PayrollActionConfirmationView(
            actionId,
            1,
            2026,
            8,
            "388",
            "Ayrton Buyle",
            PayrollProposedActionType.DeleteExistingPerformance,
            PayrollProposedActionStatus.ReadyForApproval,
            null,
            new PayrollActionEvidenceSnapshot(
                "Project300WithoutPlanning:388:20260820:283272",
                PayrollFindingType.Project300WithoutPlanning,
                PayrollFindingSeverity.Review,
                null,
                "evidence",
                "title",
                "desc",
                [283272],
                start,
                end,
                0.1667m,
                "49432",
                "26601886",
                ["Project300WithoutPlanning:388:20260820:283272"],
                [404]),
            null,
            null,
            "Project 300-prestatie foutief aangemaakt.",
            true,
            true,
            null,
            new PayrollActionDeleteProposal(
                283272,
                new DateOnly(2026, 8, 20),
                start,
                end,
                0.1667m,
                "388",
                "49432",
                "26601886",
                14,
                "Werkuren Projecttechnicus",
                "ophalen badgelezer",
                null,
                null,
                "300"));
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

    [Fact]
    public void ActionConfirmMarkup_HidesDiagnosticEvidenceForDelete()
    {
        var markup = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "TheBelgian.TimeControl.Web",
            "Pages",
            "Admin",
            "Payroll",
            "ActionConfirm.cshtml"));
        Assert.Contains("Huidige prestatie", markup, StringComparison.Ordinal);
        Assert.Contains("Omschrijving:", markup, StringComparison.Ordinal);
        Assert.Contains("PerformanceId", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Workbench delete-voorstel", markup, StringComparison.Ordinal);
        // Create path may surface Evidence under Technische details; delete path must not lead with raw evidence.
        Assert.Contains("isDelete ? \"Huidige prestatie\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkbenchMarkup_EmptyQueueDoesNotSpinForeverOnContextLaden()
    {
        var markup = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "TheBelgian.TimeControl.Web",
            "Pages",
            "Admin",
            "Payroll",
            "Workbench.cshtml"));
        Assert.Contains("Geen open case", markup, StringComparison.Ordinal);
        Assert.Contains("if (currentKey) loadDetail(currentKey);", markup, StringComparison.Ordinal);
    }

    private sealed class FakeWorkbench : IPayrollProject300WorkbenchService
    {
        public string? NextFocus { get; set; }
        public bool InvalidateCalled { get; private set; }

        public Task<PayrollProject300WorkbenchPage> GetWorkbenchAsync(
            int year, int month, PayrollReviewQueueFilter filter, string? selectedAdminCaseKey, CancellationToken cancellationToken) =>
            GetShellAsync(year, month, filter, selectedAdminCaseKey, cancellationToken);

        public Task<PayrollProject300WorkbenchPage> GetShellAsync(
            int year, int month, PayrollReviewQueueFilter filter, string? selectedAdminCaseKey, CancellationToken cancellationToken)
        {
            var emptyCategories = new Dictionary<PayrollReviewCategory, int>();
            var cases = string.IsNullOrWhiteSpace(NextFocus)
                ? Array.Empty<PayrollAdminCase>()
                : new[]
                {
                    new PayrollAdminCase(
                        NextFocus!,
                        PayrollReviewCategory.Project300,
                        "999",
                        "Other",
                        new DateOnly(2026, 8, 21),
                        PayrollFindingSeverity.Review,
                        PayrollFindingStatus.Open,
                        "q",
                        "issue",
                        "fact",
                        BonNr: null,
                        ProjectId: "49432",
                        BookedSummary: null,
                        PlannedSummary: null,
                        DifferenceSummary: null,
                        EvidenceSummary: null,
                        HybridScenarioNote: null,
                        FriendlyState: null,
                        RuleHint: null,
                        ActionabilityHint: "review",
                        UnderlyingReviewCaseCount: 1,
                        UnderlyingPerformanceCount: 1,
                        TotalBookedHours: 0.5m,
                        UnderlyingCases: [],
                        FindingIds: [],
                        FindingKeys: [],
                        DecisionCode: null,
                        DecisionLabel: null,
                        ReviewComment: null,
                        ReviewedAtUtc: null,
                        ReviewedBy: null,
                        AllowsBulkDisposition: false,
                        Choices: [],
                        Performances: []),
                };
            return Task.FromResult(new PayrollProject300WorkbenchPage(
                year,
                month,
                new PayrollAdminQueueSummary(0, 0, 0, 0, 0, 0, 0, emptyCategories, emptyCategories, emptyCategories, emptyCategories, emptyCategories),
                cases,
                NextFocus,
                null,
                new PayrollProject300WorkbenchMetrics(0, 0, 0, GpsDeferred: true)));
        }

        public Task<PayrollProject300WorkbenchPage> GetCoreDetailAsync(
            int year, int month, PayrollReviewQueueFilter filter, string? selectedAdminCaseKey, CancellationToken cancellationToken) =>
            GetShellAsync(year, month, filter, selectedAdminCaseKey, cancellationToken);

        public Task<PayrollProject300GpsLoadResult> GetGpsContextAsync(
            int year, int month, string adminCaseKey, bool prefetchNext = true, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PayrollProject300ProposeCorrectionResult> ProposeTimeCorrectionAsync(
            int year, int month, string adminCaseKey, long performanceId, TimeOnly newStart, TimeOnly newEnd, string reason, string actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PayrollProject300ProposeCorrectionResult> ProposeDeletePerformanceAsync(
            int year, int month, string adminCaseKey, long performanceId, string reason, string actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public PayrollProject300GpsCacheHint GetGpsCacheHint(string resourceId, DateOnly workDate) =>
            PayrollProject300GpsCacheHint.Unknown;

        public void InvalidateQueueCache(int year, int month) => InvalidateCalled = true;
    }

    private sealed class FakeWorkbench200 : IPayrollProject200WorkbenchService
    {
        public string? NextFocus { get; set; }
        public bool InvalidateCalled { get; private set; }

        public Task<PayrollProject200WorkbenchPage> GetWorkbenchAsync(
            int year, int month, PayrollReviewQueueFilter filter, string? selectedAdminCaseKey, CancellationToken cancellationToken) =>
            GetShellAsync(year, month, filter, selectedAdminCaseKey, cancellationToken);

        public Task<PayrollProject200WorkbenchPage> GetShellAsync(
            int year, int month, PayrollReviewQueueFilter filter, string? selectedAdminCaseKey, CancellationToken cancellationToken)
        {
            var emptyCategories = new Dictionary<PayrollReviewCategory, int>();
            var cases = string.IsNullOrWhiteSpace(NextFocus)
                ? Array.Empty<PayrollAdminCase>()
                : new[]
                {
                    new PayrollAdminCase(
                        NextFocus!,
                        PayrollReviewCategory.Project200,
                        "10",
                        "Other",
                        new DateOnly(2026, 8, 28),
                        PayrollFindingSeverity.Review,
                        PayrollFindingStatus.Open,
                        "q",
                        "issue",
                        "fact",
                        BonNr: null,
                        ProjectId: "200",
                        BookedSummary: null,
                        PlannedSummary: null,
                        DifferenceSummary: null,
                        EvidenceSummary: null,
                        HybridScenarioNote: null,
                        FriendlyState: null,
                        RuleHint: null,
                        ActionabilityHint: "review",
                        UnderlyingReviewCaseCount: 1,
                        UnderlyingPerformanceCount: 1,
                        TotalBookedHours: 0.5m,
                        UnderlyingCases: [],
                        FindingIds: [],
                        FindingKeys: [],
                        DecisionCode: null,
                        DecisionLabel: null,
                        ReviewComment: null,
                        ReviewedAtUtc: null,
                        ReviewedBy: null,
                        AllowsBulkDisposition: false,
                        Choices: [],
                        Performances: []),
                };
            return Task.FromResult(new PayrollProject200WorkbenchPage(
                year,
                month,
                new PayrollAdminQueueSummary(0, 0, 0, 0, 0, 0, 0, emptyCategories, emptyCategories, emptyCategories, emptyCategories, emptyCategories),
                cases,
                NextFocus,
                null,
                new PayrollProject200WorkbenchMetrics(0, 0, 0, GpsDeferred: true)));
        }

        public Task<PayrollProject200WorkbenchPage> GetCoreDetailAsync(
            int year, int month, PayrollReviewQueueFilter filter, string? selectedAdminCaseKey, CancellationToken cancellationToken) =>
            GetShellAsync(year, month, filter, selectedAdminCaseKey, cancellationToken);

        public Task<PayrollProject200GpsLoadResult> GetGpsContextAsync(
            int year, int month, string adminCaseKey, bool prefetchNext = true, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PayrollProject200ProposeCorrectionResult> ProposeTimeCorrectionAsync(
            int year, int month, string adminCaseKey, long performanceId, TimeOnly newStart, TimeOnly newEnd, string reason, string actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PayrollProject200ProposeCorrectionResult> ProposeDeletePerformanceAsync(
            int year, int month, string adminCaseKey, long performanceId, string reason, string actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public PayrollProject200GpsCacheHint GetGpsCacheHint(string resourceId, DateOnly workDate) =>
            PayrollProject200GpsCacheHint.Unknown;

        public void InvalidateQueueCache(int year, int month) => InvalidateCalled = true;
    }

    private sealed class FakeWorkbench100 : IPayrollProject100WorkbenchService
    {
        public Task<PayrollProject100WorkbenchPage> GetWorkbenchAsync(
            int year, int month, PayrollReviewQueueFilter filter, string? selectedAdminCaseKey, CancellationToken cancellationToken) =>
            GetShellAsync(year, month, filter, selectedAdminCaseKey, cancellationToken);

        public Task<PayrollProject100WorkbenchPage> GetShellAsync(
            int year, int month, PayrollReviewQueueFilter filter, string? selectedAdminCaseKey, CancellationToken cancellationToken)
        {
            var emptyCategories = new Dictionary<PayrollReviewCategory, int>();
            return Task.FromResult(new PayrollProject100WorkbenchPage(
                year,
                month,
                new PayrollAdminQueueSummary(0, 0, 0, 0, 0, 0, 0, emptyCategories, emptyCategories, emptyCategories, emptyCategories, emptyCategories),
                [],
                null,
                null,
                new PayrollProject100WorkbenchMetrics(0, 0, 0, GpsDeferred: true)));
        }

        public Task<PayrollProject100WorkbenchPage> GetCoreDetailAsync(
            int year, int month, PayrollReviewQueueFilter filter, string? selectedAdminCaseKey, CancellationToken cancellationToken) =>
            GetShellAsync(year, month, filter, selectedAdminCaseKey, cancellationToken);

        public Task<PayrollProject100GpsLoadResult> GetGpsContextAsync(
            int year, int month, string adminCaseKey, bool prefetchNext = true, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PayrollProject100ProposeCorrectionResult> ProposeTimeCorrectionAsync(
            int year, int month, string adminCaseKey, long performanceId, TimeOnly newStart, TimeOnly newEnd, string reason, string actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PayrollProject100ProposeCorrectionResult> ProposeDeletePerformanceAsync(
            int year, int month, string adminCaseKey, long performanceId, string reason, string actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public PayrollProject100GpsCacheHint GetGpsCacheHint(string resourceId, DateOnly workDate) =>
            PayrollProject100GpsCacheHint.Unknown;

        public void InvalidateQueueCache(int year, int month)
        {
        }
    }

    private sealed class FakeWorkbenchStandby : IPayrollStandbyWorkbenchService
    {
        public Task<PayrollStandbyWorkbenchPage> GetWorkbenchAsync(
            int year, int month, PayrollReviewQueueFilter filter, string? selectedAdminCaseKey, CancellationToken cancellationToken) =>
            GetShellAsync(year, month, filter, selectedAdminCaseKey, cancellationToken);

        public Task<PayrollStandbyWorkbenchPage> GetShellAsync(
            int year, int month, PayrollReviewQueueFilter filter, string? selectedAdminCaseKey, CancellationToken cancellationToken)
        {
            var emptyCategories = new Dictionary<PayrollReviewCategory, int>();
            return Task.FromResult(new PayrollStandbyWorkbenchPage(
                year,
                month,
                new PayrollAdminQueueSummary(0, 0, 0, 0, 0, 0, 0, emptyCategories, emptyCategories, emptyCategories, emptyCategories, emptyCategories),
                [],
                null,
                null,
                new PayrollStandbyWorkbenchMetrics(0, 0, 0, GpsDeferred: true)));
        }

        public Task<PayrollStandbyWorkbenchPage> GetCoreDetailAsync(
            int year, int month, PayrollReviewQueueFilter filter, string? selectedAdminCaseKey, CancellationToken cancellationToken) =>
            GetShellAsync(year, month, filter, selectedAdminCaseKey, cancellationToken);

        public Task<PayrollStandbyGpsLoadResult> GetGpsContextAsync(
            int year, int month, string adminCaseKey, bool prefetchNext = true, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PayrollStandbyProposeCorrectionResult> ProposeTimeCorrectionAsync(
            int year, int month, string adminCaseKey, long performanceId, TimeOnly newStart, TimeOnly newEnd, string reason, string actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PayrollStandbyProposeCorrectionResult> ProposeDeletePerformanceAsync(
            int year, int month, string adminCaseKey, long performanceId, string reason, string actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public PayrollStandbyGpsCacheHint GetGpsCacheHint(string resourceId, DateOnly workDate) =>
            PayrollStandbyGpsCacheHint.Unknown;

        public void InvalidateQueueCache(int year, int month)
        {
        }
    }

    private sealed class FakeIntelligenceWorkbench : IPayrollIntelligenceWorkbenchService
    {
        public Task<PayrollIntelligenceWorkbenchPage> GetShellAsync(
            int year,
            int month,
            PayrollReviewCategory category,
            PayrollReviewQueueFilter filter,
            string? selectedAdminCaseKey,
            CancellationToken cancellationToken)
        {
            var emptyCategories = new Dictionary<PayrollReviewCategory, int>();
            return Task.FromResult(new PayrollIntelligenceWorkbenchPage(
                year,
                month,
                category,
                new PayrollAdminQueueSummary(0, 0, 0, 0, 0, 0, 0, emptyCategories, emptyCategories, emptyCategories, emptyCategories, emptyCategories),
                [],
                null,
                null));
        }

        public Task<PayrollIntelligenceWorkbenchPage> GetCoreDetailAsync(
            int year,
            int month,
            PayrollReviewCategory category,
            PayrollReviewQueueFilter filter,
            string? selectedAdminCaseKey,
            CancellationToken cancellationToken) =>
            GetShellAsync(year, month, category, filter, selectedAdminCaseKey, cancellationToken);

        public Task<PayrollIntelligenceGpsLoadResult> GetGpsContextAsync(
            int year,
            int month,
            PayrollReviewCategory category,
            string adminCaseKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PayrollIntelligenceProposeResult> ProposeAdjustAsync(
            int year, int month, string adminCaseKey, long performanceId, TimeOnly newStart, TimeOnly newEnd, string reason, string actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PayrollIntelligenceProposeResult> ProposeDeleteAsync(
            int year, int month, string adminCaseKey, long performanceId, string reason, string actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PayrollIntelligenceProposeResult> ProposeCreateAsync(
            int year, int month, string adminCaseKey, TimeOnly start, TimeOnly endTime, string reason, string actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PayrollIntelligenceProposeResult> ProposePauseBoundaryAdjustAsync(
            int year,
            int month,
            string resourceId,
            DateOnly workDate,
            long performanceId,
            TimeOnly? newStart,
            TimeOnly newEnd,
            TimeSpan newPause,
            string reason,
            string actor,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public PayrollIntelligenceGpsCacheHint GetGpsCacheHint(string resourceId, DateOnly workDate) =>
            PayrollIntelligenceGpsCacheHint.Unknown;

        public void InvalidateQueueCache(int year, int month)
        {
        }
    }

    private sealed class FakeUser : ICurrentUserContext
    {
        public AuthenticatedActor? CurrentUser =>
            new("benjamin.tonoli@thebelgian.be", "cf-sub", "Benjamin Tonoli");

        public AuthenticatedActor RequireActor(string developmentFallbackReviewer) =>
            CurrentUser!;
    }

    private sealed class FakeTempDataProvider : ITempDataProvider
    {
        private Dictionary<string, object?> _data = new(StringComparer.Ordinal);

        public IDictionary<string, object?> LoadTempData(HttpContext context) => _data;

        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) =>
            _data = new Dictionary<string, object?>(values, StringComparer.Ordinal);
    }

    private sealed class FakeActions : IPayrollActionService
    {
        public PayrollActionConfirmationView? Confirmation { get; set; }
        public PayrollActionExecutionResult? ExecuteResult { get; set; }
        public int ExecuteCalls { get; private set; }
        public Guid? LastExecutedActionId { get; private set; }
        public string? LastComment { get; private set; }
        public string? LastActor { get; private set; }

        public Task<IReadOnlyList<PayrollProposedActionRecord>> ProposeFromFindingsAsync(
            int year, int month, string? resourceId, string actor, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PayrollProposedActionRecord>>([]);

        public Task<IReadOnlyList<PayrollProposedActionRecord>> ListActionsAsync(
            int year, int month, string? resourceId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PayrollProposedActionRecord>>([]);

        public Task<PayrollProposedActionRecord?> GetActionAsync(
            Guid actionId, CancellationToken cancellationToken) =>
            Task.FromResult<PayrollProposedActionRecord?>(null);

        public Task<PayrollActionConfirmationView?> PrepareConfirmationAsync(
            Guid actionId, CancellationToken cancellationToken) =>
            Task.FromResult(Confirmation is null || Confirmation.ActionId != actionId ? null : Confirmation);

        public Task<PayrollActionExecutionResult> ExecuteAsync(
            Guid actionId, string comment, string actor, CancellationToken cancellationToken)
        {
            ExecuteCalls++;
            LastExecutedActionId = actionId;
            LastComment = comment;
            LastActor = actor;
            return Task.FromResult(ExecuteResult ?? new PayrollActionExecutionResult(
                actionId, PayrollProposedActionStatus.Failed, "no result", null, null));
        }

        public Task CancelAsync(Guid actionId, string actor, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<PayrollActionProposeResult> UpdateCreateProposalAsync(
            Guid actionId,
            TimeOnly? start,
            TimeOnly? endTime,
            int? mainTaskId,
            string actor,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PayrollActionProposeResult(false, "not implemented", actionId, "NotImplemented"));

        public Task<PayrollActionProposeResult> ProposeDeleteForPerformanceAsync(
            int year,
            int month,
            string resourceId,
            DateOnly workDate,
            long performanceId,
            string reason,
            string actor,
            PayrollFindingType findingType,
            string? actionKey = null,
            string? sourceFindingKey = null,
            int? sourceFindingId = null,
            IReadOnlyList<string>? sourceFindingKeys = null,
            IReadOnlyList<int>? sourceFindingIds = null,
            string? prestOmschr = null,
            string? prestMemo = null,
            string? bonTechnicianRemark = null,
            string? projectLabel = null,
            string? expectedActivityType = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PayrollActionProposeResult(false, "n/a", null, null));
    }
}
