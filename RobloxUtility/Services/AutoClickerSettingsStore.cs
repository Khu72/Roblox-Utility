using System.IO;
using System.Text.Json;

namespace RobloxUtility.Services;

public sealed class AutoClickerSettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _filePath;

    public AutoClickerSettingsStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RobloxUtility");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "autoclicker.json");
    }

    public AutoClickerConfig? Load()
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var cfg = JsonSerializer.Deserialize<AutoClickerConfig>(json);
            if (cfg is null)
            {
                return null;
            }

            cfg.ClicksPerSecond = Math.Clamp(cfg.ClicksPerSecond, 1, 100);
            cfg.InitialDelayMs = Math.Clamp(cfg.InitialDelayMs, 0, 1000);
            cfg.ExtraDelayPerClickMs = Math.Clamp(cfg.ExtraDelayPerClickMs, 0, 500);
            return cfg;
        }
        catch
        {
            return null;
        }
    }

    public void Save(AutoClickerConfig cfg)
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

