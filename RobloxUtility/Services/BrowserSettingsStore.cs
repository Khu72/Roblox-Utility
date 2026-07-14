using System.IO;
using System.Text.Json;
using RobloxUtility.Models;

namespace RobloxUtility.Services;

public sealed class BrowserSettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _filePath;

    public BrowserSettingsStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RobloxUtility");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "browser.json");
    }

    public BrowserSettings Load()
    {
        if (!File.Exists(_filePath))
        {
            return new BrowserSettings();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var cfg = JsonSerializer.Deserialize<BrowserSettings>(json);
            if (cfg is null)
            {
                return new BrowserSettings();
            }

            if (!BrowserSettings.AllBrowsers.Contains(cfg.Browser))
            {
                cfg.Browser = BrowserSettings.DefaultBrowser;
            }

            return cfg;
        }
        catch
        {
            return new BrowserSettings();
        }
    }

    public void Save(BrowserSettings cfg)
    {
        try
        {
            File.WriteAllText(_filePath, JsonSerializer.Serialize(cfg, Json));
        }
        catch
        {
            // ignore IO failures
        }
    }
}
