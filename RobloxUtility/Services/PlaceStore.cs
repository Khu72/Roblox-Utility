using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using RobloxUtility.Models;

namespace RobloxUtility.Services;

public sealed class PlaceStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _filePath;
    public ObservableCollection<PlaceEntry> Places { get; } = new();

    public PlaceStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RobloxUtility");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "places.json");
    }

    public void Load()
    {
        Places.Clear();
        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var list = JsonSerializer.Deserialize<List<PlaceEntry>>(json) ?? new List<PlaceEntry>();
            foreach (var p in list)
            {
                Places.Add(p);
            }
        }
        catch
        {
            // ignore corrupt file
        }
    }

    public void Save()
    {
        File.WriteAllText(_filePath, JsonSerializer.Serialize(Places.ToList(), Json));
    }

    public PlaceEntry? FindById(Guid id)
    {
        foreach (var p in Places)
        {
            if (p.Id == id)
            {
                return p;
            }
        }

        return null;
    }
}
