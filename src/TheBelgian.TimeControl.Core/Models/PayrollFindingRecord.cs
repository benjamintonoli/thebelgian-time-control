using TheBelgian.TimeControl.Core.Payroll.Findings;

namespace TheBelgian.TimeControl.Core.Models;

public sealed class PayrollFindingRecord
{
    public int Id { get; set; }
    public int ShadowMonthId { get; set; }
    public string FindingKey { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public PayrollFindingType FindingType { get; set; }
    public PayrollFindingSeverity Severity { get; set; }
    public PayrollFindingStatus Status { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Evidence { get; set; } = string.Empty;
    public string SuggestedAction { get; set; } = string.Empty;
    public string RelatedPerformanceIdsJson { get; set; } = "[]";
    public decimal? PlannedHours { get; set; }
    public decimal? BookedHours { get; set; }
    public decimal? OverlapHours { get; set; }
    public decimal? SuggestedOvertimeAdjustmentHours { get; set; }
    public decimal? LegacyDifferenceHours { get; set; }
    public DateTimeOffset? SuggestedPayableStart { get; set; }
    public DateTimeOffset? SuggestedPayableEnd { get; set; }
    public decimal? SuggestedPayableHours { get; set; }
    public string? SuggestedProjectId { get; set; }
    public string? SuggestedBonNr { get; set; }
    public string? GpsClassification { get; set; }
}
