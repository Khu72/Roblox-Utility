using System.Runtime.InteropServices;
using System.Text;

namespace RobloxUtility.Native;

internal static class NativeMethods
{
    public const int InputMouse = 0;

    public const int SystemHandleInformation = 16;
    public const int SystemExtendedHandleInformation = 64;
    public const int ObjectNameInformation = 1;
    public const int DuplicateCloseSource = 0x00000001;

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(
        int systemInformationClass,
        IntPtr systemInformation,
        uint systemInformationLength,
        out uint returnLength);

    [DllImport("ntdll.dll")]
    public static extern int NtQueryObject(
        IntPtr handle,
        int objectInformationClass,
        IntPtr objectInformation,
        uint objectInformationLength,
        out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DuplicateHandle(
        IntPtr hSourceProcess,
        IntPtr hSourceHandle,
        IntPtr hTargetProcess,
        out IntPtr lpTargetHandle,
        uint dwDesiredAccess,
        bool bInheritHandle,
        uint dwOptions);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(
        uint dwDesiredAccess,
        bool bInheritHandle,
        int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetCurrentProcess();

    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint SePrivilegeEnabled = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privileges;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr TokenHandle,
        bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState,
        uint BufferLength,
        IntPtr PreviousState,
        IntPtr ReturnLength);

    internal static bool TryEnableDebugPrivilege()
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery | TokenAdjustPrivileges, out token) || token == IntPtr.Zero)
            {
                return false;
            }

            if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out var luid))
            {
                return false;
            }

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SePrivilegeEnabled }
            };

            _ = AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            // AdjustTokenPrivileges can return true even if privilege wasn't assigned; check GetLastError.
            return Marshal.GetLastWin32Error() == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (token != IntPtr.Zero)
            {
                CloseHandle(token);
            }
        }
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    /// <summary>2 = root top-level window (foreground may be a child control).</summary>
    public const uint GaRoot = 2;

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall)]
    public static extern void MouseEvent(uint dwFlags, uint dx, uint dy, uint cButtons, uint dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    public const int VkLButton = 0x01;
    public const uint MouseeventfLeftdown = 0x0002;
    public const uint MouseeventfLeftup = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    /// <summary>Win32 INPUT size differs on 32/64-bit (union padding). Use this for SendInput cbSize.</summary>
    internal static readonly int InputStructureSize = Marshal.SizeOf<INPUT>();

    /// <summary>Prefer SendInput over mouse_event — many games only honor the newer injection path.</summary>
    internal static bool TrySendLeftButtonClick()
    {
        var down = new INPUT
        {
            type = (uint)InputMouse,
            mi = new MOUSEINPUT { dwFlags = MouseeventfLeftdown }
        };
        var up = new INPUT
        {
            type = (uint)InputMouse,
            mi = new MOUSEINPUT { dwFlags = MouseeventfLeftup }
        };
        var batch = new[] { down, up };
        var n = SendInput(2, batch, InputStructureSize);
        if (n != 2)
        {
            MouseEvent(MouseeventfLeftdown, 0, 0, 0, 0);
            MouseEvent(MouseeventfLeftup, 0, 0, 0, 0);
            return false;
        }

        return true;
    }

    public const int WhMouseLl = 14;
    public const int WmLButtonDown = 0x0201;
    public const int WmLButtonUp = 0x0202;
    public const int WmMButtonDown = 0x0207;
    public const int WmXButtonDown = 0x020B;
    public const int LlmhfInjected = 0x00000001;
    public const int LlmhfLowerIlInjected = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT Pt;
        public int MouseData;
        public int Flags;
        public int Time;
        public UIntPtr DwExtraInfo;
    }

    public delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern nint SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern nint GetModuleHandle(string? lpModuleName);
}

/// <summary>Matches the layout used in reference singleton-closer C code (x64, pack 1).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SystemHandle
{
    public uint ProcessId;
    public byte ObjectTypeNumber;
    public byte Flags;
    public ushort Handle;
    public IntPtr Object;
    public uint GrantedAccess;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SystemHandleEx
{
    public IntPtr Object;
    public IntPtr UniqueProcessId;
    public IntPtr HandleValue;
    public uint GrantedAccess;
    public ushort CreatorBackTraceIndex;
    public ushort ObjectTypeIndex;
    public uint HandleAttributes;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 0)]
internal struct SystemHandleInformation
{
    public uint HandleCount;
    // First element; actual array is HandleCount
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct UnicodeString
{
    public ushort Length;
    public ushort MaximumLength;
    public IntPtr Buffer;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ObjectNameInformation
{
    public UnicodeString Name;
}
