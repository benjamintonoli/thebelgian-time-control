using Microsoft.EntityFrameworkCore;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Persistence;

namespace TheBelgian.TimeControl.Infrastructure.VehicleAssignments;

public sealed record HistoricalVehicleTransferRequest(
    string TechnicianCode,
    string PreviousObjectId,
    string NewObjectId,
    DateOnly TransferDate,
    DateOnly MonthFrom,
    DateOnly MonthThrough,
    string Reviewer,
    string Evidence);

public sealed class HistoricalVehicleAssignmentWorkflowService(
    HistoricalVehicleCandidateCache candidateCache,
    TechnicianVehicleAssignmentBackfillService backfillService,
    IDbContextFactory<TimeControlDbContext> contextFactory,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<TechnicianVehicleAssignment>> ConfirmCandidatesAsync(
        IReadOnlyList<string> candidateKeys,
        string reviewer,
        bool bulk,
        CancellationToken cancellationToken)
    {
        ValidateReviewer(reviewer);
        var snapshot = await candidateCache.GetAsync(false, cancellationToken);
        var selected = snapshot.Candidates.Where(item =>
                candidateKeys.Contains(item.CandidateKey, StringComparer.Ordinal))
            .ToArray();
        if (selected.Length != candidateKeys.Distinct(StringComparer.Ordinal).Count())
            throw new InvalidOperationException("Een geselecteerde kandidaat bestaat niet meer; vernieuw de pagina.");
        if (bulk && selected.Any(item =>
                item.Status != HistoricalVehicleCandidateStatus.HighConfidenceCandidate))
            throw new InvalidOperationException(
                "Bulkbevestiging is uitsluitend toegestaan voor HighConfidenceCandidate.");
        if (selected.Any(item => string.IsNullOrWhiteSpace(item.ProposedObjectId)))
            throw new InvalidOperationException("Minstens één kandidaat heeft geen ObjectId.");
        var requests = selected.Select(item => Confirmation(
            item.TechnicianCode,
            item.ProposedObjectId!,
            item.From,
            item.Through,
            reviewer,
            string.Join(" | ", item.Evidence))).ToArray();
        var result = await backfillService.RegisterManyAsync(requests, cancellationToken);
        candidateCache.MarkConfirmed(candidateKeys);
        return result;
    }

    public async Task<IReadOnlyList<TechnicianVehicleAssignment>> ConfirmCustomAsync(
        string technicianCode,
        string objectId,
        DateOnly from,
        DateOnly through,
        string reviewer,
        string evidence,
        CancellationToken cancellationToken)
    {
        ValidateReviewer(reviewer);
        if (through < from) throw new ArgumentException("Einddatum ligt vóór begindatum.");
        var result = await backfillService.RegisterManyAsync(
            [Confirmation(technicianCode, objectId, from, through, reviewer, evidence)],
            cancellationToken);
        candidateCache.MarkTechnicianConfirmed(technicianCode);
        return result;
    }

    public async Task<IReadOnlyList<TechnicianVehicleAssignment>> RegisterTransferAsync(
        HistoricalVehicleTransferRequest request,
        CancellationToken cancellationToken)
    {
        ValidateReviewer(request.Reviewer);
        if (request.TransferDate <= request.MonthFrom || request.TransferDate > request.MonthThrough)
            throw new ArgumentException("Transferdatum moet binnen de maand en na de begindatum liggen.");
        var firstEvidence = $"Bevestigde transfer vóór {request.TransferDate:dd/MM/yyyy}. {request.Evidence}";
        var secondEvidence = $"Bevestigde transfer vanaf {request.TransferDate:dd/MM/yyyy}. {request.Evidence}";
        var result = await backfillService.RegisterManyAsync(
            [
                Confirmation(request.TechnicianCode, request.PreviousObjectId,
                    request.MonthFrom, request.TransferDate.AddDays(-1), request.Reviewer, firstEvidence),
                Confirmation(request.TechnicianCode, request.NewObjectId,
                    request.TransferDate, request.MonthThrough, request.Reviewer, secondEvidence),
            ],
            cancellationToken);
        candidateCache.MarkTechnicianConfirmed(request.TechnicianCode);
        return result;
    }

    public async Task RecordInsufficientInformationAsync(
        string candidateKey,
        string technicianCode,
        string reviewer,
        string note,
        CancellationToken cancellationToken)
    {
        ValidateReviewer(reviewer);
        var now = timeProvider.GetUtcNow();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.TechnicianVehicleAssignmentAudits.Add(new TechnicianVehicleAssignmentAudit
        {
            Action = "HistoricalCandidateInsufficientInformation",
            Actor = reviewer.Trim(),
            Source = "HistoricalInitializationReview",
            ChangedAt = now,
            EvidenceReference = $"Candidate={candidateKey};RESCODE={technicianCode};Note={note}",
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    private static VehicleAssignmentBackfillRequest Confirmation(
        string technicianCode,
        string objectId,
        DateOnly from,
        DateOnly through,
        string reviewer,
        string evidence) => new(
        technicianCode,
        objectId,
        LocalStart(from),
        LocalStart(through.AddDays(1)),
        "HistoricalAdminConfirmation",
        evidence,
        reviewer.Trim());

    private static DateTimeOffset LocalStart(DateOnly date) =>
        new(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.FromHours(2));

    private static void ValidateReviewer(string reviewer)
    {
        if (string.IsNullOrWhiteSpace(reviewer))
            throw new ArgumentException("Reviewer is verplicht.");
    }
}
