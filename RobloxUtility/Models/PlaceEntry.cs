using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RobloxUtility.Models;

public sealed class PlaceEntry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public Guid Id { get; set; } = Guid.NewGuid();

    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
            {
                return;
            }

            _name = value;
            Notify();
            Notify(nameof(ListLabel));
        }
    }

    /// <summary>Text used in the saved-places list when the name would otherwise be blank.</summary>
    public string ListLabel => string.IsNullOrWhiteSpace(Name) ? "·  Unsaved place" : Name!;

    public long PlaceId { get; set; }

    public override string ToString() => ListLabel;
}

