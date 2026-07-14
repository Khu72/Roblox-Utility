using System.Diagnostics;
using System.Runtime.InteropServices;
using RobloxUtility.Native;

namespace RobloxUtility.Services;

/// <summary>
/// Closes the ROBLOX_singletonEvent object handle in running RobloxPlayerBeta.exe
/// (same idea as <c>\Sessions\...\BaseNamedObjects\ROBLOX_singletonEvent</c> in the handle name).
/// </summary>
public sealed class MultiInstanceService
{
    public const string RobloxProcessName = "RobloxPlayerBeta";

    /// <summary>Only the game client holds the singleton we need to close. Launcher stubs are ignored.</summary>
    private static readonly string[] SingletonTargetProcessNames = { RobloxProcessName };

    private static readonly string[] CountProcessNames =
    {
        RobloxProcessName,
        "RobloxPlayerLauncher",
        "WinRoblox",
        "Roblox",
    };

    private const uint ProcessDupHandle = 0x0040;
    /// <summary>Win10+; often allowed when full <see cref="ProcessQueryInformation"/> is denied.</summary>
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int StatusSuccess = 0;

    /// <summary>PIDs we already unlocked this session — re-scanning every launch destabilizes clients.</summary>
    private readonly HashSet<int> _unlockedPids = new();
    private readonly object _unlockGate = new();

    public IReadOnlyList<MultiInstanceResult> EnableMultiInstance()
    {
        _ = NativeMethods.TryEnableDebugPrivilege();
        PruneUnlockedPids();

        var results = new List<MultiInstanceResult>();
        var seen = new HashSet<int>();
        ushort? eventTypeIndex = TryResolveEventObjectTypeIndex();

        foreach (var name in SingletonTargetProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                if (!seen.Add(p.Id))
                {
                    p.Dispose();
                    continue;
                }

                using (p)
                {
                    lock (_unlockGate)
                    {
                        if (_unlockedPids.Contains(p.Id))
                        {
                            results.Add(new MultiInstanceResult(p.Id, true, 0, "Already unlocked this session."));
                            continue;
                        }
                    }

                    var ok = TryCloseSingletonForProcess(p, eventTypeIndex, out var closed, out var detail);
                    if (ok && closed > 0)
                    {
                        lock (_unlockGate)
                        {
                            _unlockedPids.Add(p.Id);
                        }
                    }

                    results.Add(new MultiInstanceResult(p.Id, ok, closed, detail));
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Unlocks any new RobloxPlayerBeta instances before a join. Retries briefly because the
    /// singleton handle can appear a moment after the process starts. Skips PIDs already unlocked
    /// this session so we do not re-scan every handle on every launch (that can destabilize clients).
    /// </summary>
    public async Task<int> EnsureUnlockedBeforeLaunchAsync(CancellationToken cancellationToken = default)
    {
        var totalClosed = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CountRobloxPlayerInstances() == 0)
            {
                return totalClosed;
            }

            var results = EnableMultiInstance();
            foreach (var r in results)
            {
                totalClosed += r.HandlesClosed;
            }

            if (results.Count == 0 || results.All(r => r.Succeeded))
            {
                return totalClosed;
            }

            await Task.Delay(350, cancellationToken).ConfigureAwait(false);
        }

        return totalClosed;
    }

    public int CountRobloxInstances()
    {
        var seen = new HashSet<int>();
        foreach (var name in CountProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                seen.Add(p.Id);
                p.Dispose();
            }
        }

        return seen.Count;
    }

    public int CountRobloxPlayerInstances()
    {
        var seen = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName(RobloxProcessName))
        {
            seen.Add(p.Id);
            p.Dispose();
        }

        return seen.Count;
    }

    private void PruneUnlockedPids()
    {
        lock (_unlockGate)
        {
            if (_unlockedPids.Count == 0)
            {
                return;
            }

            var live = new HashSet<int>();
            foreach (var p in Process.GetProcessesByName(RobloxProcessName))
            {
                live.Add(p.Id);
                p.Dispose();
            }

            _unlockedPids.RemoveWhere(id => !live.Contains(id));
        }
    }

    private static bool TryCloseSingletonForProcess(
        Process process,
        ushort? eventTypeIndex,
        out int handlesClosed,
        out string detail)
    {
        handlesClosed = 0;
        detail = "";
        if (string.IsNullOrEmpty(process.ProcessName) || process.Id <= 0)
        {
            detail = "Invalid process.";
            return false;
        }

        IntPtr hProcess = IntPtr.Zero;
        var extOk = false;
        var extNt = 0;
        var legOk = false;
        var legNt = 0;
        try
        {
            // Prefer minimal rights: games/AV often block QUERY_INFORMATION + VM_READ while DUPLICATE still works.
            hProcess = NativeMethods.OpenProcess(ProcessDupHandle, false, process.Id);
            if (hProcess == IntPtr.Zero)
            {
                hProcess = NativeMethods.OpenProcess(
                    ProcessDupHandle | ProcessQueryLimitedInformation,
                    false,
                    process.Id);
            }

            if (hProcess == IntPtr.Zero)
            {
                hProcess = NativeMethods.OpenProcess(
                    ProcessDupHandle | ProcessQueryInformation | ProcessVmRead,
                    false,
                    process.Id);
            }

            if (hProcess == IntPtr.Zero)
            {
                detail = $"OpenProcess failed (Win32={Marshal.GetLastWin32Error()}). Try running the utility as Administrator.";
                return false;
            }

            // Class 64 (extended) is richer but is blocked on some PCs/policies; class 16 (basic) still works for typical PIDs ≤ 65535.
            extOk = QuerySystemHandleTable(
                NativeMethods.SystemExtendedHandleInformation,
                out var extTable,
                out _,
                out extNt);
            if (extOk && extTable != IntPtr.Zero)
            {
                using (new LocalMemory(extTable))
                {
                    TryCloseUsingExtendedTable(hProcess, process.Id, extTable, eventTypeIndex, out var closedEx);
                    handlesClosed += closedEx;
                }
            }

            if (handlesClosed == 0 && process.Id <= 0xFFFF)
            {
                legOk = QuerySystemHandleTable(
                    NativeMethods.SystemHandleInformation,
                    out var legacyPtr,
                    out _,
                    out legNt);
                if (legOk && legacyPtr != IntPtr.Zero)
                {
                    using (new LocalMemory(legacyPtr))
                    {
                        handlesClosed += TryCloseUsingLegacyTable(hProcess, process.Id, legacyPtr, eventTypeIndex);
                    }
                }
            }
        }
        finally
        {
            if (hProcess != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(hProcess);
            }
        }

        if (handlesClosed == 0)
        {
            if (!extOk && process.Id <= 0xFFFF && !legOk)
            {
                var lengthMismatch =
                    extNt == StatusInfoLengthMismatch && legNt == StatusInfoLengthMismatch;
                detail = lengthMismatch
                    ? $"Could not read system handle tables: both queries returned 0x{extNt:X8} (STATUS_INFO_LENGTH_MISMATCH — buffer too small for this PC's handle count). Try closing heavy apps (browsers, etc.) and retry; if it persists, the snapshot may exceed the app limit."
                    : $"Could not read system handle tables (extended NtStatus=0x{extNt:X8}; basic=0x{legNt:X8}). Run as Administrator if you have not; some policies block one of these APIs.";
            }
            else if (!extOk && process.Id > 0xFFFF)
            {
                detail =
                    $"Extended handle information failed (0x{extNt:X8}) and this Roblox PID is above 65535, so the basic handle list cannot target it.";
            }
            else if (process.Id > 0xFFFF)
            {
                detail =
                    "No matching handle found (or duplication blocked). Roblox's PID is above 65535; only the extended handle table can enumerate it on this PC.";
            }
            else
            {
                detail =
                    "No matching singletonEvent handle was closed (duplicate/close may be blocked). Ensure Roblox is running, run this app as Administrator, and check AV; Microsoft Store / some builds differ.";
            }
        }

        return handlesClosed > 0;
    }

    private static bool TryCloseUsingExtendedTable(
        IntPtr hProcess,
        int pid,
        IntPtr tablePtr,
        ushort? eventTypeIndex,
        out int handlesClosed)
    {
        handlesClosed = 0;
        try
        {
            // SYSTEM_HANDLE_INFORMATION_EX:
            // ULONG_PTR NumberOfHandles; ULONG_PTR Reserved; SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX Handles[]
            var count = Marshal.ReadIntPtr(tablePtr).ToInt64();
            if (count <= 0 || count > 16_000_000)
            {
                return false;
            }

            var pBase = IntPtr.Add(tablePtr, IntPtr.Size * 2);
            int handleStructSize = Marshal.SizeOf<SystemHandleEx>();
            for (int i = 0; i < (int)count; i++)
            {
                var h = Marshal.PtrToStructure<SystemHandleEx>(pBase);
                pBase = IntPtr.Add(pBase, handleStructSize);

                if (h.UniqueProcessId == IntPtr.Zero || h.HandleValue == IntPtr.Zero)
                {
                    continue;
                }

                if (h.UniqueProcessId.ToInt64() != pid)
                {
                    continue;
                }

                if (eventTypeIndex is ushort ev && h.ObjectTypeIndex != ev)
                {
                    continue;
                }

                if (TryCloseSingletonHandle(hProcess, h.HandleValue))
                {
                    handlesClosed++;
                    // One unlock per process is enough; keep scanning less invasive.
                    break;
                }
            }

            return true;
        }
        catch
        {
            handlesClosed = 0;
            return false;
        }
    }

    private static int TryCloseUsingLegacyTable(
        IntPtr hProcess,
        int pid,
        IntPtr tablePtr,
        ushort? eventTypeIndex)
    {
        if (pid > 0xFFFF)
        {
            return 0;
        }

        var handlesClosed = 0;
        var pBase = tablePtr;
        uint countU = unchecked((uint)Marshal.ReadInt32(pBase));
        if (countU > 16_000_000)
        {
            return 0;
        }

        var count = (int)countU;

        pBase = IntPtr.Add(pBase, sizeof(uint));
        int handleStructSize = Marshal.SizeOf<SystemHandleTableEntryInfo>();
        ushort upid = (ushort)pid;
        for (int i = 0; i < count; i++)
        {
            var h = Marshal.PtrToStructure<SystemHandleTableEntryInfo>(pBase);
            pBase = IntPtr.Add(pBase, handleStructSize);

            if (h.UniqueProcessId != upid)
            {
                continue;
            }

            if (eventTypeIndex is ushort ev && h.ObjectTypeIndex != ev)
            {
                continue;
            }

            var handleValue = new IntPtr(h.HandleValue);
            if (TryCloseSingletonHandle(hProcess, handleValue))
            {
                handlesClosed++;
                break;
            }
        }

        return handlesClosed;
    }

    /// <summary>
    /// Duplicate → verify ROBLOX_singletonEvent → re-verify → close source.
    /// Re-verify shrinks the race where the handle value is reused for something else.
    /// </summary>
    private static bool TryCloseSingletonHandle(IntPtr hProcess, IntPtr handleValue)
    {
        if (!NativeMethods.DuplicateHandle(
                hProcess,
                handleValue,
                NativeMethods.GetCurrentProcess(),
                out var dup,
                0,
                false,
                0))
        {
            return false;
        }

        try
        {
            if (!TryGetName(dup, out var name) || !IsRobloxSingletonEventName(name))
            {
                return false;
            }
        }
        finally
        {
            NativeMethods.CloseHandle(dup);
        }

        // Re-check immediately before closing the source handle.
        if (!NativeMethods.DuplicateHandle(
                hProcess,
                handleValue,
                NativeMethods.GetCurrentProcess(),
                out var dup2,
                0,
                false,
                0))
        {
            return false;
        }

        try
        {
            if (!TryGetName(dup2, out var name2) || !IsRobloxSingletonEventName(name2))
            {
                return false;
            }
        }
        finally
        {
            NativeMethods.CloseHandle(dup2);
        }

        if (NativeMethods.DuplicateHandle(
                hProcess,
                handleValue,
                NativeMethods.GetCurrentProcess(),
                out var dummy,
                0,
                false,
                NativeMethods.DuplicateCloseSource) && dummy != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(dummy);
            return true;
        }

        return false;
    }

    private static bool IsRobloxSingletonEventName(string name) =>
        name.Contains("ROBLOX_singletonEvent", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("singletonEvent", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolve the current Windows "Event" object type index so we only DuplicateHandle
    /// Event objects in Roblox — duplicating every handle on each launch can destabilize clients
    /// and they may exit a while later.
    /// </summary>
    private static ushort? TryResolveEventObjectTypeIndex()
    {
        using var localEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
        if (!localEvent.SafeWaitHandle.IsInvalid)
        {
            var ourPid = Environment.ProcessId;
            var handleValue = localEvent.SafeWaitHandle.DangerousGetHandle();
            if (QuerySystemHandleTable(
                    NativeMethods.SystemExtendedHandleInformation,
                    out var table,
                    out _,
                    out _) && table != IntPtr.Zero)
            {
                using (new LocalMemory(table))
                {
                    var count = Marshal.ReadIntPtr(table).ToInt64();
                    if (count > 0 && count <= 16_000_000)
                    {
                        var pBase = IntPtr.Add(table, IntPtr.Size * 2);
                        var size = Marshal.SizeOf<SystemHandleEx>();
                        for (var i = 0; i < (int)count; i++)
                        {
                            var h = Marshal.PtrToStructure<SystemHandleEx>(pBase);
                            pBase = IntPtr.Add(pBase, size);
                            if (h.UniqueProcessId.ToInt64() == ourPid && h.HandleValue == handleValue)
                            {
                                return h.ObjectTypeIndex;
                            }
                        }
                    }
                }
            }
        }

        return null;
    }

    private static bool TryGetName(IntPtr duplicatedHandle, out string name)
    {
        name = string.Empty;
        if (duplicatedHandle == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.NtQueryObject(duplicatedHandle, NativeMethods.ObjectNameInformation, IntPtr.Zero, 0, out var need);
        if (need == 0)
        {
            return false;
        }

        var buf = Marshal.AllocHGlobal((int)need);
        try
        {
            if (NativeMethods.NtQueryObject(duplicatedHandle, NativeMethods.ObjectNameInformation, buf, need, out need) != StatusSuccess)
            {
                return false;
            }

            var o = Marshal.PtrToStructure<ObjectNameInformation>(buf);
            if (o.Name.Buffer == IntPtr.Zero || o.Name.Length == 0)
            {
                return false;
            }

            var ch = o.Name.Length / 2;
            var w = o.Name.Length > 0 ? Marshal.PtrToStringUni(o.Name.Buffer, ch) ?? string.Empty : string.Empty;
            name = w;
            return name.Length > 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>Upper bound for system handle snapshot; machines with huge handle counts need a large buffer.</summary>
    private const uint MaxHandleTableBufferBytes = 512 * 1024 * 1024;

    /// <param name="informationClass"><see cref="NativeMethods.SystemExtendedHandleInformation"/> or <see cref="NativeMethods.SystemHandleInformation"/>.</param>
    /// <param name="ntStatusOnFailure">Last non-success NTSTATUS when this returns false.</param>
    private static bool QuerySystemHandleTable(int informationClass, out IntPtr table, out uint size, out int ntStatusOnFailure)
    {
        table = IntPtr.Zero;
        size = 0x10000;
        ntStatusOnFailure = 0;

        // STATUS_INFO_LENGTH_MISMATCH (0xC0000004): grow buffer. Must allow trying sizes >= 0x2000000 —
        // the old "while (size < 0x2000000)" skipped the final double (16MB → 32MB) entirely.
        while (size <= MaxHandleTableBufferBytes)
        {
            var buf = Marshal.AllocHGlobal((int)size);
            int status = NativeMethods.NtQuerySystemInformation(
                informationClass,
                buf,
                size,
                out var retLen);

            if (status == StatusInfoLengthMismatch)
            {
                Marshal.FreeHGlobal(buf);
                uint next = Math.Max(checked(size * 2), retLen);
                if (next == 0)
                {
                    next = size * 2;
                }

                if (next < size || next > MaxHandleTableBufferBytes)
                {
                    ntStatusOnFailure = StatusInfoLengthMismatch;
                    return false;
                }

                size = next;
                continue;
            }

            if (status != StatusSuccess)
            {
                ntStatusOnFailure = status;
                Marshal.FreeHGlobal(buf);
                return false;
            }

            table = buf;
            size = retLen;
            return true;
        }

        ntStatusOnFailure = StatusInfoLengthMismatch;
        return false;
    }

    private readonly struct LocalMemory : IDisposable
    {
        public LocalMemory(IntPtr p) => Pointer = p;
        public IntPtr Pointer { get; }
        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }
}

public readonly record struct MultiInstanceResult(int ProcessId, bool Succeeded, int HandlesClosed, string Detail);
