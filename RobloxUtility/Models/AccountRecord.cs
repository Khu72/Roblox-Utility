using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

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

    /// <summary>
    /// Stable browserTrackerId for this account (Roblox web uses a persistent tracker).
    /// Randomizing it on every launch looks more like automation and can trigger challenges.
    /// </summary>
    public long BrowserTrackerId { get; set; }

    private AccountPresenceKind _presenceKind = AccountPresenceKind.Unknown;

    [JsonIgnore]
    public AccountPresenceKind PresenceKind
    {
        get => _presenceKind;
        set
        {
            if (_presenceKind == value)
            {
                return;
            }

            _presenceKind = value;
            Notify();
        }
    }

    public override string ToString() => ListLabel;
}

