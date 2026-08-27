using Microsoft.Extensions.DependencyInjection;

namespace TheBelgian.TimeControl.Infrastructure.VehicleAssignments;

public sealed class HistoricalVehicleCandidateCache(IServiceScopeFactory scopeFactory) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HistoricalVehicleCandidateResult? _cached;

    public async Task<HistoricalVehicleCandidateResult> GetAsync(
        bool refresh,
        CancellationToken cancellationToken)
    {
        if (!refresh && _cached is not null) return _cached;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!refresh && _cached is not null) return _cached;
            await using var scope = scopeFactory.CreateAsyncScope();
            _cached = await scope.ServiceProvider
                .GetRequiredService<HistoricalVehicleAssignmentCandidateService>()
                .GenerateAsync(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31),
                    cancellationToken);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate() => _cached = null;

    public void MarkConfirmed(IReadOnlyCollection<string> candidateKeys)
    {
        if (_cached is null) return;
        var candidates = _cached.Candidates.Select(item =>
            candidateKeys.Contains(item.CandidateKey)
                ? item with
                {
                    Status = HistoricalVehicleCandidateStatus.AlreadyConfirmed,
                    Evidence = item.Evidence.Concat(
                        ["Historische assignment expliciet door admin bevestigd."]).ToArray(),
                }
                : item).ToArray();
        _cached = _cached with
        {
            AlreadyConfirmed = candidates.Count(item =>
                item.Status == HistoricalVehicleCandidateStatus.AlreadyConfirmed),
            HighConfidenceCandidate = candidates.Count(item =>
                item.Status == HistoricalVehicleCandidateStatus.HighConfidenceCandidate),
            TransferSuspected = candidates.Count(item =>
                item.Status == HistoricalVehicleCandidateStatus.TransferSuspected),
            MultipleCandidates = candidates.Count(item =>
                item.Status == HistoricalVehicleCandidateStatus.MultipleCandidates),
            NoCandidate = candidates.Count(item =>
                item.Status == HistoricalVehicleCandidateStatus.NoCandidate),
            TheoreticallyAuditableDaysAfterHighConfidenceConfirmation = candidates
                .Where(item => item.Status == HistoricalVehicleCandidateStatus.HighConfidenceCandidate)
                .Sum(item => item.AuditableTechnicianDays),
            Candidates = candidates,
        };
    }

    public void MarkTechnicianConfirmed(string technicianCode)
    {
        if (_cached is null) return;
        var keys = _cached.Candidates.Where(item => item.TechnicianCode.Equals(
                technicianCode, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.CandidateKey)
            .ToArray();
        MarkConfirmed(keys);
    }

    public void Dispose() => _gate.Dispose();
}
