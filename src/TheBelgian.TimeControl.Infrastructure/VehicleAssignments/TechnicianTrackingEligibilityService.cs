using Microsoft.EntityFrameworkCore;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Persistence;

namespace TheBelgian.TimeControl.Infrastructure.VehicleAssignments;

internal sealed class TechnicianTrackingEligibilityService(
    IDbContextFactory<TimeControlDbContext> contextFactory,
    IPlenionReader plenionReader,
    TimeProvider timeProvider)
{
    public async Task<TechnicianTrackingEligibility?> ResolveAsync(
        string technicianExternalId,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var records = await context.TechnicianTrackingEligibilities.AsNoTracking()
            .Where(item => item.TechnicianExternalId == technicianExternalId)
            .ToArrayAsync(cancellationToken);
        var valid = records.Where(item => item.IsValidAt(at)).ToArray();
        if (valid.Length > 1)
            throw new InvalidOperationException(
                $"Overlappende tracking-eligibility voor technieker {technicianExternalId}.");
        return valid.SingleOrDefault();
    }

    public async Task<IReadOnlyList<TechnicianTrackingEligibility>> RegisterNoTrackAndTraceAsync(
        IReadOnlyList<string> technicianCodes,
        DateTimeOffset validFrom,
        string reason,
        string source,
        string createdBy,
        CancellationToken cancellationToken)
    {
        if (technicianCodes.Count == 0) throw new ArgumentException("Geen techniekercodes opgegeven.");
        if (string.IsNullOrWhiteSpace(reason) || string.IsNullOrWhiteSpace(source) ||
            string.IsNullOrWhiteSpace(createdBy))
            throw new ArgumentException("Reason, Source en CreatedBy zijn verplicht.");
        var technicians = await plenionReader.GetTechniciansAsync(cancellationToken);
        var mapped = technicianCodes.Distinct(StringComparer.OrdinalIgnoreCase).Select(code =>
        {
            var matches = TechnicianVehicleAssignmentBackfillService.ExactCodeMatches(technicians, code);
            if (matches.Length != 1)
                throw new InvalidOperationException(
                    matches.Length == 0
                        ? $"TechnicianCode {code} bestaat niet exact in RESOURCE."
                        : $"AmbiguousTechnicianCode: {code} is niet uniek.");
            return matches[0];
        }).ToArray();

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var externalIds = mapped.Select(item => item.ExternalId).ToArray();
        var existing = await context.TechnicianTrackingEligibilities.AsNoTracking()
            .Where(item => externalIds.Contains(item.TechnicianExternalId))
            .ToArrayAsync(cancellationToken);
        if (existing.Any(item => item.ValidTo is null || validFrom < item.ValidTo))
            throw new InvalidOperationException(
                "TrackingEligibilityOverlap: minstens één bestaande status; niets overschreven.");
        var now = timeProvider.GetUtcNow();
        var records = mapped.Select(item => new TechnicianTrackingEligibility
        {
            TechnicianExternalId = item.ExternalId,
            TechnicianCode = TechnicianVehicleAssignmentBackfillService.NormalizeCode(item.Code),
            TrackingStatus = TechnicianTrackingStatus.NoTrackAndTrace,
            Reason = reason.Trim(),
            Source = source.Trim(),
            ValidFrom = validFrom,
            CreatedAt = now,
            CreatedBy = createdBy.Trim(),
        }).ToArray();
        context.TechnicianTrackingEligibilities.AddRange(records);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return records;
    }
}
