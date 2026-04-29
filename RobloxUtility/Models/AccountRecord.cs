using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RobloxUtility.Models;
public sealed class AccountRecord : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public Guid Id { get; set; } = Guid.NewGuid();

    private string _displayName = string.Empty;
    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (_displayName == value)
            {
                return;
            }

            _displayName = value;
            Notify();
            Notify(nameof(ListLabel));
        }
    }

    /// <summary>Text used in the saved-accounts list when the name would otherwise be blank.</summary>
    public string ListLabel => string.IsNullOrWhiteSpace(DisplayName) ? "·  Unnamed account" : DisplayName!;

    public long DefaultPlaceId { get; set; }

    /// <summary>Optional link to a saved entry on the Places tab.</summary>
    public Guid? LinkedPlaceEntryId { get; set; }

    public string? ProtectedCookieBase64 { get; set; }

    public override string ToString() => ListLabel;
}

