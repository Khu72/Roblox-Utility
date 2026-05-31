using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RobloxUtility.Models;

public sealed class OnlineFriendRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public long UserId { get; init; }

    public string Username { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string TitleText => string.IsNullOrWhiteSpace(DisplayName) || DisplayName == Username
        ? Username
        : $"{DisplayName} (@{Username})";

    private string _gameText = "Online";
    public string GameText
    {
        get => _gameText;
        set
        {
            if (_gameText == value)
            {
                return;
            }

            _gameText = value;
            Notify();
        }
    }

    private string _onAccountsText = string.Empty;
    public string OnAccountsText
    {
        get => _onAccountsText;
        set
        {
            if (_onAccountsText == value)
            {
                return;
            }

            _onAccountsText = value;
            Notify();
        }
    }

    private ImageSource? _avatar;
    public ImageSource? Avatar
    {
        get => _avatar;
        set
        {
            if (_avatar == value)
            {
                return;
            }

            _avatar = value;
            Notify();
        }
    }

    public void SetAvatarFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            Avatar = null;
            return;
        }

        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.UriSource = new Uri(url, UriKind.Absolute);
            img.EndInit();
            if (img.CanFreeze)
            {
                img.Freeze();
            }

            Avatar = img;
        }
        catch
        {
            Avatar = null;
        }
    }
}
