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
            hProcess = NativeMethods.OpenProcess(
                ProcessDupHandle | ProcessQueryInformation | ProcessVmRead,
                false,
                process.Id);
            if (hProcess == IntPtr.Zero)
            {
                detail = $"OpenProcess failed (Win32={Marshal.GetLastWin32Error()}).";
                return false;
            }

            if (!QuerySystemHandleTable(out var tablePtr, out var tableSize) || tablePtr == IntPtr.Zero)
            {
                detail = "NtQuerySystemInformation failed.";
                return false;
            }

            using (new LocalMemory(tablePtr))
            {
                if (TryCloseUsingExtendedTable(hProcess, process.Id, tablePtr, out var closedEx))
                {
                    handlesClosed += closedEx;
                }
                else
                {
                    handlesClosed += TryCloseUsingLegacyTable(hProcess, process.Id, tablePtr);
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
            detail = "No matching handle found or access denied when duplicating/closing.";
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
            if (count <= 0 || count > int.MaxValue)
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

                if (!TryGetName(dup, out var name) || !name.Contains("ROBLOX_singletonEvent", StringComparison.OrdinalIgnoreCase))
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
        var handlesClosed = 0;
        var pBase = tablePtr;
        var count = (int)Marshal.ReadInt32(pBase);
        pBase = IntPtr.Add(pBase, sizeof(uint));
        int handleStructSize = Marshal.SizeOf<SystemHandle>();
        for (int i = 0; i < count; i++)
        {
            var h = Marshal.PtrToStructure<SystemHandle>(pBase);
            pBase = IntPtr.Add(pBase, handleStructSize);

            if ((int)h.ProcessId != pid)
            {
                continue;
            }

            var handleValue = new IntPtr(h.Handle);
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

            if (!TryGetName(dup, out var name) || !name.Contains("ROBLOX_singletonEvent", StringComparison.OrdinalIgnoreCase))
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

    private static bool QuerySystemHandleTable(out IntPtr table, out uint size)
    {
        table = IntPtr.Zero;
        size = 0x10000;
        while (size < 0x2000000)
        {
            var buf = Marshal.AllocHGlobal((int)size);
            int status = NativeMethods.NtQuerySystemInformation(
                NativeMethods.SystemExtendedHandleInformation,
                buf,
                (uint)size,
                out var retLen);

            if (status != StatusSuccess && status != StatusInfoLengthMismatch)
            {
                // Fallback for older systems.
                status = NativeMethods.NtQuerySystemInformation(
                    NativeMethods.SystemHandleInformation,
                    buf,
                    (uint)size,
                    out retLen);
            }
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
