using TheBelgian.TimeControl.Core.Payroll.Actions;

namespace TheBelgian.TimeControl.Core.Models;

public sealed class PayrollProposedActionRecord
{
    public int Id { get; set; }
    public Guid ActionId { get; set; }
    public int ShadowMonthId { get; set; }
    public string FindingKey { get; set; } = string.Empty;
    public int? FindingId { get; set; }
    public string ResourceId { get; set; } = string.Empty;
    public PayrollProposedActionType ActionType { get; set; }
    public PayrollProposedActionStatus Status { get; set; }
    public string? BlockReason { get; set; }
    public string EvidenceSnapshotJson { get; set; } = "{}";
    public string ProposalSnapshotJson { get; set; } = "{}";
    public string SourceRevision { get; set; } = string.Empty;
    public string? PwsReference { get; set; }
    public long? ResultPerformanceId { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset? ApprovedAtUtc { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ExecutedAtUtc { get; set; }
    public string? ExecutedBy { get; set; }
    public string? ExecutionResult { get; set; }
    public DateTimeOffset? UpdatedAtUtc { get; set; }
}
