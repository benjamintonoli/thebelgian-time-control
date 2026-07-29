using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Web.Pages.Pilot;

public sealed class BenchmarkReviewModel(
    IWebHostEnvironment environment,
    ILogger<BenchmarkReviewModel> logger) : PageModel
{
    private static readonly string[] AllowedLabels =
    [
        "CorrectCandidate",
        "NoValidCandidate",
        "Ambiguous",
    ];

    private static readonly string[] ConfidenceLevels =
    [
        "High",
        "Medium",
        "Low",
    ];

    [BindProperty]
    public long PerformanceId { get; set; }

    [BindProperty]
    [Required]
    public string Label { get; set; } = string.Empty;

    [BindProperty]
    public string? ExpectedStopId { get; set; }

    [BindProperty]
    [Required]
    public string ReviewerConfidence { get; set; } = "Medium";

    [BindProperty]
    public string? ReviewerNote { get; set; }

    [BindProperty]
    public bool SecondPass { get; set; }

    public IReadOnlyList<LocationMatchingBenchmarkCase> Queue { get; private set; } = [];
    public LocationMatchingBenchmarkCase? Current { get; private set; }
    public int RemainingCount { get; private set; }
    public int LabeledCount { get; private set; }
    public int DoubleLabeledCount { get; private set; }
    public int DisagreementCount { get; private set; }
    public BenchmarkLabelAgreement? Agreement { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? InfoMessage { get; private set; }
    public string DocsPath { get; private set; } = string.Empty;

    public IActionResult OnGet(bool secondPass = false)
    {
        if (!environment.IsDevelopment())
        {
            return NotFound();
        }

        SecondPass = secondPass;
        LoadQueue();
        return Page();
    }

    public IActionResult OnPost()
    {
        if (!environment.IsDevelopment())
        {
            return NotFound();
        }

        DocsPath = ResolveDocsPath();
        var cases = LocationMatchingBenchmarkService.LoadCalibrationCases(DocsPath).ToList();
        var index = cases.FindIndex(item => item.PerformanceId == PerformanceId);
        if (index < 0)
        {
            ErrorMessage = "Case niet gevonden in de 30-case kalibratieset.";
            LoadQueue(cases);
            return Page();
        }

        if (!AllowedLabels.Contains(Label, StringComparer.Ordinal))
        {
            ErrorMessage = "Ongeldig label.";
            LoadQueue(cases);
            return Page();
        }

        if (!ConfidenceLevels.Contains(ReviewerConfidence, StringComparer.Ordinal))
        {
            ErrorMessage = "Ongeldige reviewer confidence.";
            LoadQueue(cases);
            return Page();
        }

        if (string.Equals(Label, "CorrectCandidate", StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(ExpectedStopId))
        {
            ErrorMessage = "CorrectCandidate vereist ExpectedStopId.";
            LoadQueue(cases);
            return Page();
        }

        if ((string.Equals(Label, "NoValidCandidate", StringComparison.Ordinal) ||
             string.Equals(Label, "Ambiguous", StringComparison.Ordinal)) &&
            !string.IsNullOrWhiteSpace(ExpectedStopId))
        {
            ErrorMessage = "NoValidCandidate en Ambiguous vereisen ExpectedStopId = null.";
            LoadQueue(cases);
            return Page();
        }

        var stopId = string.IsNullOrWhiteSpace(ExpectedStopId) ? null : ExpectedStopId.Trim();
        var current = cases[index];
        if (SecondPass)
        {
            var adjudication = string.Equals(current.Label, Label, StringComparison.Ordinal) &&
                               string.Equals(current.ExpectedStopId, stopId, StringComparison.Ordinal)
                ? "Agree"
                : "Disagreement";
            cases[index] = current with
            {
                SecondReviewLabel = Label,
                SecondReviewExpectedStopId = stopId,
                SecondReviewerConfidence = ReviewerConfidence,
                SecondReviewerNote = string.IsNullOrWhiteSpace(ReviewerNote) ? null : ReviewerNote.Trim(),
                AdjudicationStatus = adjudication,
                RequiresSecondReview = true,
                IsCalibrationCase = true,
            };
        }
        else
        {
            cases[index] = current with
            {
                Label = Label,
                ExpectedStopId = stopId,
                ReviewerConfidence = ReviewerConfidence,
                ReviewerNote = string.IsNullOrWhiteSpace(ReviewerNote) ? null : ReviewerNote.Trim(),
                RequiresSecondReview = true,
                IsCalibrationCase = true,
                AdjudicationStatus = !string.IsNullOrWhiteSpace(current.SecondReviewLabel)
                    ? string.Equals(Label, current.SecondReviewLabel, StringComparison.Ordinal) &&
                      string.Equals(stopId, current.SecondReviewExpectedStopId, StringComparison.Ordinal)
                        ? "Agree"
                        : "Disagreement"
                    : current.AdjudicationStatus,
            };
        }

        try
        {
            LocationMatchingBenchmarkService.SaveCalibrationAndDevelopmentCases(DocsPath, cases);
            Agreement = LocationMatchingBenchmarkSampling.ComputeLabelAgreement(
                LocationMatchingBenchmarkService.LoadCalibrationCases(DocsPath));
            InfoMessage = SecondPass
                ? $"Tweede review opgeslagen ({cases[index].AdjudicationStatus})."
                : "Kalibratielabel opgeslagen.";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Kon kalibratielabels niet opslaan.");
            ErrorMessage = exception.Message;
        }

        Label = string.Empty;
        ExpectedStopId = null;
        ReviewerNote = null;
        ReviewerConfidence = "Medium";
        LoadQueue();
        return Page();
    }

    private void LoadQueue(IReadOnlyList<LocationMatchingBenchmarkCase>? loaded = null)
    {
        DocsPath = ResolveDocsPath();
        var cases = loaded?.ToList() ??
                    LocationMatchingBenchmarkService.LoadCalibrationCases(DocsPath).ToList();
        LabeledCount = cases.Count(item => !string.IsNullOrWhiteSpace(item.Label));
        DoubleLabeledCount = cases.Count(item =>
            !string.IsNullOrWhiteSpace(item.Label) &&
            !string.IsNullOrWhiteSpace(item.SecondReviewLabel));
        DisagreementCount = cases.Count(item =>
            string.Equals(item.AdjudicationStatus, "Disagreement", StringComparison.Ordinal));
        Agreement = LocationMatchingBenchmarkSampling.ComputeLabelAgreement(cases);
        var ordered = LocationMatchingBenchmarkSampling.BlindReviewOrder(cases);
        Queue = SecondPass
            ? ordered
                .Where(item => string.IsNullOrWhiteSpace(item.SecondReviewLabel))
                .ToArray()
            : ordered
                .Where(item => string.IsNullOrWhiteSpace(item.Label))
                .ToArray();
        RemainingCount = Queue.Count;
        Current = Queue.Count > 0 ? Queue[0] : null;
        PerformanceId = Current?.PerformanceId ?? 0;
    }

    private string ResolveDocsPath() =>
        Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "..", "docs"));
}
