using System.IO;

namespace RobloxUtility.Services;

public static class RobloxPathHelper
{
    public static string? FindRobloxPlayerBeta()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(local, "Roblox", "Versions");
        if (!Directory.Exists(root))
        {
            return null;
        }

        string? latest = null;
        DateTime latestTime = DateTime.MinValue;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var exe = Path.Combine(dir, "RobloxPlayerBeta.exe");
            if (File.Exists(exe))
            {
                var t = File.GetLastWriteTimeUtc(exe);
                if (t >= latestTime)
                {
                    latestTime = t;
                    latest = exe;
                }
            }
        }

        return latest;
    }
}
