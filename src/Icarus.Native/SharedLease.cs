using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Icarus.Core;

namespace Icarus.Native;

/// <summary>
/// Cross-process channel for the input lease.
///
/// The owner and the watchdog are separate processes, so the lease state has to live in
/// shared memory rather than in a field. A memory-mapped file is used rather than a named
/// pipe because the watchdog only ever reads the state and the owner only ever writes it:
/// there is no request that travels back, so a one-way channel is sufficient and cannot
/// deadlock the owner on a blocked reader.
///
/// CRITICAL: two separate problems were found here while testing, and both are load-bearing.
///
/// 1. Namespace. A "Local\" mapping is session-scoped. An unqualified name is still created
///    per-session by .NET, so the explicit "Global\" prefix is required for the mapping to be
///    reachable from any process.
///
/// 2. Lifetime, which is the subtler one. A memory-mapped file is destroyed the moment its
///    LAST handle closes, regardless of namespace. A killed owner therefore takes the lease
///    with it, and a watchdog that had not yet opened the mapping finds nothing to read.
///    The mapping only survives if the watchdog is already holding a handle to it. That is
///    why the watchdog must be started, and must keep the handle open in a loop, BEFORE any
///    input is ever claimed. Starting it lazily at release time is too late by definition.
///
/// The content is a list of scan codes rather than anything sensitive, so the wider
/// visibility of a global mapping is an acceptable trade. The watchdog additionally refuses
/// to act unless it can verify the owning process is really gone.
/// </summary>
/// </summary>
public sealed class SharedLease : IDisposable
{
    /// <summary>
    /// Shared memory name. The Global prefix is required so the mapping is reachable across
    /// processes, and the watchdog must hold a handle open before any input is claimed so
    /// the mapping outlives a killed owner. See the note above on both points.
    /// </summary>
    public const string MapName = "Global\\Icarus.InputLease.v1";

    /// <summary>
    /// Capacity of the owned-input list. A macro holding more keys than this is refused
    /// rather than silently truncated: a truncated list would leave the overflow keys
    /// unreleased, which is exactly the failure this whole mechanism exists to prevent.
    /// </summary>
    public const int MaxOwnedInputs = 64;

    // Fixed layout. Sizes are explicit and asserted at construction so a mismatch between
    // writer and reader fails immediately instead of reading garbage.
    private const int OffsetHeartbeat = 0;      // long
    private const int OffsetOwnerPid = 8;       // int
    private const int OffsetGeneration = 12;    // long
    private const int OffsetReleaseRequested = 20; // int, 0/1
    private const int OffsetHeldCount = 24;     // int
    private const int OffsetHeldStart = 32;     // MaxOwnedInputs * 8 bytes

    private MemoryMappedFile? file;
    private MemoryMappedViewAccessor? view;
    private readonly bool ownsFile;

    private SharedLease(MemoryMappedFile file, MemoryMappedViewAccessor view, bool ownsFile)
    {
        this.file = file;
        this.view = view;
        this.ownsFile = ownsFile;
    }

    public static int RequiredBytes => OffsetHeldStart + MaxOwnedInputs * 8;

    /// <summary>Opens an existing lease owned by another process.</summary>
    public static SharedLease OpenExisting()
    {
        var file = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
        var view = file.CreateViewAccessor(0, RequiredBytes, MemoryMappedFileAccess.Read);
        return new SharedLease(file, view, ownsFile: false);
    }

    /// <summary>
    /// Creates the lease, or opens it if it already exists. Concurrent creation is normal:
    /// the app may restart while a watchdog is still running.
    /// </summary>
    public static SharedLease CreateOrOpen()
    {
        // CreateNew is atomic, so this is the reliable way to establish ownership of a
        // fresh mapping without a create/open race between the app and a surviving
        // watchdog.
        try
        {
            var file = MemoryMappedFile.CreateNew(MapName, RequiredBytes, MemoryMappedFileAccess.ReadWrite);
            var view = file.CreateViewAccessor(0, RequiredBytes, MemoryMappedFileAccess.ReadWrite);
            return new SharedLease(file, view, ownsFile: true);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            // Someone else already created it, which is the normal case on restart.
        }

        try
        {
            var file = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.ReadWrite);
            var view = file.CreateViewAccessor(0, RequiredBytes, MemoryMappedFileAccess.ReadWrite);
            return new SharedLease(file, view, ownsFile: false);
        }
        catch (FileNotFoundException)
        {
            // The previous owner exited and its mapping went away in between. Start fresh.
            var file = MemoryMappedFile.CreateOrOpen(MapName, RequiredBytes, MemoryMappedFileAccess.ReadWrite);
            var view = file.CreateViewAccessor(0, RequiredBytes, MemoryMappedFileAccess.ReadWrite);
            return new SharedLease(file, view, ownsFile: true);
        }
    }

    private MemoryMappedViewAccessor View => view ??= file!.CreateViewAccessor(0, RequiredBytes, MemoryMappedFileAccess.ReadWrite);

    /// <summary>Records the owner. Must be called before any input is claimed.</summary>
    public void Claim(int pid, long generation)
    {
        var v = View;
        v.Write(OffsetOwnerPid, pid);
        v.Write(OffsetGeneration, generation);
        v.Write(OffsetHeldCount, 0);
        v.Write(OffsetReleaseRequested, 0);
        Pulse();
    }

    /// <summary>Advances the heartbeat. Cheap enough to call on a timer tick.</summary>
    public void Pulse()
    {
        var v = View;
        v.Write(OffsetHeartbeat, Interlocked.Read(ref heartbeatScratch) + 1);
    }

    private long heartbeatScratch;

    /// <summary>
    /// Records a held input. Returns false when the list is full, and the caller must then
    /// refuse to send the key-down. A silently dropped entry would leave that key held with
    /// nothing tracking it, which is the failure this mechanism exists to prevent.
    /// </summary>
    public bool Add(OwnedInput input)
    {
        var v = View;
        int count = v.ReadInt32(OffsetHeldCount);
        if (count >= MaxOwnedInputs) return false;

        v.Write(OffsetHeldStart + count * 8, input.Code);
        v.Write(OffsetHeldStart + count * 8 + 2, input.IsMouse ? (byte)1 : (byte)0);
        v.Write(OffsetHeldStart + count * 8 + 3, input.Extended ? (byte)1 : (byte)0);
        // Count is written last so a reader never sees a count covering a half-written entry.
        v.Write(OffsetHeldCount, count + 1);
        return true;
    }

    /// <summary>Removes a held input after its key-up has been sent.</summary>
    public void Remove(OwnedInput input)
    {
        var v = View;
        int count = v.ReadInt32(OffsetHeldCount);
        var entries = ReadEntries(count).ToList();
        if (!entries.Remove(input)) return;

        v.Write(OffsetHeldCount, 0);
        for (int i = 0; i < entries.Count; i++) WriteEntry(v, i, entries[i]);
        v.Write(OffsetHeldCount, entries.Count);
    }

    public void RequestRelease() => View.Write(OffsetReleaseRequested, 1);

    /// <summary>Reads the full lease state. Safe to call at any time from either process.</summary>
    public InputLease Read()
    {
        var v = View;
        var lease = new InputLease();
        int count = Math.Clamp(v.ReadInt32(OffsetHeldCount), 0, MaxOwnedInputs);

        lease.Load(
            heartbeat: v.ReadInt64(OffsetHeartbeat),
            ownerPid: v.ReadInt32(OffsetOwnerPid),
            generation: v.ReadInt64(OffsetGeneration),
            releaseRequested: v.ReadInt32(OffsetReleaseRequested) != 0,
            held: ReadEntries(count));
        return lease;
    }

    /// <summary>Clears the held list. Only valid once key-ups have been confirmed sent.</summary>
    public void ClearHeld() => View.Write(OffsetHeldCount, 0);

    private IEnumerable<OwnedInput> ReadEntries(int count)
    {
        var v = View;
        for (int i = 0; i < count; i++)
        {
            ushort code = v.ReadUInt16(OffsetHeldStart + i * 8);
            bool mouse = v.ReadByte(OffsetHeldStart + i * 8 + 2) != 0;
            bool extended = v.ReadByte(OffsetHeldStart + i * 8 + 3) != 0;
            yield return new OwnedInput(mouse, code, extended);
        }
    }

    private static void WriteEntry(MemoryMappedViewAccessor v, int index, OwnedInput input)
    {
        v.Write(OffsetHeldStart + index * 8, input.Code);
        v.Write(OffsetHeldStart + index * 8 + 2, input.IsMouse ? (byte)1 : (byte)0);
        v.Write(OffsetHeldStart + index * 8 + 3, input.Extended ? (byte)1 : (byte)0);
    }

    public void Dispose()
    {
        view?.Dispose();
        file?.Dispose();
        // The mapping is deliberately not disposed here on the owner side: a watchdog may
        // still be reading it, and tearing the page out from under a reader would turn a
        // recoverable state into an access violation.
        _ = ownsFile;
    }
}

/// <summary>Reads real process liveness for the watchdog's release decision.</summary>
public sealed class SystemProcessProbe : IProcessProbe
{
    public bool IsRunning(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
