using System.Security.Cryptography;
using System.Text;

namespace TheBelgian.TimeControl.Infrastructure.VehicleAssignments;

internal sealed class VehicleAssignmentSyncExecutionGuard : IDisposable
{
    private readonly Semaphore _semaphore;
    private bool _ownsSlot;

    private VehicleAssignmentSyncExecutionGuard(Semaphore semaphore, bool ownsSlot)
    {
        _semaphore = semaphore;
        _ownsSlot = ownsSlot;
    }

    public bool Acquired => _ownsSlot;

    public static VehicleAssignmentSyncExecutionGuard TryAcquire(string databasePath)
    {
        var normalized = Path.GetFullPath(databasePath).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..24];
        var semaphore = new Semaphore(
            1, 1, $"Global\\TheBelgian.TimeControl.VehicleAssignmentSync.{hash}");
        return new(semaphore, semaphore.WaitOne(TimeSpan.Zero));
    }

    public void Dispose()
    {
        if (_ownsSlot)
        {
            _semaphore.Release();
            _ownsSlot = false;
        }
        _semaphore.Dispose();
    }
}
