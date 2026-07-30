namespace TheBelgian.TimeControl.Core.Models;

public sealed class LockedHoldoutReviewPack
{
    public required string DatasetRole { get; init; }
    public required int CaseCount { get; init; }
    public required DateTimeOffset ExportedAt { get; init; }
    public required string BlindNote { get; init; }
    public required string HoldoutContentSha256 { get; init; }
    public required IReadOnlyList<LockedHoldoutReviewCase> Cases { get; init; }
}

public sealed class LockedHoldoutReviewCase
{
    public required int CaseNumber { get; init; }
    public required long PerformanceId { get; init; }
    public required string Technician { get; init; }
    public required DateOnly Date { get; init; }
    public required DateTimeOffset PerformanceStart { get; init; }
    public required DateTimeOffset PerformanceEnd { get; init; }
    public string? Lacleunik { get; init; }
    public required string PlenionAddress { get; init; }
    public string? PreviousPerformance { get; init; }
    public string? NextPerformance { get; init; }
    public required IReadOnlyList<LockedHoldoutReviewCandidate> Candidates { get; init; }
    public required IReadOnlyList<LockedHoldoutReviewVisitGroup> PossibleVisitGroups { get; init; }
}

public sealed class LockedHoldoutReviewCandidate
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

public sealed class LockedHoldoutReviewVisitGroup
{
    public required IReadOnlyList<string> ConstituentStopIds { get; init; }
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

public sealed class LockedHoldoutExportResult
{
    public required string MarkdownPath { get; init; }
    public required string LabelsPath { get; init; }
    public required int CaseCount { get; init; }
    public required string HoldoutContentSha256 { get; init; }
}
