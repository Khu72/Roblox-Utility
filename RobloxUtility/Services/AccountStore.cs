using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using RobloxUtility.Models;

namespace RobloxUtility.Services;

public sealed class AccountStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _filePath;
    public ObservableCollection<AccountRecord> Accounts { get; } = new();

    public AccountStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RobloxUtility");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "accounts.json");
    }

    public void Load()
    {
        Accounts.Clear();
        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var list = JsonSerializer.Deserialize<List<AccountRecord>>(json) ?? new List<AccountRecord>();
            foreach (var a in list)
            {
                Accounts.Add(a);
            }
        }
        catch
        {
            // keep empty; corrupt file
        }
    }

    public void Save()
    {
        var list = Accounts.ToList();
        File.WriteAllText(_filePath, JsonSerializer.Serialize(list, Json));
    }
}
