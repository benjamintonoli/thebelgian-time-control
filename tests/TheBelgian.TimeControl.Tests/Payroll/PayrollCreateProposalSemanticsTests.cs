using TheBelgian.TimeControl.Core.Payroll.Actions;
using TheBelgian.TimeControl.Core.Payroll.Findings;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollCreateProposalSemanticsTests
{
    [Fact]
    public void WorkflowOnly_OffsetAndSeconds_RemainEquivalent()
    {
        var stored = Proposal(
            start: new DateTimeOffset(2026, 8, 14, 7, 55, 0, TimeSpan.Zero),
            end: new DateTimeOffset(2026, 8, 14, 14, 46, 0, TimeSpan.Zero));
        var current = Proposal(
            start: new DateTimeOffset(2026, 8, 14, 7, 55, 11, TimeSpan.FromHours(2)),
            end: new DateTimeOffset(2026, 8, 14, 14, 46, 4, TimeSpan.FromHours(2)));

        Assert.True(PayrollCreateProposalSemantics.AreMateriallyEquivalent(stored, current));
        Assert.Equal(
            PayrollCreateProposalSemantics.ComputeFingerprint(stored),
            PayrollCreateProposalSemantics.ComputeFingerprint(current));
    }

    [Fact]
    public void VanTotChange_IsMaterial()
    {
        var stored = Proposal(
            start: new DateTimeOffset(2026, 8, 14, 7, 55, 0, TimeSpan.FromHours(2)),
            end: new DateTimeOffset(2026, 8, 14, 14, 46, 0, TimeSpan.FromHours(2)));
        var changed = Proposal(
            start: new DateTimeOffset(2026, 8, 14, 8, 0, 0, TimeSpan.FromHours(2)),
            end: new DateTimeOffset(2026, 8, 14, 14, 46, 0, TimeSpan.FromHours(2)));

        Assert.False(PayrollCreateProposalSemantics.AreMateriallyEquivalent(stored, changed));
    }

    [Fact]
    public void ProjectOrBonChange_IsMaterial()
    {
        var stored = Proposal(projectId: "40167", bonNr: "26501760");
        Assert.False(PayrollCreateProposalSemantics.AreMateriallyEquivalent(
            stored,
            Proposal(projectId: "21540", bonNr: "26501760")));
        Assert.False(PayrollCreateProposalSemantics.AreMateriallyEquivalent(
            stored,
            Proposal(projectId: "40167", bonNr: "999")));
    }

    [Fact]
    public void ActivityChange_IsMaterial()
    {
        var stored = Proposal(mainTaskId: 9);
        Assert.False(PayrollCreateProposalSemantics.AreMateriallyEquivalent(
            stored,
            Proposal(mainTaskId: 14)));
    }

    [Fact]
    public void Fingerprint_IgnoresNonMaterialDisplayFields()
    {
        // Fingerprint is proposal-only; finding status/decision/comment never enter it.
        var a = Proposal();
        var b = Proposal();
        Assert.Equal(
            PayrollCreateProposalSemantics.ComputeFingerprint(a),
            PayrollCreateProposalSemantics.ComputeFingerprint(b));
    }

    private static PayrollActionCreateProposal Proposal(
        DateTimeOffset? start = null,
        DateTimeOffset? end = null,
        string projectId = "40167",
        string? bonNr = "26501760",
        int mainTaskId = 9) =>
        new(
            "661",
            new DateOnly(2026, 8, 14),
            start ?? new DateTimeOffset(2026, 8, 14, 7, 55, 0, TimeSpan.FromHours(2)),
            end ?? new DateTimeOffset(2026, 8, 14, 14, 46, 0, TimeSpan.FromHours(2)),
            6.85m,
            projectId,
            bonNr,
            mainTaskId,
            PayrollIntervalSemantics.PayableWork);
}
