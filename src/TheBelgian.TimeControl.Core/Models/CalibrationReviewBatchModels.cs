namespace TheBelgian.TimeControl.Core.Models;

public sealed class CalibrationReviewPack
{
    public required string DatasetRole { get; init; }
    public required int CaseCount { get; init; }
    public required DateTimeOffset ExportedAt { get; init; }
    public required string BlindNote { get; init; }
    public required IReadOnlyList<CalibrationReviewCase> Cases { get; init; }
}

public sealed class CalibrationReviewCase
{
    public required int CaseNumber { get; init; }
    public required long PerformanceId { get; init; }
    public required string Technician { get; init; }
    public required DateOnly Date { get; init; }
    public required DateTimeOffset PerformanceStart { get; init; }
    public required DateTimeOffset PerformanceEnd { get; init; }
    public string? Lacleunik { get; init; }
    public required string PlenionAddress { get; init; }
    public required string GeocodeQuality { get; init; }
    public string? ActivityType { get; init; }
    public string? PreviousPerformance { get; init; }
    public string? NextPerformance { get; init; }
    public required string LocationExposure { get; init; }
    public required IReadOnlyList<CalibrationReviewCandidate> Candidates { get; init; }
}

public sealed class CalibrationReviewCandidate
{
    public required string StopId { get; init; }
    public string? Address { get; init; }
    public double? DistanceMeters { get; init; }
    public required DateTimeOffset Arrival { get; init; }
    public required DateTimeOffset Departure { get; init; }
    public required int DwellMinutes { get; init; }
    public required int OverlapMinutes { get; init; }
    public required double OverlapPercent { get; init; }
    public required int StartDifferenceMinutes { get; init; }
    public required int EndDifferenceMinutes { get; init; }
}

public sealed class CalibrationLabelEntry
{
    public required long PerformanceId { get; init; }
    public string? Label { get; init; }
    public string? ExpectedStopId { get; init; }
    public string? ReviewerConfidence { get; init; }
    public string? ReviewerNote { get; init; }
}

public sealed class CalibrationLabelImportResult
{
    public required int Reviewer { get; init; }
    public required int ImportedCount { get; init; }
    public required string LabelsPath { get; init; }
    public required string CalibrationPath { get; init; }
    public required BenchmarkLabelAgreement Agreement { get; init; }
}

public sealed class CalibrationExportResult
{
    public required string MarkdownPath { get; init; }
    public required string JsonPath { get; init; }
    public required string TemplatePath { get; init; }
    public required int CaseCount { get; init; }
}
