using System.Text;
using Microsoft.AspNetCore.DataProtection;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure;
using TheBelgian.TimeControl.Infrastructure.Pilot;
using TheBelgian.TimeControl.Web.Pages.Pilot;

var isExportLockedHoldoutReview = args.Contains(
    "--export-locked-holdout-review",
    StringComparer.OrdinalIgnoreCase);
if (isExportLockedHoldoutReview)
{
    // Offline-only path: no DI, no Plenion/Powerfleet/Geoapify initialization.
    var contentRoot = Directory.GetCurrentDirectory();
    var webProjectHint = Path.Combine(contentRoot, "src", "TheBelgian.TimeControl.Web");
    var docsPath = Directory.Exists(webProjectHint)
        ? Path.GetFullPath(Path.Combine(contentRoot, "docs"))
        : Path.GetFullPath(Path.Combine(contentRoot, "..", "..", "docs"));
    Directory.CreateDirectory(docsPath);
    Console.WriteLine("LockedHoldoutReviewExportMode=offline-local-only");
    Console.WriteLine("ExternalProviders=disabled");
    Console.WriteLine($"DocsPath={docsPath}");
    try
    {
        var exported = LockedHoldoutReviewPackService.ExportReviewPack(docsPath);
        Console.WriteLine($"CaseCount={exported.CaseCount}");
        Console.WriteLine($"HoldoutContentSha256={exported.HoldoutContentSha256}");
        Console.WriteLine($"Markdown={exported.MarkdownPath}");
        Console.WriteLine($"Labels={exported.LabelsPath}");
        Console.WriteLine("Blind=True");
        Console.WriteLine("HoldoutMutated=False");
        Environment.ExitCode = 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error={ex.Message}");
        Environment.ExitCode = 1;
    }

    return;
}

var isEvaluateLockedHoldout = args.Contains(
    "--evaluate-locked-holdout",
    StringComparer.OrdinalIgnoreCase);
if (isEvaluateLockedHoldout)
{
    // Offline-only path: no DI, no Plenion/Powerfleet/Geoapify initialization.
    var contentRoot = Directory.GetCurrentDirectory();
    var webProjectHint = Path.Combine(contentRoot, "src", "TheBelgian.TimeControl.Web");
    var docsPath = Directory.Exists(webProjectHint)
        ? Path.GetFullPath(Path.Combine(contentRoot, "docs"))
        : Path.GetFullPath(Path.Combine(contentRoot, "..", "..", "docs"));
    Directory.CreateDirectory(docsPath);
    Console.WriteLine("LockedHoldoutMode=offline-local-only");
    Console.WriteLine("ExternalProviders=disabled");
    Console.WriteLine($"DocsPath={docsPath}");
    var holdoutEvaluation = LockedHoldoutEvaluationService.Evaluate(docsPath);
    Console.WriteLine($"Completed={holdoutEvaluation.Completed}");
    Console.WriteLine($"Decision={holdoutEvaluation.Decision}");
    Console.WriteLine($"ExitCode={holdoutEvaluation.ExitCode}");
    Console.WriteLine($"FinalJson={holdoutEvaluation.FinalJsonPath}");
    Console.WriteLine($"FinalMarkdown={holdoutEvaluation.FinalMarkdownPath}");
    Console.WriteLine($"StartedMarker={holdoutEvaluation.StartedMarkerPath}");
    if (holdoutEvaluation.Report is not null)
    {
        var report = holdoutEvaluation.Report;
        Console.WriteLine($"HoldoutOpened={report.HoldoutOpened}");
        Console.WriteLine($"ExternalDataAccessed={report.ExternalDataAccessed}");
        Console.WriteLine($"GitCommit={report.GitCommit}");
        Console.WriteLine($"ConfigurationHashSha256={report.ConfigurationHashSha256}");
        Console.WriteLine($"HoldoutContentSha256={report.HoldoutContentSha256}");
        Console.WriteLine(
            $"Metrics=Cases={report.CaseCount}|Accepted={report.AcceptedMatches}|CorrectAccepted={report.CorrectAcceptedMatches}|Precision={report.Precision}|Coverage={report.Coverage}|FP={report.FalsePositives}|FN={report.FalseNegatives}|WrongVisit={report.WrongVisitCandidateChoices}|Abstentions={report.Abstentions}");
        foreach (var pair in report.ErrorCategories.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"ErrorCategory={pair.Key}:{pair.Value}");
        }
    }

    foreach (var message in holdoutEvaluation.Messages)
    {
        Console.WriteLine($"Message={message}");
    }

    Environment.ExitCode = holdoutEvaluation.ExitCode;
    return;
}

var isVerifyFrozenMatcher = args.Contains(
    "--verify-frozen-matcher",
    StringComparer.OrdinalIgnoreCase);
if (isVerifyFrozenMatcher)
{
    // Offline-only path: no DI, no Plenion/Powerfleet/Geoapify initialization.
    var contentRoot = Directory.GetCurrentDirectory();
    var webProjectHint = Path.Combine(contentRoot, "src", "TheBelgian.TimeControl.Web");
    var docsPath = Directory.Exists(webProjectHint)
        ? Path.GetFullPath(Path.Combine(contentRoot, "docs"))
        : Path.GetFullPath(Path.Combine(contentRoot, "..", "..", "docs"));
    Directory.CreateDirectory(docsPath);
    Console.WriteLine("FrozenMatcherMode=offline-local-only");
    Console.WriteLine("ExternalProviders=disabled");
    Console.WriteLine($"DocsPath={docsPath}");
    var verification = FrozenMatcherVerificationService.Verify(docsPath);
    Console.WriteLine($"GitCommit={verification.GitCommit}");
    Console.WriteLine($"ConfigurationHashSha256={verification.ConfigurationHashSha256}");
    Console.WriteLine($"Manifest={verification.ManifestPath}");
    Console.WriteLine($"Report={verification.ReportPath}");
    Console.WriteLine($"Passed={verification.Passed}");
    Console.WriteLine($"ExitCode={verification.ExitCode}");
    Console.WriteLine($"ExternalDataAccessed={verification.ExternalDataAccessed}");
    Console.WriteLine($"HoldoutOpened={verification.HoldoutOpened}");
    Console.WriteLine(
        $"Calibration=Cases={verification.Calibration.CaseCount}|Precision={verification.Calibration.Precision}|Coverage={verification.Calibration.Coverage}|FP={verification.Calibration.FalsePositives}|FN={verification.Calibration.FalseNegatives}|WrongVisit={verification.Calibration.WrongVisitCandidateChoices}");
    Console.WriteLine(
        $"RecoveryOnly=Cases={verification.RecoveryOnly.CaseCount}|Precision={verification.RecoveryOnly.Precision}|FP={verification.RecoveryOnly.FalsePositives}|FN={verification.RecoveryOnly.FalseNegatives}|WrongVisit={verification.RecoveryOnly.WrongVisitCandidateChoices}");
    Console.WriteLine(
        $"AllLabeledHybrid=Cases={verification.AllLabeledHybrid.CaseCount}|Precision={verification.AllLabeledHybrid.Precision}|FP={verification.AllLabeledHybrid.FalsePositives}|FN={verification.AllLabeledHybrid.FalseNegatives}|WrongVisit={verification.AllLabeledHybrid.WrongVisitCandidateChoices}");
    foreach (var check in verification.RegressionChecks)
    {
        Console.WriteLine(
            $"Regression={check.PerformanceId}|Expect={check.Expectation}|Passed={check.Passed}|Observed={check.Observed}");
    }

    foreach (var note in verification.Notes)
    {
        Console.WriteLine($"Note={note}");
    }

    foreach (var failure in verification.Failures)
    {
        Console.Error.WriteLine($"Failure={failure}");
    }

    Environment.ExitCode = verification.ExitCode;
    return;
}

var isBroader = args.Contains("--broader-validation", StringComparer.OrdinalIgnoreCase);
var isCoverageGap = args.Contains("--coverage-gap", StringComparer.OrdinalIgnoreCase);
var isActivity = args.Contains("--activity-classification", StringComparer.OrdinalIgnoreCase);
var isAdaptive = args.Contains("--adaptive-location-matching", StringComparer.OrdinalIgnoreCase);
var isBenchmark = args.Contains("--location-matching-benchmark", StringComparer.OrdinalIgnoreCase);
var isBenchmarkResample = args.Contains(
    "--location-matching-benchmark-resample",
    StringComparer.OrdinalIgnoreCase);
var isBenchmarkPurify = args.Contains(
    "--location-matching-benchmark-purify",
    StringComparer.OrdinalIgnoreCase);
var isExportCalibration = args.Contains(
    "--export-calibration-review",
    StringComparer.OrdinalIgnoreCase);
var importCalibrationIndex = Array.FindIndex(
    args,
    static item => string.Equals(item, "--import-calibration-labels", StringComparison.OrdinalIgnoreCase));
var isImportCalibration = importCalibrationIndex >= 0;
var resetReviewerIndex = Array.FindIndex(
    args,
    static item => string.Equals(item, "--reset-calibration-reviewer", StringComparison.OrdinalIgnoreCase));
var isResetCalibrationReviewer = resetReviewerIndex >= 0;

var isCalibrationEval = args.Contains(
    "--calibration-single-reviewer-eval",
    StringComparer.OrdinalIgnoreCase);
var isExportRecoveryAudit = args.Contains(
    "--export-recovery-audit",
    StringComparer.OrdinalIgnoreCase);
var importRecoveryAuditIndex = Array.FindIndex(
    args,
    static item => string.Equals(item, "--import-recovery-audit-labels", StringComparison.OrdinalIgnoreCase));
var isImportRecoveryAudit = importRecoveryAuditIndex >= 0;
var isEvaluateRecoveryAudit = args.Contains(
    "--evaluate-recovery-audit",
    StringComparer.OrdinalIgnoreCase);

if (isBroader || isCoverageGap || isActivity || isAdaptive ||
    isBenchmark || isBenchmarkResample || isBenchmarkPurify ||
    isExportCalibration || isImportCalibration || isResetCalibrationReviewer ||
    isCalibrationEval || isExportRecoveryAudit || isImportRecoveryAudit ||
    isEvaluateRecoveryAudit)
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole();
    builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
    builder.Services.AddTimeControlInfrastructure(builder.Configuration);
    await using var host = builder.Build();
    await host.Services.InitializeTimeControlDatabaseAsync();
    await using var scope = host.Services.CreateAsyncScope();
    var docsPath = Path.GetFullPath(
        Path.Combine(builder.Environment.ContentRootPath, "..", "..", "docs"));
    Directory.CreateDirectory(docsPath);
    if (isCalibrationEval)
    {
        var evaluationService = scope.ServiceProvider
            .GetRequiredService<CalibrationSingleReviewerEvaluationService>();
        var evaluation = await evaluationService.EvaluateAsync(docsPath, CancellationToken.None);
        Console.WriteLine($"ReferenceSet={evaluation.ReferenceSet}");
        Console.WriteLine($"CaseCount={evaluation.CaseCount}");
        Console.WriteLine($"HighConfidenceCaseCount={evaluation.HighConfidenceCaseCount}");
        Console.WriteLine($"LearnedClusters={evaluation.LearnedClusterCount}");
        Console.WriteLine($"BestVariant={evaluation.BestVariant}");
        Console.WriteLine($"HybridAcceptanceCriteriaMet={evaluation.HybridAcceptanceCriteriaMet}");
        Console.WriteLine($"HybridAcceptanceNotes={evaluation.HybridAcceptanceNotes}");
        Console.WriteLine($"RecoveredPerformanceIds={string.Join(',', evaluation.RecoveredPerformanceIds)}");
        foreach (var gap in evaluation.GapAnalysis)
        {
            Console.WriteLine(
                $"Gap={gap.PerformanceId}|Label={gap.Label}|BaselineAccepted={gap.BaselineAccepted}|AdaptiveUnresolved={gap.AdaptiveUnresolved}|Recoverable={gap.IsRecoverableGap}|Dist={gap.DistanceMeters}|OverlapMin={gap.OverlapMinutes}|OverlapPct={gap.OverlapPercent}|ArrDiff={gap.ArrivalVersusStartMinutes}|DepDiff={gap.DepartureVersusEndMinutes}|Geo={gap.GeocodeQuality}|Competitors={gap.CompetingCandidateCount}|Margin={gap.ScoreMarginVsSecond}|Prev={gap.PreviousPerformance}|Next={gap.NextPerformance}|Abstention={gap.AdaptiveAbstentionReason}|HybridRecovered={gap.HybridRecovered}|RecoveryReason={gap.HybridRecoveryReason}");
        }

        var sanity = evaluation.DevelopmentSanityCheck;
        Console.WriteLine(
            $"DevSanity=Cases={sanity.CaseCount}|Accepted={sanity.Accepted}|Unresolved={sanity.Unresolved}|Ambiguous={sanity.Ambiguous}|RecoveryOnly={sanity.RecoveryOnlyMatches}");
        foreach (var pair in sanity.RecoveryByDistanceZone.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"DevRecoveryDistance={pair.Key}:{pair.Value}");
        }

        foreach (var pair in sanity.RecoveryByOverlapZone.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"DevRecoveryOverlap={pair.Key}:{pair.Value}");
        }

        foreach (var risk in sanity.NotableRiskPatterns)
        {
            Console.WriteLine($"DevRisk={risk}");
        }

        foreach (var variant in evaluation.Variants)
        {
            var all = variant.AllCases;
            var high = variant.HighConfidenceOnly;
            Console.WriteLine(
                $"Variant={variant.Name}|AllAccepted={all.AcceptedMatches}|AllCorrectAccepted={all.CorrectAcceptedMatches}|AllPrecision={all.Precision}|AllCoverage={all.Coverage}|AllFP={all.FalsePositives}|AllFN={all.FalseNegatives}|AllWrongStop={all.WrongStopIdChoices}|HighAccepted={high.AcceptedMatches}|HighCorrectAccepted={high.CorrectAcceptedMatches}|HighPrecision={high.Precision}|HighCoverage={high.Coverage}|HighFP={high.FalsePositives}|HighFN={high.FalseNegatives}|HighWrongStop={high.WrongStopIdChoices}");
            foreach (var error in variant.Errors)
            {
                Console.WriteLine(
                    $"Error={variant.Name}|{error.PerformanceId}|{error.Label}|{error.ReviewerConfidence}|{error.PredictedDecision}|{error.PredictedStopId}|{error.Reason}");
            }
        }

        foreach (var cause in evaluation.MainErrorCauses)
        {
            Console.WriteLine($"MainErrorCause={cause}");
        }

        Console.WriteLine($"NextStep={evaluation.RecommendedNextStep}");
        return;
    }

    if (isResetCalibrationReviewer)
    {
        if (resetReviewerIndex + 1 >= args.Length ||
            !int.TryParse(args[resetReviewerIndex + 1], out var resetReviewer) ||
            resetReviewer is not (1 or 2))
        {
            Console.Error.WriteLine("Gebruik: --reset-calibration-reviewer 1|2");
            Environment.ExitCode = 1;
            return;
        }

        var reset = LocationMatchingCalibrationBatchService.ResetReviewerLabels(
            docsPath,
            resetReviewer);
        var templatePath = resetReviewer == 2
            ? LocationMatchingCalibrationBatchService.WriteEmptyReviewerTemplate(
                docsPath,
                "calibration-labels-reviewer2.json")
            : LocationMatchingCalibrationBatchService.WriteEmptyReviewerTemplate(
                docsPath,
                "calibration-labels.json");
        var cases = LocationMatchingBenchmarkService.LoadCalibrationCases(docsPath);
        var reviewer1 = cases.Count(item => !string.IsNullOrWhiteSpace(item.Label));
        var reviewer2 = cases.Count(item => !string.IsNullOrWhiteSpace(item.SecondReviewLabel));
        var conflicts = cases.Count(item =>
            string.Equals(item.AdjudicationStatus, "Disagreement", StringComparison.Ordinal));
        Console.WriteLine($"ResetReviewer={reset.Reviewer}");
        Console.WriteLine($"Reviewer1={reviewer1}");
        Console.WriteLine($"Reviewer2={reviewer2}");
        Console.WriteLine($"Conflicts={conflicts}");
        Console.WriteLine($"Template={templatePath}");
        return;
    }

    if (isExportRecoveryAudit)
    {
        var auditService = scope.ServiceProvider
            .GetRequiredService<LocationMatchingRecoveryAuditService>();
        var exported = await auditService.ExportAsync(docsPath, CancellationToken.None);
        Console.WriteLine($"Markdown={exported.MarkdownPath}");
        Console.WriteLine($"Labels={exported.LabelsPath}");
        Console.WriteLine($"Set={exported.SetPath}");
        Console.WriteLine($"CaseCount={exported.CaseCount}");
        Console.WriteLine($"NewRecoveryOnly={exported.NewRecoveryOnlyCount}");
        Console.WriteLine(
            $"Distribution=RecoveryOnly={exported.Distribution.RecoveryOnly}|AdaptiveAcceptedControl={exported.Distribution.AdaptiveAcceptedControl}|AbstentionControl={exported.Distribution.AbstentionControl}|WeakOverlapRecovery={exported.Distribution.WeakOverlapRecovery}|ProbableDistanceRecovery={exported.Distribution.ProbableDistanceRecovery}|WeakGeocodeRecovery={exported.Distribution.WeakGeocodeRecovery}|Total={exported.Distribution.Total}");
        return;
    }

    if (isImportRecoveryAudit)
    {
        if (importRecoveryAuditIndex + 1 >= args.Length)
        {
            Console.Error.WriteLine("Gebruik: --import-recovery-audit-labels <bestand>");
            Environment.ExitCode = 1;
            return;
        }

        var labelsPath = Path.GetFullPath(args[importRecoveryAuditIndex + 1]);
        try
        {
            var auditService = scope.ServiceProvider
                .GetRequiredService<LocationMatchingRecoveryAuditService>();
            var imported = auditService.ImportLabels(docsPath, labelsPath);
            Console.WriteLine($"ImportedCount={imported.ImportedCount}");
            Console.WriteLine($"LabeledCount={imported.LabeledCount}");
            Console.WriteLine($"Labels={imported.LabelsPath}");
            Console.WriteLine($"Set={imported.SetPath}");
            var evaluation = await auditService.EvaluateAsync(docsPath, CancellationToken.None);
            Console.WriteLine($"EvalStatus={evaluation.Status}");
            Console.WriteLine($"LabelsComplete={evaluation.LabelsComplete}");
            if (evaluation.RecoveryOnly is not null)
            {
                Console.WriteLine(
                    $"RecoveryOnlyPrecision={evaluation.RecoveryOnly.Precision}|FP={evaluation.RecoveryOnly.FalsePositives}|WrongStop={evaluation.RecoveryOnly.WrongStopIdChoices}");
            }

            if (evaluation.AllLabeledHybrid is not null)
            {
                Console.WriteLine(
                    $"AllLabeledHybridPrecision={evaluation.AllLabeledHybrid.Precision}|FP={evaluation.AllLabeledHybrid.FalsePositives}|WrongStop={evaluation.AllLabeledHybrid.WrongStopIdChoices}");
            }

            foreach (var note in evaluation.Notes)
            {
                Console.WriteLine($"Note={note}");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine(exception.Message);
            Environment.ExitCode = 1;
        }

        return;
    }

    if (isEvaluateRecoveryAudit)
    {
        var auditService = scope.ServiceProvider
            .GetRequiredService<LocationMatchingRecoveryAuditService>();
        var evaluation = await auditService.EvaluateAsync(docsPath, CancellationToken.None);
        Console.WriteLine($"CaseCount={evaluation.CaseCount}");
        Console.WriteLine($"LabeledCount={evaluation.LabeledCount}");
        Console.WriteLine($"LabelsComplete={evaluation.LabelsComplete}");
        Console.WriteLine($"Status={evaluation.Status}");
        if (evaluation.RecoveryOnly is not null)
        {
            var slice = evaluation.RecoveryOnly;
            Console.WriteLine(
                $"RecoveryOnly=Cases={slice.CaseCount}|Accepted={slice.AcceptedMatches}|Correct={slice.CorrectAcceptedMatches}|Precision={slice.Precision}|FP={slice.FalsePositives}|FN={slice.FalseNegatives}|WrongStop={slice.WrongStopIdChoices}");
        }

        if (evaluation.WeakOverlapRecovery is not null)
        {
            var slice = evaluation.WeakOverlapRecovery;
            Console.WriteLine(
                $"WeakOverlap=Cases={slice.CaseCount}|Precision={slice.Precision}|FP={slice.FalsePositives}|WrongStop={slice.WrongStopIdChoices}");
        }

        if (evaluation.ByDistanceZone is not null)
        {
            foreach (var pair in evaluation.ByDistanceZone.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                Console.WriteLine(
                    $"DistanceZone={pair.Key}|Cases={pair.Value.CaseCount}|Precision={pair.Value.Precision}|FP={pair.Value.FalsePositives}|WrongStop={pair.Value.WrongStopIdChoices}");
            }
        }

        if (evaluation.ByGeocodeQuality is not null)
        {
            foreach (var pair in evaluation.ByGeocodeQuality.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                Console.WriteLine(
                    $"Geocode={pair.Key}|Cases={pair.Value.CaseCount}|Precision={pair.Value.Precision}|FP={pair.Value.FalsePositives}|WrongStop={pair.Value.WrongStopIdChoices}");
            }
        }

        if (evaluation.AllLabeledHybrid is not null)
        {
            var slice = evaluation.AllLabeledHybrid;
            Console.WriteLine(
                $"AllLabeledHybrid=Cases={slice.CaseCount}|Precision={slice.Precision}|FP={slice.FalsePositives}|WrongStop={slice.WrongStopIdChoices}");
        }

        foreach (var error in evaluation.Errors)
        {
            Console.WriteLine(
                $"Error={error.PerformanceId}|{error.Stratum}|{error.Label}|{error.HybridDecision}|{error.Reason}");
        }

        foreach (var note in evaluation.Notes)
        {
            Console.WriteLine($"Note={note}");
        }

        return;
    }

    if (isExportCalibration)
    {
        var exported = LocationMatchingCalibrationBatchService.ExportReviewPack(docsPath);
        Console.WriteLine($"Markdown={exported.MarkdownPath}");
        Console.WriteLine($"Json={exported.JsonPath}");
        Console.WriteLine($"Template={exported.TemplatePath}");
        Console.WriteLine($"CaseCount={exported.CaseCount}");
        return;
    }

    if (isImportCalibration)
    {
        if (importCalibrationIndex + 1 >= args.Length)
        {
            Console.Error.WriteLine(
                "Gebruik: --import-calibration-labels <bestand> --reviewer 1|2");
            return;
        }

        var labelsPath = Path.GetFullPath(args[importCalibrationIndex + 1]);
        var reviewer = ParseReviewer(args);
        try
        {
            var imported = LocationMatchingCalibrationBatchService.ImportLabels(
                docsPath,
                labelsPath,
                reviewer);
            Console.WriteLine($"Reviewer={imported.Reviewer}");
            Console.WriteLine($"ImportedCount={imported.ImportedCount}");
            Console.WriteLine($"Labels={imported.LabelsPath}");
            Console.WriteLine($"Calibration={imported.CalibrationPath}");
            Console.WriteLine($"AgreementStatus={imported.Agreement.Status}");
            Console.WriteLine($"ExactLabelAgreement={imported.Agreement.ExactLabelAgreementCount}");
            Console.WriteLine($"ExpectedStopIdAgreement={imported.Agreement.ExpectedStopIdAgreementCount}");
            Console.WriteLine($"Conflicts={imported.Agreement.ConflictCount}");
            Console.WriteLine($"CohensKappa={imported.Agreement.CohensKappa}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine(exception.Message);
            Environment.ExitCode = 1;
        }

        return;
    }

    if (isBenchmark || isBenchmarkResample || isBenchmarkPurify)
    {
        var benchmarkService = scope.ServiceProvider
            .GetRequiredService<LocationMatchingBenchmarkService>();
        if (isBenchmarkPurify)
        {
            var purified = await benchmarkService.PurifyAndCalibrateAsync(
                docsPath,
                CancellationToken.None);
            foreach (var finding in purified.PriorLeakage.Findings)
            {
                Console.WriteLine($"Leakage={finding}");
            }

            Console.WriteLine($"DevelopmentRole={purified.DevelopmentRole}");
            Console.WriteLine($"ChallengeRole={purified.ChallengeRole}");
            Console.WriteLine($"HoldoutPeriod={purified.HoldoutPeriod}");
            Console.WriteLine($"PureHoldoutCaseCount={purified.PureHoldoutCaseCount}");
            Console.WriteLine($"HoldoutUniqueLocations={purified.HoldoutUniqueLocationCount}");
            Console.WriteLine($"CalibrationCaseCount={purified.CalibrationCaseCount}");
            Console.WriteLine($"CalibrationReviewer={purified.CalibrationReviewerPath}");
            Console.WriteLine($"HoldoutSha256={purified.HoldoutContentSha256}");
            Console.WriteLine($"DevelopmentCaseCount={purified.DevelopmentCaseCount}");
            Console.WriteLine($"ChallengeCaseCount={purified.ChallengeCaseCount}");
            return;
        }

        var benchmark = isBenchmarkResample
            ? benchmarkService.ResampleFromSavedPool(docsPath)
            : await benchmarkService.RunAsync(docsPath, CancellationToken.None);
        var markdownPath = Path.Combine(docsPath, "location-matching-benchmark.md");
        var jsonPath = Path.Combine(docsPath, "location-matching-benchmark-report.json");
        await File.WriteAllTextAsync(
            markdownPath,
            LocationMatchingBenchmarkReportWriter.ToMarkdown(benchmark),
            Encoding.UTF8);
        await File.WriteAllTextAsync(
            jsonPath,
            LocationMatchingBenchmarkReportWriter.ToJson(benchmark),
            Encoding.UTF8);
        Console.WriteLine($"CompleteMonths={string.Join(',', benchmark.CompleteMonths)}");
        Console.WriteLine($"DevelopmentCaseCount={benchmark.DevelopmentCaseCount}");
        Console.WriteLine($"HoldoutCaseCount={benchmark.HoldoutCaseCount}");
        Console.WriteLine($"HoldoutUniqueLocations={benchmark.HoldoutUniqueLocationCount}");
        Console.WriteLine($"ChallengeCaseCount={benchmark.ChallengeCaseCount}");
        Console.WriteLine($"SeenLocation={benchmark.SeenLocationCount}");
        Console.WriteLine($"UnseenLocation={benchmark.UnseenLocationCount}");
        Console.WriteLine($"BlindReviewer={benchmark.BlindReviewerPath}");
        Console.WriteLine($"Report={markdownPath}");
        return;
    }

    var service = scope.ServiceProvider.GetRequiredService<IBroaderValidationPilotService>();
    var from = ParseDate(args, "--from", new DateOnly(2026, 7, 1));
    var through = ParseDate(args, "--through", new DateOnly(2026, 7, 28));
    var broader = await service.RunAsync(
        new BroaderValidationRequest(
            BroaderModel.DefaultTechnicianNames
                .Select(name => new BroaderValidationTechnicianRequest(name))
                .ToArray(),
            from,
            through,
            5),
        CancellationToken.None);
    var cachePath = BroaderValidationCache.DefaultPath(docsPath);
    if (broader.Summary.ProcessedTechnicianCount > 0)
    {
        BroaderValidationCache.Save(cachePath, broader);
    }
    else if (isAdaptive)
    {
        var cached = BroaderValidationCache.TryLoad(cachePath);
        if (cached is null || cached.Summary.ProcessedTechnicianCount <= 0)
        {
            Console.Error.WriteLine(
                "Adaptive matching afgebroken: Plenion onbereikbaar en geen lokale full-cache.");
            Console.WriteLine("BaselineCoverage=39");
            Console.WriteLine("AdaptiveWithoutLearning=n/a");
            Console.WriteLine("AdaptiveWithLearning=n/a");
            Console.WriteLine("PrecisionKind=estimated");
            Console.WriteLine("PrecisionPercent=n/a");
            Console.WriteLine("LearnedClusters=n/a");
            Console.WriteLine("Ambiguous=n/a");
            Console.WriteLine("Unresolved=n/a");
            Console.WriteLine(
                "LargestGain=Historische clusters 251-500m + overlappercentage (prior gap: +15 cases / ~57% plafond)");
            Console.WriteLine("TargetEightyResponsible=False");
            Console.WriteLine(
                "NextStep=Herstel DNS/VPN naar tbws01:4900 en herhaal --adaptive-location-matching op dezelfde 82-noemer.");
            return;
        }

        broader = cached;
        Console.WriteLine($"UsingCache={cachePath}");
    }

    if (isAdaptive)
    {
        var analysis = AdaptiveLocationValidationService.Analyze(broader, docsPath);
        var markdownPath = Path.Combine(docsPath, "adaptive-location-matching-report.md");
        var jsonPath = Path.Combine(docsPath, "adaptive-location-matching-report.json");
        await File.WriteAllTextAsync(
            markdownPath,
            AdaptiveLocationReportWriter.ToMarkdown(analysis),
            Encoding.UTF8);
        await File.WriteAllTextAsync(
            jsonPath,
            AdaptiveLocationReportWriter.ToJson(analysis),
            Encoding.UTF8);
        Console.WriteLine($"BaselineCoverage={analysis.Baseline.ReliableCoveragePercent}");
        Console.WriteLine(
            $"AdaptiveWithoutLearning={analysis.AdaptiveWithoutLearning.ReliableCoveragePercent}");
        Console.WriteLine(
            $"AdaptiveWithLearning={analysis.AdaptiveWithLearning.ReliableCoveragePercent}");
        Console.WriteLine($"PrecisionKind={analysis.PrecisionKind}");
        Console.WriteLine($"PrecisionPercent={analysis.PrecisionPercent}");
        Console.WriteLine($"LearnedClusters={analysis.LearnedClusterCount}");
        Console.WriteLine($"Ambiguous={analysis.AdaptiveWithLearning.Ambiguous}");
        Console.WriteLine($"Unresolved={analysis.AdaptiveWithLearning.Unresolved}");
        Console.WriteLine($"LargestGain={analysis.LargestGainRules}");
        Console.WriteLine(
            $"TargetEightyResponsible={analysis.TargetEightyPercentResponsible}");
        Console.WriteLine($"SelectedConfig={analysis.SelectedConfiguration.Name}");
        Console.WriteLine($"NextStep={analysis.RecommendedNextStep}");
        Console.WriteLine($"Sample={analysis.StratifiedSamplePath}");
        Console.WriteLine($"Report={markdownPath}");
        return;
    }

    if (isActivity)
    {
        var analysis = ActivityClassificationAnalysisService.Analyze(broader);
        var markdownPath = Path.Combine(docsPath, "activity-classification-report.md");
        var jsonPath = Path.Combine(docsPath, "activity-classification-report.json");
        await File.WriteAllTextAsync(
            markdownPath,
            ActivityClassificationReportWriter.ToMarkdown(analysis),
            Encoding.UTF8);
        await File.WriteAllTextAsync(
            jsonPath,
            ActivityClassificationReportWriter.ToJson(analysis),
            Encoding.UTF8);
        foreach (var summary in analysis.TypeSummaries)
        {
            Console.WriteLine(
                $"Type={summary.ActivityType}|Count={summary.PerformanceCount}|GeoRequired={summary.RequiresGeographicMatchCount}|IncorrectDenominator={summary.IncorrectlyInLocationDenominatorCount}|Unknown={summary.UnknownCount}|HFDTAAK={string.Join(';', summary.MainTaskCodes)}");
        }

        Console.WriteLine($"OpenCases={analysis.OpenCases.OpenCaseCount}");
        Console.WriteLine($"OpenNotLocationBound={analysis.OpenCases.NotLocationBoundCount}");
        Console.WriteLine($"OpenStillLocationBound={analysis.OpenCases.StillLocationBoundCount}");
        Console.WriteLine($"OpenUnknown={analysis.OpenCases.UnknownCount}");
        Console.WriteLine(
            $"CorrectedReliablePercent={analysis.CorrectedMatch.CorrectedReliablePercent}");
        Console.WriteLine(
            $"LocationBoundResolutions={analysis.CorrectedMatch.LocationBoundResolutionCount}");
        Console.WriteLine(
            $"RemainingNoReliableMatch={analysis.CorrectedMatch.RemainingNoReliableMatchCount}");
        Console.WriteLine(
            $"AliasFlippableLocationBound={analysis.CorrectedMatch.AliasFlippableLocationBoundCount}");
        Console.WriteLine(
            $"PotentialAfterAlias={analysis.CorrectedMatch.PotentialReliablePercentAfterAliases}");
        Console.WriteLine($"Advice={analysis.AliasAdvice}");
        Console.WriteLine($"Report={markdownPath}");
        return;
    }

    if (isCoverageGap)
    {
        var analysis = CoverageGapAnalysisService.Analyze(broader);
        var markdownPath = Path.Combine(docsPath, "coverage-gap-analysis.md");
        var jsonPath = Path.Combine(docsPath, "coverage-gap-analysis.json");
        await File.WriteAllTextAsync(
            markdownPath,
            CoverageGapReportWriter.ToMarkdown(analysis),
            Encoding.UTF8);
        await File.WriteAllTextAsync(
            jsonPath,
            CoverageGapReportWriter.ToJson(analysis),
            Encoding.UTF8);
        Console.WriteLine($"EmployeeLinks={analysis.EmployeeLinks.Count}");
        foreach (var link in analysis.EmployeeLinks)
        {
            Console.WriteLine(
                $"Link={link.PlenionOmschr}|IDRESOURCE={link.PlenionIdResource}|RESCODE={link.PlenionResCode}|driverid={link.PowerfleetDriverId}|drivername={link.PowerfleetDriverName}|objects={string.Join(',', link.InformativeObjectNames)}|key=driverid");
        }

        Console.WriteLine($"ReliablePercent={analysis.MatchBreakdown.ReliablePercent}");
        Console.WriteLine($"UnreliablePercent={analysis.MatchBreakdown.UnreliablePercent}");
        Console.WriteLine($"Cause={analysis.MatchBreakdown.PrimaryCause}");
        Console.WriteLine(
            $"UniqueProblemLocations={analysis.AliasProjection.UniqueProblemLocations}");
        Console.WriteLine(
            $"ConfirmableAliases={analysis.AliasProjection.UniqueConfirmableAliases}");
        Console.WriteLine(
            $"FlippablePerformances={analysis.AliasProjection.PerformancesFlippedIfAllAliasesConfirmed}");
        Console.WriteLine(
            $"PotentialReliablePercent={analysis.AliasProjection.PotentialReliablePercentAfterAliasConfirmation}");
        Console.WriteLine($"Top20Gain={analysis.AliasProjection.Top20GainPerformances}");
        Console.WriteLine($"Advice={analysis.AliasTableAdvice}");
        Console.WriteLine($"Report={markdownPath}");
        return;
    }

    var broaderMarkdown = Path.Combine(docsPath, "broader-validation-report.md");
    var broaderJson = Path.Combine(docsPath, "broader-validation-report.json");
    await File.WriteAllTextAsync(
        broaderMarkdown,
        BroaderValidationReportWriter.ToMarkdown(broader),
        Encoding.UTF8);
    await File.WriteAllTextAsync(
        broaderJson,
        BroaderValidationReportWriter.ToJson(broader),
        Encoding.UTF8);
    Console.WriteLine($"ProcessedTechnicians={broader.Summary.ProcessedTechnicianCount}");
    Console.WriteLine($"Workdays={broader.Summary.WorkdayCount}");
    Console.WriteLine($"Performances={broader.Summary.TotalPerformanceCount}");
    Console.WriteLine($"ReliableMatchPercent={broader.Summary.ReliableMatchPercent}");
    Console.WriteLine($"ConfirmedPercent={broader.Summary.ConfirmedPercent}");
    Console.WriteLine($"ProbablePercent={broader.Summary.ProbablePercent}");
    Console.WriteLine($"ManualReviewPercent={broader.Summary.ManualReviewPercent}");
    Console.WriteLine($"MissingDriverTrips={broader.Summary.MissingDriverTripCount}");
    Console.WriteLine($"PossibleHourDeviations={broader.Summary.PossibleHourDeviationCount}");
    Console.WriteLine($"IndividualDeviations={broader.Summary.IndividualToleranceDeviationCount}");
    Console.WriteLine($"HighPriorityDeviations={broader.Summary.HighPriorityToleranceDeviationCount}");
    Console.WriteLine($"Report={broaderMarkdown}");
    foreach (var skipped in broader.Summary.SkippedTechnicians)
    {
        Console.WriteLine($"Skipped={skipped}");
    }

    foreach (var problem in broader.Summary.RecurringAddressProblems)
    {
        Console.WriteLine($"AddressProblem={problem}");
    }

    return;
}

var webBuilder = WebApplication.CreateBuilder(args);

webBuilder.Logging.ClearProviders();
webBuilder.Logging.AddConsole();
webBuilder.Logging.AddDebug();
webBuilder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

var dataProtectionDirectory = Path.Combine(
    webBuilder.Environment.ContentRootPath,
    "data",
    "data-protection-keys");
Directory.CreateDirectory(dataProtectionDirectory);

webBuilder.Services.AddRazorPages();
var dataProtection = webBuilder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionDirectory))
    .SetApplicationName("TheBelgian.TimeControl");
if (OperatingSystem.IsWindows())
{
    dataProtection.ProtectKeysWithDpapi();
}

webBuilder.Services.AddTimeControlInfrastructure(webBuilder.Configuration);

var app = webBuilder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();
await app.Services.InitializeTimeControlDatabaseAsync();

app.Run();

static DateOnly ParseDate(string[] arguments, string name, DateOnly fallback)
{
    var index = Array.FindIndex(
        arguments,
        item => item.Equals(name, StringComparison.OrdinalIgnoreCase));
    if (index < 0 || index + 1 >= arguments.Length)
    {
        return fallback;
    }

    return DateOnly.TryParse(arguments[index + 1], out var parsed)
        ? parsed
        : fallback;
}

static int ParseReviewer(string[] arguments)
{
    var index = Array.FindIndex(
        arguments,
        item => item.Equals("--reviewer", StringComparison.OrdinalIgnoreCase));
    if (index < 0 || index + 1 >= arguments.Length ||
        !int.TryParse(arguments[index + 1], out var reviewer) ||
        reviewer is not (1 or 2))
    {
        throw new ArgumentException("Gebruik --reviewer 1 of --reviewer 2.");
    }

    return reviewer;
}
