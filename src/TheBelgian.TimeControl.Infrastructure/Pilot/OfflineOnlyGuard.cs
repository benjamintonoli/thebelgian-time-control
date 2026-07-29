namespace TheBelgian.TimeControl.Infrastructure.Pilot;

/// <summary>
/// Blocks Plenion/Powerfleet/Geoapify/ODBC access while frozen offline verification runs.
/// Scoped via AsyncLocal so parallel tests are not affected.
/// </summary>
internal static class OfflineOnlyGuard
{
    private static readonly AsyncLocal<int> Active = new();

    public static bool IsActive => Active.Value > 0;

    public static IDisposable Enter()
    {
        Active.Value++;
        return new Scope();
    }

    public static void EnsureLiveAccessAllowed(string provider)
    {
        if (!IsActive)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Live databron '{provider}' mag niet worden geïnitialiseerd tijdens offline frozen-matcher verificatie.");
    }

    private sealed class Scope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (Active.Value > 0)
            {
                Active.Value--;
            }
        }
    }
}
