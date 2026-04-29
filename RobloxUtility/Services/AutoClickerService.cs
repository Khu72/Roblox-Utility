using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using RobloxUtility.Native;

namespace RobloxUtility.Services;

public sealed class AutoClickerConfig
{
    public bool Enabled { get; set; }
    public double ClicksPerSecond { get; set; } = 8;
    public int InitialDelayMs { get; set; } = 0;
    public int ExtraDelayPerClickMs { get; set; } = 0;
    public bool EnableKeybind { get; set; }
    public string Keybind { get; set; } = "F6";
    public bool NotifyOnKeybindToggle { get; set; }
}

/// <summary>
/// Repeats synthetic left clicks while the physical left button is held and Roblox is foreground.
/// Uses WH_MOUSE_LL (non-injected only) for hold state so SendInput does not fight GetAsyncKeyState.
/// </summary>
public sealed class AutoClickerService : IDisposable
{
    private static readonly string[] RobloxWindowProcessNames = { MultiInstanceService.RobloxProcessName, "WinRoblox", "Roblox" };

    private static nint s_mouseHook;
    private static readonly NativeMethods.LowLevelMouseProc s_mouseProc = MouseHookCallback;
    private static int s_physicalLmbDown;

    private static nint MouseHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && lParam != nint.Zero)
        {
            var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            var injected = (info.Flags & (NativeMethods.LlmhfInjected | NativeMethods.LlmhfLowerIlInjected)) != 0;
            if (!injected)
            {
                if (wParam == NativeMethods.WmLButtonDown)
                {
                    Interlocked.Exchange(ref s_physicalLmbDown, 1);
                }
                else if (wParam == NativeMethods.WmLButtonUp)
                {
                    Interlocked.Exchange(ref s_physicalLmbDown, 0);
                }
            }
        }

        return NativeMethods.CallNextHookEx(s_mouseHook, nCode, wParam, lParam);
    }

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly object _lock = new();
    private volatile AutoClickerConfig _config = new();
    private bool _sessionActive;
    private long _nextActionTick;

    public AutoClickerService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(2) };
        _timer.Tick += OnTimerTick;
    }

    public void UpdateConfig(AutoClickerConfig config) => _config = config;

    public void SetRunning(bool on)
    {
        void apply()
        {
            if (on)
            {
                EnsureMouseHookInstalled();
                _timer.Start();
            }
            else
            {
                _timer.Stop();
                RemoveMouseHook();
                Interlocked.Exchange(ref s_physicalLmbDown, 0);
                _sessionActive = false;
            }
        }

        if (_dispatcher.CheckAccess())
        {
            lock (_lock)
            {
                apply();
            }
        }
        else
        {
            _dispatcher.Invoke(() =>
            {
                lock (_lock)
                {
                    apply();
                }
            });
        }
    }

    public void Dispose() => SetRunning(false);

    private static void EnsureMouseHookInstalled()
    {
        if (s_mouseHook != nint.Zero)
        {
            return;
        }

        var hMod = NativeMethods.GetModuleHandle(null);
        s_mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WhMouseLl, s_mouseProc, hMod, 0);
    }

    private static void RemoveMouseHook()
    {
        if (s_mouseHook == nint.Zero)
        {
            return;
        }

        _ = NativeMethods.UnhookWindowsHookEx(s_mouseHook);
        s_mouseHook = nint.Zero;
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        var cfg = _config;
        if (!cfg.Enabled)
        {
            _sessionActive = false;
            return;
        }

        if (Volatile.Read(ref s_physicalLmbDown) == 0)
        {
            _sessionActive = false;
            return;
        }

        if (!IsRobloxInForeground())
        {
            return;
        }

        if (!_sessionActive)
        {
            _sessionActive = true;
            var now0 = Environment.TickCount64;
            _nextActionTick = now0 + Math.Max(0, cfg.InitialDelayMs);
        }

        var now = Environment.TickCount64;
        if (now < _nextActionTick)
        {
            return;
        }

        var cps = Math.Clamp(cfg.ClicksPerSecond, 0.1, 200);
        var baseInterval = (int)Math.Round(1000.0 / cps);
        if (baseInterval < 1)
        {
            baseInterval = 1;
        }

        var interval = baseInterval + Math.Max(0, cfg.ExtraDelayPerClickMs);
        if (Volatile.Read(ref s_physicalLmbDown) == 0 || !IsRobloxInForeground())
        {
            return;
        }

        NativeMethods.TrySendLeftButtonClick();
        _nextActionTick = now + interval;
    }

    private static bool IsRobloxInForeground()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GaRoot);
        if (root != IntPtr.Zero)
        {
            hwnd = root;
        }

        if (NativeMethods.GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == 0)
        {
            return false;
        }

        try
        {
            using var p = Process.GetProcessById((int)pid);
            var n = p.ProcessName;
            if (n.Equals("RobloxPlayerBeta", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return RobloxWindowProcessNames.Any(x => n.Equals(x, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}
