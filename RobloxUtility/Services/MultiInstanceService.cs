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
    private static readonly string[] RobloxProcessNames =
    {
        "RobloxPlayerBeta",
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

    public IReadOnlyList<MultiInstanceResult> EnableMultiInstance()
    {
        _ = NativeMethods.TryEnableDebugPrivilege();
        var results = new List<MultiInstanceResult>();
        var seen = new HashSet<int>();
        foreach (var name in RobloxProcessNames)
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
                    int closed;
                    var ok = TryCloseSingletonForProcess(p, out closed, out var detail);
                    results.Add(new MultiInstanceResult(p.Id, ok, closed, detail));
                }
            }
        }

        return results;
    }

    public int CountRobloxInstances()
    {
        var seen = new HashSet<int>();
        foreach (var name in RobloxProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                seen.Add(p.Id);
                p.Dispose();
            }
        }

        return seen.Count;
    }

    private static bool TryCloseSingletonForProcess(Process process, out int handlesClosed, out string detail)
    {
        handlesClosed = 0;
        detail = "";
        if (string.IsNullOrEmpty(process.ProcessName) || process.Id <= 0)
        {
            detail = "Invalid process.";
            return false;
        }

        IntPtr hProcess = IntPtr.Zero;
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

            if (!QuerySystemHandleTable(NativeMethods.SystemExtendedHandleInformation, out var tablePtr, out _)
                || tablePtr == IntPtr.Zero)
            {
                detail = "NtQuerySystemInformation(SystemExtendedHandleInformation) failed.";
                return false;
            }

            using (new LocalMemory(tablePtr))
            {
                TryCloseUsingExtendedTable(hProcess, process.Id, tablePtr, out var closedEx);
                handlesClosed += closedEx;
            }

            if (handlesClosed == 0 && process.Id <= 0xFFFF
                && QuerySystemHandleTable(NativeMethods.SystemHandleInformation, out var legacyPtr, out _)
                && legacyPtr != IntPtr.Zero)
            {
                using (new LocalMemory(legacyPtr))
                {
                    handlesClosed += TryCloseUsingLegacyTable(hProcess, process.Id, legacyPtr);
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
            detail = process.Id > 0xFFFF
                ? "No matching handle found (or duplication blocked). Your Roblox PID is above 65535; only the extended handle table path applies on your system."
                : "No matching handle found or access denied when duplicating/closing. If Roblox is running, try Run as administrator, allow the app in AV, or check for a different Roblox build (e.g. Microsoft Store).";
        }
        return handlesClosed > 0;
    }

    private static bool TryCloseUsingExtendedTable(IntPtr hProcess, int pid, IntPtr tablePtr, out int handlesClosed)
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

                if (!NativeMethods.DuplicateHandle(
                        hProcess,
                        h.HandleValue,
                        NativeMethods.GetCurrentProcess(),
                        out var dup,
                        0,
                        false,
                        0))
                {
                    continue;
                }

                if (!TryGetName(dup, out var name)
                    || !name.Contains("singletonEvent", StringComparison.OrdinalIgnoreCase))
                {
                    NativeMethods.CloseHandle(dup);
                    continue;
                }

                NativeMethods.CloseHandle(dup);

                if (NativeMethods.DuplicateHandle(
                        hProcess,
                        h.HandleValue,
                        NativeMethods.GetCurrentProcess(),
                        out var dummy,
                        0,
                        false,
                        NativeMethods.DuplicateCloseSource) && dummy != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(dummy);
                    handlesClosed++;
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

    private static int TryCloseUsingLegacyTable(IntPtr hProcess, int pid, IntPtr tablePtr)
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

            var handleValue = new IntPtr(h.HandleValue);
            if (!NativeMethods.DuplicateHandle(
                    hProcess,
                    handleValue,
                    NativeMethods.GetCurrentProcess(),
                    out var dup,
                    0,
                    false,
                    0))
            {
                continue;
            }

            if (!TryGetName(dup, out var name)
                || !name.Contains("singletonEvent", StringComparison.OrdinalIgnoreCase))
            {
                NativeMethods.CloseHandle(dup);
                continue;
            }

            NativeMethods.CloseHandle(dup);

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
                handlesClosed++;
            }
        }

        return handlesClosed;
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

    /// <param name="informationClass"><see cref="NativeMethods.SystemExtendedHandleInformation"/> or <see cref="NativeMethods.SystemHandleInformation"/>.</param>
    private static bool QuerySystemHandleTable(int informationClass, out IntPtr table, out uint size)
    {
        table = IntPtr.Zero;
        size = 0x10000;
        while (size < 0x2000000)
        {
            var buf = Marshal.AllocHGlobal((int)size);
            int status = NativeMethods.NtQuerySystemInformation(
                informationClass,
                buf,
                (uint)size,
                out var retLen);

            if (status == StatusInfoLengthMismatch)
            {
                Marshal.FreeHGlobal(buf);
                size *= 2;
                continue;
            }

            if (status != StatusSuccess)
            {
                Marshal.FreeHGlobal(buf);
                return false;
            }

            table = buf;
            size = retLen;
            return true;
        }

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
