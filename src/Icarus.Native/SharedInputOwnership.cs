using Icarus.Core;

namespace Icarus.Native;

/// <summary>
/// Cross-process input ownership backed by the shared lease.
///
/// The engine records an input before pressing it and forgets it after the key-up, so the
/// watchdog can always reconstruct exactly what is physically down. Every method is
/// defensive about the mapping: a failure to record is reported rather than thrown, so the
/// engine refuses to press an untracked key instead of stranding it.
/// </summary>
public sealed class SharedInputOwnership : IInputOwnership, IDisposable
{
    private readonly SharedLease lease;
    private long generation;

    public SharedInputOwnership(SharedLease lease)
    {
        this.lease = lease ?? throw new ArgumentNullException(nameof(lease));
    }

    /// <summary>True when cross-process protection is active.</summary>
    public bool IsActive => true;

    public void Claim()
    {
        generation = Environment.TickCount64;
        lease.Claim(Environment.ProcessId, generation);
    }

    public bool Record(InputToken input)
    {
        try
        {
            return lease.Add(ToOwned(input));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // Reporting failure is the whole point: the engine must not press a key it
            // could not record, because nothing would ever release it.
            return false;
        }
    }

    public void Forget(InputToken input)
    {
        try { lease.Remove(ToOwned(input)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    public void Pulse()
    {
        try { lease.Pulse(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    public void RequestRelease()
    {
        try { lease.RequestRelease(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    private static OwnedInput ToOwned(InputToken t) => new(t.Mouse, t.Code, t.Extended);

    public void Dispose() => lease.Dispose();
}
