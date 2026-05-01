using System.Collections.Specialized;
using System.Drawing;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using RobloxUtility.Models;
using RobloxUtility.Native;
using RobloxUtility.Services;

namespace RobloxUtility;

public partial class MainWindow
{
    private const int ConsoleMaxChars = 500_000;

    private readonly AccountStore _store = new();
    private readonly PlaceStore _placeStore = new();
    private readonly MultiInstanceService _multi = new();
    private readonly AutoClickerService _autoClicker;
    private readonly AutoClickerSettingsStore _autoStore = new();
    private HwndSource? _hwndSource;
    private const int AutoHotkeyId = 0x4A11;
    private const int WmHotkey = 0x0312;
    private bool _hotkeyRegistered;
    private bool _awaitingHotkeyInput;
    private string _selectedHotkeyCanonical = "F6";
    private nint _mouseHotkeyHook;
    private NativeMethods.LowLevelMouseProc? _mouseHotkeyProc;
    private HotkeyBindingType _mouseBindingType = HotkeyBindingType.None;
    private Forms.NotifyIcon? _notifyIcon;
    private bool _suppressAutoPersist;
    private enum HotkeyBindingType
    {
        None = 0,
        Keyboard = 1,
        MouseMiddle = 2,
        MouseXButton1 = 3,
        MouseXButton2 = 4
    }

    private bool _suppressExperienceCombo;
    private bool _suppressPlacesPlaceCombo;
    private int _presenceRefreshGate;

    public MainWindow()
    {
        InitializeComponent();
        _autoClicker = new AutoClickerService(Dispatcher);
        CpsSlider.ValueChanged += (_, _) => { RefreshAutoLabels(); PushAutoConfig(); };
        InitialDelaySlider.ValueChanged += (_, _) => { RefreshAutoLabels(); PushAutoConfig(); };
        ExtraDelaySlider.ValueChanged += (_, _) => { RefreshAutoLabels(); PushAutoConfig(); };
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _store.Load();
        _placeStore.Load();
        AccountsList.ItemsSource = _store.Accounts;
        PlacesList.ItemsSource = _placeStore.Places;

        _placeStore.Places.CollectionChanged += Places_CollectionChanged;
        _store.Accounts.CollectionChanged += Accounts_CollectionChanged;

        if (_store.Accounts.Count == 0)
        {
            _store.Accounts.Add(new AccountRecord());
        }

        if (_placeStore.Places.Count == 0)
        {
            _placeStore.Places.Add(new PlaceEntry());
        }

        PlacesTabAccountCombo.ItemsSource = _store.Accounts;
        PlacesTabAccountCombo.DisplayMemberPath = nameof(AccountRecord.ListLabel);

        AccountsList.SelectedItem = _store.Accounts[0];
        PlacesList.SelectedItem = _placeStore.Places[0];
        PlacesTabAccountCombo.SelectedItem = _store.Accounts[0];

        RebuildExperienceComboForAccount();
        RebuildPlacesTabPlaceCombo(selectListMatch: true);
        MapPlaceSelectionToDetail();
        MapSelectionToDetail();

        _suppressAutoPersist = true;
        var savedAuto = _autoStore.Load();
        if (savedAuto is not null)
        {
            AutoClickEnabled.IsChecked = savedAuto.Enabled;
            CpsSlider.Value = savedAuto.ClicksPerSecond;
            InitialDelaySlider.Value = savedAuto.InitialDelayMs;
            ExtraDelaySlider.Value = savedAuto.ExtraDelayPerClickMs;
            AutoHotkeyEnabled.IsChecked = savedAuto.EnableKeybind;
            AutoHotkeyNotifyEnabled.IsChecked = savedAuto.NotifyOnKeybindToggle;
            SetHotkeyText(savedAuto.Keybind);
        }
        else
        {
            SetHotkeyText("F6");
        }
        _suppressAutoPersist = false;

        SetupHotkeyHook();
        RefreshAutoLabels();
        _autoClicker.SetRunning(true);
        PushAutoConfig();

        AppLog.UiLogLine += OnAppLogUiLine;
        AppLog.Info($"Loaded {_store.Accounts.Count} account(s), {_placeStore.Places.Count} saved place(s).");
        UpdateAddControls();
        _ = RefreshAllAccountsPresenceAsync();
    }

    private async void RefreshAccountsPresence_Click(object sender, RoutedEventArgs e) => await RefreshAllAccountsPresenceAsync();

    private async Task RefreshAllAccountsPresenceAsync()
    {
        if (Interlocked.Exchange(ref _presenceRefreshGate, 1) == 1)
        {
            return;
        }

        RefreshAccountsPresenceButton.IsEnabled = false;
        try
        {
            foreach (var a in _store.Accounts.ToList())
            {
                AccountPresenceKind kind;
                if (string.IsNullOrEmpty(a.ProtectedCookieBase64))
                {
                    kind = AccountPresenceKind.NoCookie;
                }
                else
                {
                    var raw = CredentialProtector.UnprotectFromBase64(a.ProtectedCookieBase64);
                    var clean = RobloxSessionCookie.Sanitize(raw);
                    if (string.IsNullOrEmpty(clean))
                    {
                        kind = AccountPresenceKind.NoCookie;
                    }
                    else
                    {
                        try
                        {
                            kind = await RobloxAccountPresenceService.QueryAsync(clean, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            AppLog.Line("PRESENCE", $"Could not refresh status for “{a.ListLabel}”: {ex.Message}");
                            kind = AccountPresenceKind.InvalidCookie;
                        }
                    }
                }

                await Dispatcher.InvokeAsync(() => { a.PresenceKind = kind; });
            }
        }
        finally
        {
            try
            {
                await Dispatcher.InvokeAsync(() => { RefreshAccountsPresenceButton.IsEnabled = true; });
            }
            finally
            {
                Interlocked.Exchange(ref _presenceRefreshGate, 0);
            }
        }
    }

    private void OnAppLogUiLine(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => OnAppLogUiLine(line));
            return;
        }

        AppConsoleBox.AppendText(line + Environment.NewLine);
        if (AppConsoleBox.Text.Length > ConsoleMaxChars)
        {
            var drop = AppConsoleBox.Text.Length - ConsoleMaxChars + 50_000;
            AppConsoleBox.Text = AppConsoleBox.Text[drop..];
        }

        AppConsoleBox.CaretIndex = AppConsoleBox.Text.Length;
        AppConsoleBox.ScrollToEnd();
    }

    private void ConsoleClear_Click(object sender, RoutedEventArgs e) => AppConsoleBox.Clear();

    private void Accounts_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            PlacesTabAccountCombo.ItemsSource = null;
            PlacesTabAccountCombo.ItemsSource = _store.Accounts;
            PlacesTabAccountCombo.DisplayMemberPath = nameof(AccountRecord.ListLabel);
            if (PlacesTabAccountCombo.SelectedItem is null && _store.Accounts.Count > 0)
            {
                PlacesTabAccountCombo.SelectedItem = _store.Accounts[0];
            }

            RebuildExperienceComboForAccount();
            RebuildPlacesTabPlaceCombo(selectListMatch: false);
            UpdateAddControls();
        });
    }

    private void Places_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            RebuildExperienceComboForAccount();
            RebuildPlacesTabPlaceCombo(selectListMatch: true);
            UpdateAddControls();
        });
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        UnregisterAutoHotkey();
        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }

        UnregisterMouseHotkey();
        _notifyIcon?.Dispose();
        _notifyIcon = null;

        AppLog.UiLogLine -= OnAppLogUiLine;
        _autoClicker.SetRunning(false);
        _autoClicker.Dispose();
        _store.Save();
        _placeStore.Save();
    }

    private sealed class ExperiencePick
    {
        public Guid? LinkedId { get; init; }
        public string Display { get; init; } = "";
        public long? PlaceId { get; init; }

        public override string ToString() => Display;
    }

    private void RebuildExperienceComboForAccount()
    {
        if (AccountsList.SelectedItem is not AccountRecord a)
        {
            return;
        }

        var items = new List<ExperiencePick>
        {
            new() { LinkedId = null, Display = "— Custom place ID —", PlaceId = null }
        };
        foreach (var p in _placeStore.Places)
        {
            if (p.PlaceId <= 0)
            {
                continue;
            }

            items.Add(new ExperiencePick
            {
                LinkedId = p.Id,
                Display = $"{p.ListLabel}  ·  {p.PlaceId}",
                PlaceId = p.PlaceId
            });
        }

        _suppressExperienceCombo = true;
        AccExperienceCombo.ItemsSource = items;
        AccExperienceCombo.DisplayMemberPath = nameof(ExperiencePick.Display);

        ExperiencePick? sel = items.FirstOrDefault(x => x.LinkedId == a.LinkedPlaceEntryId)
                             ?? items[0];
        AccExperienceCombo.SelectedItem = sel;
        _suppressExperienceCombo = false;
    }

    private void RebuildPlacesTabPlaceCombo(bool selectListMatch)
    {
        var items = new List<ExperiencePick>();
        foreach (var p in _placeStore.Places)
        {
            if (p.PlaceId <= 0)
            {
                continue;
            }

            items.Add(new ExperiencePick
            {
                LinkedId = p.Id,
                Display = $"{p.ListLabel}  ·  {p.PlaceId}",
                PlaceId = p.PlaceId
            });
        }

        _suppressPlacesPlaceCombo = true;
        PlacesTabPlaceCombo.ItemsSource = items;
        PlacesTabPlaceCombo.DisplayMemberPath = nameof(ExperiencePick.Display);

        if (items.Count == 0)
        {
            PlacesTabPlaceCombo.SelectedItem = null;
        }
        else if (selectListMatch && PlacesList.SelectedItem is PlaceEntry pl)
        {
            PlacesTabPlaceCombo.SelectedItem = items.FirstOrDefault(i => i.LinkedId == pl.Id) ?? items[0];
        }
        else if (PlacesTabPlaceCombo.SelectedItem is null)
        {
            PlacesTabPlaceCombo.SelectedItem = items[0];
        }

        _suppressPlacesPlaceCombo = false;
    }

    private void AccExperienceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressExperienceCombo || AccExperienceCombo.SelectedItem is not ExperiencePick pick)
        {
            return;
        }

        if (pick.LinkedId is null)
        {
            return;
        }

        if (pick.PlaceId is long pid)
        {
            AccPlaceId.Text = pid.ToString();
        }
    }

    private void EnableMultiInstance_Click(object sender, RoutedEventArgs e)
    {
        AppLog.Info("Enabling multi-instance (closing ROBLOX_singletonEvent handles)…");
        var results = _multi.EnableMultiInstance();
        if (results.Count == 0)
        {
            MultiInstanceStatus.Text = "No running RobloxPlayerBeta.exe. Start the game client first, then use this to allow more instances before opening another one.";
            MultiInstanceStatus.Foreground = System.Windows.Media.Brushes.LightSalmon;
            AppLog.Warn("No RobloxPlayerBeta.exe processes found.");
            return;
        }

        var any = false;
        var log = new System.Text.StringBuilder();
        foreach (var r in results)
        {
            var suffix = r.Succeeded ? "OK" : $"failed ({r.Detail})";
            log.AppendLine($"- PID {r.ProcessId}: handle(s) removed {r.HandlesClosed}, result {suffix}.");
            if (r.HandlesClosed > 0)
            {
                any = true;
            }

            AppLog.Line("MULTI", $"PID {r.ProcessId}: closed {r.HandlesClosed} handle(s), ok={r.Succeeded}");
        }

        MultiInstanceStatus.Text = (any
            ? "Singleton handle(s) closed. You can launch or join on another account.\n"
            : "No ROBLOX_singletonEvent handle was closed. Start Roblox first, or run the app as Administrator. Details:\n")
            + log;
        MultiInstanceStatus.Foreground = any
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(155, 199, 155))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 180, 120));
        if (any)
        {
            AppLog.Ok("Multi-instance step finished (handles removed).");
        }
        else
        {
            AppLog.Warn("Multi-instance: no singleton handle was removed.");
        }
    }

    private void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        if (_store.Accounts.Any(a => string.IsNullOrWhiteSpace(a.DisplayName)))
        {
            _ = System.Windows.MessageBox.Show(
                "The current account needs a display name. Save it or remove it before you add another.",
                "Roblox Utility",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var a = new AccountRecord();
        _store.Accounts.Add(a);
        AccountsList.SelectedItem = a;
        AppLog.Line("SAVE", "Added a new account row.");
        UpdateAddControls();
    }

    private void DeleteAccount_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsList.SelectedItem is not AccountRecord a)
        {
            return;
        }

        _store.Accounts.Remove(a);
        if (_store.Accounts.Count == 0)
        {
            _store.Accounts.Add(new AccountRecord());
        }

        AccountsList.SelectedItem = _store.Accounts[0];
        AppLog.Line("SAVE", "Removed an account.");
        UpdateAddControls();
    }

    private void AccountsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => MapSelectionToDetail();

    private void MapSelectionToDetail()
    {
        if (AccountsList.SelectedItem is not AccountRecord a)
        {
            AccountDetailPanel.IsEnabled = false;
            return;
        }

        AccountDetailPanel.IsEnabled = true;
        AccName.Text = a.DisplayName;
        AccPlaceId.Text = a.DefaultPlaceId > 0 ? a.DefaultPlaceId.ToString() : string.Empty;
        AccCookie.Password = string.Empty;
        if (!string.IsNullOrEmpty(a.ProtectedCookieBase64))
        {
            var c = CredentialProtector.UnprotectFromBase64(a.ProtectedCookieBase64);
            if (!string.IsNullOrEmpty(c))
            {
                AccCookie.Password = c;
            }
        }

        AccCookieStatus.Text = a.ProtectedCookieBase64 is not null
            ? "A saved .ROBLOSECURITY token is on disk. Type a new value and Save to replace it, or use Launch with the last saved one."
            : "No token saved for this account yet. Paste a .ROBLOSECURITY value, then Save.";

        RebuildExperienceComboForAccount();
    }

    private void SaveAccount_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsList.SelectedItem is not AccountRecord a)
        {
            return;
        }

        var name = (AccName.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name))
        {
            _ = System.Windows.MessageBox.Show("Display name is required (cannot be blank).", "Roblox Utility", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (AccExperienceCombo.SelectedItem is not ExperiencePick pick)
        {
            return;
        }

        if (pick.LinkedId is null)
        {
            if (!PlaceIdValidation.TryParse(AccPlaceId.Text, out var pl))
            {
                _ = System.Windows.MessageBox.Show(
                    "Set a place ID: digits only, no letters or other symbols, and a valid Roblox universe ID (1 or more). " +
                    "Or pick a saved place from the list (not the Custom line).",
                    "Roblox Utility",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            a.DefaultPlaceId = pl;
            a.LinkedPlaceEntryId = null;
        }
        else
        {
            if (pick.PlaceId is not long pld)
            {
                return;
            }

            a.DefaultPlaceId = pld;
            a.LinkedPlaceEntryId = pick.LinkedId;
        }

        a.DisplayName = name;

        if (!string.IsNullOrEmpty(AccCookie.Password))
        {
            a.ProtectedCookieBase64 = CredentialProtector.ProtectToBase64(AccCookie.Password);
        }
        else if (string.IsNullOrEmpty(a.ProtectedCookieBase64))
        {
            a.ProtectedCookieBase64 = null;
        }

        _store.Save();
        AccCookieStatus.Text = "Saved.";
        if (TryFindResource("SuccessBrush") is System.Windows.Media.Brush okBr)
        {
            AccCookieStatus.Foreground = okBr;
        }

        AccountsList.Items.Refresh();
        RebuildExperienceComboForAccount();
        RebuildPlacesTabPlaceCombo(selectListMatch: true);
        UpdateAddControls();
        AppLog.Line("SAVE", $"Account '{a.DisplayName}' saved (place ID {a.DefaultPlaceId}).");
    }

    private void UpdateAddControls()
    {
        AddAccountButton.IsEnabled = !_store.Accounts.Any(x => string.IsNullOrWhiteSpace(x.DisplayName));
        AddPlaceButton.IsEnabled = !_placeStore.Places.Any(p => string.IsNullOrWhiteSpace(p.Name) && p.PlaceId == 0);
    }

    private async void LaunchAccount_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsList.SelectedItem is not AccountRecord a)
        {
            return;
        }

        await LaunchAccountForPlaceAsync(a, status => AccountLaunchStatus.Text = status);
    }

    private async Task LaunchAccountForPlaceAsync(AccountRecord a, Action<string> setStatus)
    {
        if (!string.IsNullOrEmpty(AccCookie.Password))
        {
            a.ProtectedCookieBase64 = CredentialProtector.ProtectToBase64(AccCookie.Password);
            _store.Save();
        }

        var b64 = a.ProtectedCookieBase64;
        if (string.IsNullOrEmpty(b64))
        {
            setStatus("Set and save a .ROBLOSECURITY cookie for this account first.");
            AppLog.Warn("Launch aborted: no cookie for account.");
            return;
        }

        var placeId = a.DefaultPlaceId;
        if (PlaceIdValidation.TryParse(AccPlaceId.Text, out var pl))
        {
            a.DefaultPlaceId = pl;
            placeId = pl;
        }
        else if (AccExperienceCombo.SelectedItem is ExperiencePick ep && ep.PlaceId is long fromSaved)
        {
            placeId = fromSaved;
        }

        if (placeId <= 0)
        {
            setStatus("Set a place to join: pick a saved experience, or type a place ID in the custom field below.");
            AppLog.Warn("Launch aborted: no place ID.");
            return;
        }

        if (_multi.CountRobloxInstances() > 0)
        {
            var m = _multi.EnableMultiInstance();
            var n = 0;
            foreach (var r in m)
            {
                n += r.HandlesClosed;
            }

            setStatus(n > 0
                ? $"Multi-instance: closed {n} singleton handle(s). Requesting join…"
                : "Multi-instance: no handle removed. Requesting join…");
        }
        else
        {
            setStatus("Requesting place join…");
        }

        var cookie = CredentialProtector.UnprotectFromBase64(b64);
        AppLog.Line("LAUNCH", $"Join place {placeId} as '{a.DisplayName}'…");
        var r2 = await RobloxLaunchService.LaunchWithCookieAsync(cookie ?? string.Empty, placeId);
        setStatus(r2.Message);
        if (r2.Ok)
        {
            AppLog.Ok($"Launch: {r2.Message}");
        }
        else
        {
            AppLog.Err($"Launch failed: {r2.Message}");
        }
    }

    private async void ClientOnly_Click(object sender, RoutedEventArgs e)
    {
        if (_multi.CountRobloxInstances() > 0)
        {
            _multi.EnableMultiInstance();
        }

        if (AccountsList.SelectedItem is not AccountRecord a)
        {
            AccountLaunchStatus.Text = "Select an account in the list first. \"Client only\" uses that account’s saved .ROBLOSECURITY.";
            AppLog.Warn("Client only: no account selected.");
            return;
        }

        if (!string.IsNullOrEmpty(AccCookie.Password))
        {
            a.ProtectedCookieBase64 = CredentialProtector.ProtectToBase64(AccCookie.Password);
            _store.Save();
        }

        var b64 = a.ProtectedCookieBase64;
        if (string.IsNullOrEmpty(b64))
        {
            AccountLaunchStatus.Text = "Set and save a .ROBLOSECURITY for this account first (or paste in the field and save).";
            return;
        }

        AppLog.Info("Starting Roblox for the selected account (session from cookie)…");
        var cookie = CredentialProtector.UnprotectFromBase64(b64);
        var r = await RobloxLaunchService.StartRobloxClientWithCookieAsync(cookie ?? string.Empty);
        AccountLaunchStatus.Text = r.Message;
        if (r.Ok)
        {
            AppLog.Ok(r.Message);
        }
        else
        {
            AppLog.Err(r.Message);
        }
    }

    private void AddPlace_Click(object sender, RoutedEventArgs e)
    {
        if (_placeStore.Places.Any(p => string.IsNullOrWhiteSpace(p.Name) && p.PlaceId == 0))
        {
            _ = System.Windows.MessageBox.Show(
                "Fill in the current place (name and a valid place ID) and save it, or remove it, before you add another.",
                "Roblox Utility",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var p = new PlaceEntry();
        _placeStore.Places.Add(p);
        PlacesList.SelectedItem = p;
        AppLog.Line("SAVE", "Added a new place row.");
        UpdateAddControls();
    }

    private void DeletePlace_Click(object sender, RoutedEventArgs e)
    {
        if (PlacesList.SelectedItem is not PlaceEntry p)
        {
            return;
        }

        _placeStore.Places.Remove(p);
        if (_placeStore.Places.Count == 0)
        {
            _placeStore.Places.Add(new PlaceEntry());
        }

        PlacesList.SelectedItem = _placeStore.Places[0];
        _placeStore.Save();
        AppLog.Line("SAVE", "Removed a saved place.");
        UpdateAddControls();
    }

    private void PlacesTabPlaceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPlacesPlaceCombo || PlacesTabPlaceCombo.SelectedItem is not ExperiencePick pick || pick.LinkedId is not Guid id)
        {
            return;
        }

        var match = _placeStore.Places.FirstOrDefault(p => p.Id == id);
        if (match is not null)
        {
            PlacesList.SelectedItem = match;
        }
    }

    private void PlacesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        MapPlaceSelectionToDetail();
        if (!_suppressPlacesPlaceCombo && PlacesList.SelectedItem is PlaceEntry pl)
        {
            _suppressPlacesPlaceCombo = true;
            if (PlacesTabPlaceCombo.ItemsSource is IEnumerable<ExperiencePick> picks)
            {
                PlacesTabPlaceCombo.SelectedItem = picks.FirstOrDefault(x => x.LinkedId == pl.Id);
            }

            _suppressPlacesPlaceCombo = false;
        }
    }

    private void MapPlaceSelectionToDetail()
    {
        if (PlacesList.SelectedItem is not PlaceEntry p)
        {
            PlaceDetailPanel.IsEnabled = false;
            return;
        }

        PlaceDetailPanel.IsEnabled = true;
        PlaceNameBox.Text = p.Name;
        PlaceIdBox.Text = p.PlaceId > 0 ? p.PlaceId.ToString() : string.Empty;
        PlacesTabStatus.Text = string.Empty;
    }

    private void SavePlace_Click(object sender, RoutedEventArgs e)
    {
        if (PlacesList.SelectedItem is not PlaceEntry p)
        {
            return;
        }

        var name = (PlaceNameBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name))
        {
            _ = System.Windows.MessageBox.Show("Display name is required (cannot be blank).", "Roblox Utility", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!PlaceIdValidation.TryParse(PlaceIdBox.Text, out var id))
        {
            _ = System.Windows.MessageBox.Show(
                "Place ID must be a valid Roblox universe ID: digits only (no letters or symbols), between 1 and 9,223,372,036,854,775,807.",
                "Roblox Utility",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        p.Name = name;
        p.PlaceId = id;
        _placeStore.Save();
        PlacesList.Items.Refresh();
        RebuildExperienceComboForAccount();
        RebuildPlacesTabPlaceCombo(selectListMatch: true);
        UpdateAddControls();
        AppLog.Line("SAVE", $"Place '{p.Name}' ({p.PlaceId}) saved.");
    }

    private async void PlacesTabLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (PlacesTabAccountCombo.SelectedItem is not AccountRecord acc)
        {
            PlacesTabStatus.Text = "Select an account.";
            AppLog.Warn("Places tab launch: no account selected.");
            return;
        }

        long placeId;
        if (PlacesTabPlaceCombo.SelectedItem is ExperiencePick pick && pick.PlaceId is long pid)
        {
            placeId = pid;
        }
        else if (PlacesList.SelectedItem is PlaceEntry pl)
        {
            placeId = pl.PlaceId;
        }
        else if (PlaceIdValidation.TryParse(PlaceIdBox.Text, out var manual))
        {
            placeId = manual;
        }
        else
        {
            PlacesTabStatus.Text = "Select an experience or enter a valid place ID.";
            AppLog.Warn("Places tab launch: no valid place.");
            return;
        }

        if (_multi.CountRobloxInstances() > 0)
        {
            _multi.EnableMultiInstance();
        }

        var b64 = acc.ProtectedCookieBase64;
        if (string.IsNullOrEmpty(b64))
        {
            PlacesTabStatus.Text = $"Account '{acc.DisplayName}' has no saved cookie. Add one on the Accounts tab.";
            AppLog.Warn($"Places tab launch: account '{acc.DisplayName}' has no cookie.");
            return;
        }

        var cookie = CredentialProtector.UnprotectFromBase64(b64);
        AppLog.Line("LAUNCH", $"Places tab: join {placeId} as '{acc.DisplayName}'…");
        var r = await RobloxLaunchService.LaunchWithCookieAsync(cookie ?? string.Empty, placeId);
        PlacesTabStatus.Text = r.Message;
        if (r.Ok)
        {
            AppLog.Ok($"Places tab launch: {r.Message}");
        }
        else
        {
            AppLog.Err($"Places tab launch: {r.Message}");
        }
    }

    private void AutoClickSetting_Changed(object sender, RoutedEventArgs e)
    {
        RefreshAutoLabels();
        PushAutoConfig();
    }

    private void KeybindSetting_Changed(object sender, RoutedEventArgs e)
    {
        RefreshAutoLabels();
        PushAutoConfig();
    }

    private void AutoHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _awaitingHotkeyInput = true;
        AutoHotkeyButton.Content = "Awaiting input...";
        AutoHotkeyButton.Focus();
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_awaitingHotkeyInput)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            _awaitingHotkeyInput = false;
            AutoHotkeyButton.Content = ToHotkeyDisplay(_selectedHotkeyCanonical);
            RefreshAutoLabels();
            e.Handled = true;
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        _awaitingHotkeyInput = false;
        SetHotkeyText(NormalizeKeyText(key));
        RefreshAutoLabels();
        PushAutoConfig();
        e.Handled = true;
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_awaitingHotkeyInput)
        {
            return;
        }

        string? mouseBind = e.ChangedButton switch
        {
            MouseButton.Middle => "MIDDLEMOUSE",
            MouseButton.XButton1 => "XBUTTON1",
            MouseButton.XButton2 => "XBUTTON2",
            _ => null
        };
        if (mouseBind is null)
        {
            return;
        }

        _awaitingHotkeyInput = false;
        SetHotkeyText(mouseBind);
        RefreshAutoLabels();
        PushAutoConfig();
        e.Handled = true;
    }

    private void RefreshAutoLabels()
    {
        if (AutoStatus is null || AutoHotkeyEnabled is null || AutoHotkeyNotifyEnabled is null || AutoHotkeyButton is null)
        {
            return;
        }

        CpsBigValue.Text = ((int)CpsSlider.Value).ToString();
        InitialDelayBigValue.Text = ((int)InitialDelaySlider.Value).ToString();
        ExtraDelayBigValue.Text = ((int)ExtraDelaySlider.Value).ToString();
        var en = AutoClickEnabled.IsChecked == true;
        var keybind = GetSelectedHotkeyDisplay();
        var keybindOn = AutoHotkeyEnabled.IsChecked == true;
        var notifyOnToggle = AutoHotkeyNotifyEnabled.IsChecked == true;
        var keybindState = _awaitingHotkeyInput
            ? "Awaiting input..."
            : keybindOn
            ? (_hotkeyRegistered ? $"Keybind ON ({keybind}) — press to toggle auto clicker." : $"Keybind ON ({keybind}) — could not register (already in use).")
            : "Keybind OFF.";
        var notifyState = notifyOnToggle ? "Notifications ON." : "Notifications OFF.";
        AutoStatus.Text = (en
            ? "Enabled — when Roblox is focused, hold the left mouse button to auto-click at the rate above."
            : "Disabled — turn on to activate (no clicks are sent while off).")
            + " " + keybindState + " " + notifyState;
    }

    private void PushAutoConfig()
    {
        var cfg = new AutoClickerConfig
        {
            Enabled = AutoClickEnabled.IsChecked == true,
            ClicksPerSecond = CpsSlider.Value,
            InitialDelayMs = (int)InitialDelaySlider.Value,
            ExtraDelayPerClickMs = (int)ExtraDelaySlider.Value,
            EnableKeybind = AutoHotkeyEnabled.IsChecked == true,
            Keybind = GetSelectedHotkeyText(),
            NotifyOnKeybindToggle = AutoHotkeyNotifyEnabled.IsChecked == true
        };

        _autoClicker.UpdateConfig(cfg);
        UpdateAutoHotkeyRegistration(cfg);
        if (!_suppressAutoPersist)
        {
            _autoStore.Save(cfg);
        }
    }

    private void SetHotkeyText(string keyText)
    {
        _selectedHotkeyCanonical = string.IsNullOrWhiteSpace(keyText) ? "F6" : keyText.Trim().ToUpperInvariant();
        AutoHotkeyButton.Content = ToHotkeyDisplay(_selectedHotkeyCanonical);
    }

    private string GetSelectedHotkeyText()
    {
        return _selectedHotkeyCanonical;
    }

    private string GetSelectedHotkeyDisplay()
    {
        return AutoHotkeyButton.Content?.ToString() ?? ToHotkeyDisplay(_selectedHotkeyCanonical);
    }

    private void SetupHotkeyHook()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);
    }

    private void UpdateAutoHotkeyRegistration(AutoClickerConfig cfg)
    {
        UnregisterAutoHotkey();
        if (!cfg.EnableKeybind || _hwndSource is null)
        {
            return;
        }

        var bind = ParseHotkeyBinding(cfg.Keybind, out var vk);
        if (bind == HotkeyBindingType.Keyboard)
        {
            if (vk == 0)
            {
                vk = 0x75; // F6
            }

            var hwnd = _hwndSource.Handle;
            _hotkeyRegistered = NativeMethods.RegisterHotKey(hwnd, AutoHotkeyId, 0, vk);
            if (!_hotkeyRegistered)
            {
                AppLog.Warn($"Auto clicker keybind '{cfg.Keybind}' could not register (already used by another app).");
            }

            return;
        }

        if (bind is HotkeyBindingType.MouseMiddle or HotkeyBindingType.MouseXButton1 or HotkeyBindingType.MouseXButton2)
        {
            RegisterMouseHotkey(bind);
            _hotkeyRegistered = _mouseHotkeyHook != nint.Zero;
            if (!_hotkeyRegistered)
            {
                AppLog.Warn($"Auto clicker keybind '{cfg.Keybind}' could not register.");
            }

            return;
        }

        AppLog.Warn($"Auto clicker keybind '{cfg.Keybind}' is not valid.");
    }

    private void UnregisterAutoHotkey()
    {
        UnregisterMouseHotkey();

        if (_hwndSource is null || !_hotkeyRegistered)
        {
            _hotkeyRegistered = false;
            return;
        }

        _ = NativeMethods.UnregisterHotKey(_hwndSource.Handle, AutoHotkeyId);
        _hotkeyRegistered = false;
    }

    private void RegisterMouseHotkey(HotkeyBindingType binding)
    {
        _mouseBindingType = binding;
        _mouseHotkeyProc ??= MouseHotkeyHookCallback;
        var module = NativeMethods.GetModuleHandle(null);
        _mouseHotkeyHook = NativeMethods.SetWindowsHookEx(NativeMethods.WhMouseLl, _mouseHotkeyProc, module, 0);
    }

    private void UnregisterMouseHotkey()
    {
        if (_mouseHotkeyHook != nint.Zero)
        {
            _ = NativeMethods.UnhookWindowsHookEx(_mouseHotkeyHook);
            _mouseHotkeyHook = nint.Zero;
        }

        _mouseBindingType = HotkeyBindingType.None;
    }

    private nint MouseHotkeyHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && lParam != nint.Zero)
        {
            var info = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            var injected = (info.Flags & (NativeMethods.LlmhfInjected | NativeMethods.LlmhfLowerIlInjected)) != 0;
            if (!injected && IsMatchingMouseBinding(wParam, info))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    AutoClickEnabled.IsChecked = AutoClickEnabled.IsChecked != true;
                    if (AutoHotkeyNotifyEnabled.IsChecked == true)
                    {
                        ShowToggleNotification(AutoClickEnabled.IsChecked == true);
                    }
                });
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHotkeyHook, nCode, wParam, lParam);
    }

    private bool IsMatchingMouseBinding(nint wParam, NativeMethods.MSLLHOOKSTRUCT info)
    {
        return _mouseBindingType switch
        {
            HotkeyBindingType.MouseMiddle => wParam == NativeMethods.WmMButtonDown,
            HotkeyBindingType.MouseXButton1 => wParam == NativeMethods.WmXButtonDown && ((info.MouseData >> 16) & 0xFFFF) == 1,
            HotkeyBindingType.MouseXButton2 => wParam == NativeMethods.WmXButtonDown && ((info.MouseData >> 16) & 0xFFFF) == 2,
            _ => false
        };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == AutoHotkeyId)
        {
            AutoClickEnabled.IsChecked = AutoClickEnabled.IsChecked != true;
            if (AutoHotkeyNotifyEnabled.IsChecked == true)
            {
                ShowToggleNotification(AutoClickEnabled.IsChecked == true);
            }

            handled = true;
        }

        return IntPtr.Zero;
    }

    private static string NormalizeKeyText(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            var c = (char)('A' + (key - Key.A));
            return c.ToString();
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            var c = (char)('0' + (key - Key.D0));
            return c.ToString();
        }

        return key.ToString().ToUpperInvariant();
    }

    private static string ToHotkeyDisplay(string canonical)
    {
        var t = (canonical ?? string.Empty).Trim().ToUpperInvariant();
        return t switch
        {
            "MIDDLEMOUSE" or "MMB" => "Middle Mouse",
            "XBUTTON1" or "MOUSE4" or "SIDE1" => "Mouse Side 1",
            "XBUTTON2" or "MOUSE5" or "SIDE2" => "Mouse Side 2",
            _ => t
        };
    }

    private static HotkeyBindingType ParseHotkeyBinding(string text, out uint vk)
    {
        vk = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return HotkeyBindingType.None;
        }

        var t = text.Trim().ToUpperInvariant();
        if (t is "MIDDLEMOUSE" or "MMB")
        {
            return HotkeyBindingType.MouseMiddle;
        }

        if (t is "XBUTTON1" or "MOUSE4" or "SIDE1")
        {
            return HotkeyBindingType.MouseXButton1;
        }

        if (t is "XBUTTON2" or "MOUSE5" or "SIDE2")
        {
            return HotkeyBindingType.MouseXButton2;
        }

        if (t.Length == 1 && t[0] >= 'A' && t[0] <= 'Z')
        {
            vk = (uint)t[0];
            return HotkeyBindingType.Keyboard;
        }

        if (t.Length == 1 && t[0] >= '0' && t[0] <= '9')
        {
            vk = (uint)t[0];
            return HotkeyBindingType.Keyboard;
        }

        if (Enum.TryParse<Key>(t, ignoreCase: true, out var key))
        {
            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            return vk != 0 ? HotkeyBindingType.Keyboard : HotkeyBindingType.None;
        }

        if (t.StartsWith("NUMPAD") && int.TryParse(t["NUMPAD".Length..], out var np) && np >= 0 && np <= 9)
        {
            vk = 0x60u + (uint)np;
            return HotkeyBindingType.Keyboard;
        }

        return HotkeyBindingType.None;
    }

    private void ShowToggleNotification(bool enabled)
    {
        _notifyIcon ??= new Forms.NotifyIcon
        {
            Icon = SystemIcons.Information,
            Visible = true
        };

        _notifyIcon.BalloonTipTitle = "Roblox Utility";
        _notifyIcon.BalloonTipText = enabled ? "Auto clicker activated (keybind)." : "Auto clicker deactivated (keybind).";
        _notifyIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(2000);
    }
}
