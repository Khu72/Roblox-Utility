using System.Diagnostics;
using System.Runtime.InteropServices;
using RobloxUtility.Native;

namespace RobloxUtility.Services;

/// <summary>
/// When a Roblox game window is closed, the client often leaves RobloxPlayerBeta / launcher /
/// crash-handler processes in the tray. This watches for processes that had a visible window
/// and then lost it, and force-ends them so they disappear from the notification area.
/// </summary>
public sealed class RobloxExitCleanupService : IDisposable
{
    private static readonly string[] PlayerProcessNames = { MultiInstanceService.RobloxProcessName };
    private static readonly string[] OrphanProcessNames =
    {
        "RobloxPlayerLauncher",
        "RobloxCrashHandler",
        "RobloxCrashHandler64"
    };

    private readonly Dictionary<int, WatchState> _watched = new();
    private readonly object _gate = new();
    private readonly System.Threading.Timer _timer;
    private bool _disposed;

    private sealed class WatchState
    {
        public bool SawVisibleWindow;
        public DateTimeOffset? WindowLostAt;
    }

    public RobloxExitCleanupService()
    {
        _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
    }

    /// <summary>Force-ends every Roblox-related process (player, launcher, crash handler).</summary>
    public static int ForceQuitAll()
    {
        _ = NativeMethods.TryEnableDebugPrivilege();
        var killed = 0;
        var names = PlayerProcessNames.Concat(OrphanProcessNames).Append("WinRoblox").Append("Roblox").Distinct();
        foreach (var name in names)
        {
            Process[] list;
            try
            {
                list = Process.GetProcessesByName(name);
            }
            catch
            {
                continue;
            }

            foreach (var p in list)
            {
                using (p)
                {
                    try
                    {
                        if (p.HasExited)
                        {
                            continue;
                        }

                        p.Kill(entireProcessTree: true);
                        killed++;
                    }
                    catch
                    {
                        // Access denied / already exiting
                    }
                }
            }
        }

        return killed;
    }

    private void Tick()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            SweepPlayers();
            SweepOrphans();
        }
        catch
        {
            // never let the timer crash the app
        }
    }

    private void SweepPlayers()
    {
        var live = new HashSet<int>();
        foreach (var name in PlayerProcessNames)
        {
            Process[] list;
            try
            {
                list = Process.GetProcessesByName(name);
            }
            catch
            {
                continue;
            }

            foreach (var p in list)
            {
                using (p)
                {
                    live.Add(p.Id);
                    UpdatePlayer(p);
                }
            }
        }

        lock (_gate)
        {
            foreach (var id in _watched.Keys.Where(id => !live.Contains(id)).ToList())
            {
                _watched.Remove(id);
            }
        }
    }

    private void UpdatePlayer(Process p)
    {
        WatchState state;
        lock (_gate)
        {
            if (!_watched.TryGetValue(p.Id, out state!))
            {
                state = new WatchState();
                _watched[p.Id] = state;
            }
        }

        var hasWindow = HasVisibleTopLevelWindow(p.Id);
        if (hasWindow)
        {
            state.SawVisibleWindow = true;
            state.WindowLostAt = null;
            return;
        }

        if (!state.SawVisibleWindow)
        {
            return;
        }

        state.WindowLostAt ??= DateTimeOffset.UtcNow;
        if (DateTimeOffset.UtcNow - state.WindowLostAt.Value < TimeSpan.FromSeconds(2.5))
        {
            return;
        }

        try
        {
            p.Refresh();
            if (p.HasExited)
            {
                return;
            }

            AppLog.Line("ROBLOX", $"Game window closed — ending leftover PID {p.Id} so it leaves the tray.");
            p.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not end leftover Roblox PID {p.Id}: {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _watched.Remove(p.Id);
            }
        }
    }

    private static void SweepOrphans()
    {
        // Only clear launcher/crash-handler stubs when no player process remains.
        if (Process.GetProcessesByName(MultiInstanceService.RobloxProcessName).Length > 0)
        {
            return;
        }

        foreach (var name in OrphanProcessNames)
        {
            Process[] list;
            try
            {
                list = Process.GetProcessesByName(name);
            }
            catch
            {
                continue;
            }

            foreach (var p in list)
            {
                using (p)
                {
                    try
                    {
                        if (p.HasExited)
                        {
                            continue;
                        }

                        AppLog.Line("ROBLOX", $"Ending orphaned {p.ProcessName} PID {p.Id}.");
                        p.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
        }
    }

    private static bool HasVisibleTopLevelWindow(int pid)
    {
        var found = false;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (NativeMethods.GetWindowThreadProcessId(hwnd, out var windowPid) == 0 || windowPid != (uint)pid)
            {
                return true;
            }

            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                return true;
            }

            // Skip owned popups; require a real top-level window.
            if (NativeMethods.GetWindow(hwnd, NativeMethods.GwOwner) != IntPtr.Zero)
            {
                return true;
            }

            found = true;
            return false;
        }, IntPtr.Zero);

        return found;
    }
}
