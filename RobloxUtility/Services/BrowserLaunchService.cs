using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using RobloxUtility.Models;

namespace RobloxUtility.Services;

public static class BrowserLaunchService
{
    public static BrowserSettings Settings { get; set; } = new();

    public static bool OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        try
        {
            if (Settings.Browser == BrowserSettings.DefaultBrowser)
            {
                return OpenWithDefaultBrowser(url, Settings.OpenInPrivate);
            }

            var exe = FindBrowserExecutable(Settings.Browser);
            if (exe is null)
            {
                AppLog.Warn($"Could not find {Settings.Browser}. Opening with your default browser instead.");
                return OpenWithDefaultBrowser(url, Settings.OpenInPrivate);
            }

            return LaunchBrowser(exe, url, Settings.OpenInPrivate);
        }
        catch (Exception ex)
        {
            AppLog.Err($"Failed to open link: {ex.Message}");
            return false;
        }
    }

    private static bool OpenWithDefaultBrowser(string url, bool privateMode)
    {
        if (!privateMode)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }

        var command = GetDefaultBrowserCommand();
        if (string.IsNullOrWhiteSpace(command))
        {
            AppLog.Warn("Private mode needs a specific browser selected in Console settings.");
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }

        var exe = ParseExecutablePath(command);
        if (exe is null || !File.Exists(exe))
        {
            AppLog.Warn("Private mode needs a specific browser selected in Console settings.");
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }

        return LaunchBrowser(exe, url, privateMode: true);
    }

    private static bool LaunchBrowser(string executablePath, string url, bool privateMode)
    {
        var args = BuildArguments(executablePath, url, privateMode);
        Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = args,
            UseShellExecute = false
        });
        return true;
    }

    private static string BuildArguments(string executablePath, string url, bool privateMode)
    {
        if (!privateMode)
        {
            return $"\"{url}\"";
        }

        var name = Path.GetFileName(executablePath).ToLowerInvariant();
        var flag = name switch
        {
            "chrome.exe" or "brave.exe" => "--incognito",
            "msedge.exe" => "--inprivate",
            "firefox.exe" => "-private-window",
            "opera.exe" => "--private",
            _ when name.Contains("chrome") => "--incognito",
            _ when name.Contains("edge") => "--inprivate",
            _ when name.Contains("firefox") => "-private-window",
            _ when name.Contains("brave") => "--incognito",
            _ when name.Contains("opera") => "--private",
            _ => null
        };

        return flag is null ? $"\"{url}\"" : $"{flag} \"{url}\"";
    }

    private static string? FindBrowserExecutable(string browser) => browser switch
    {
        BrowserSettings.Chrome => FindFirstExisting(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe")),
        BrowserSettings.Edge => FindFirstExisting(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe")),
        BrowserSettings.Firefox => FindFirstExisting(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Mozilla Firefox\firefox.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Mozilla Firefox\firefox.exe")),
        BrowserSettings.Brave => FindFirstExisting(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"BraveSoftware\Brave-Browser\Application\brave.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"BraveSoftware\Brave-Browser\Application\brave.exe")),
        BrowserSettings.Opera => FindFirstExisting(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Opera\opera.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Opera Software\Opera Stable\opera.exe")),
        _ => null
    };

    private static string? FindFirstExisting(params string[] paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static string? GetDefaultBrowserCommand()
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(@"http\shell\open\command");
            return key?.GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }

    private static string? ParseExecutablePath(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end > 1)
            {
                return command[1..end];
            }
        }

        var space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }
}
