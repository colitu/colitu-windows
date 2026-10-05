using System.Windows.Controls;
using v2rayN.Services;

namespace v2rayN.Views;

public partial class ColituMainWindow
{
    private ColituUpdateInfo? _pendingUpdate;
    private bool _updateBusy;

    // ── Devices ────────────────────────────────────────────────────────────
    private async Task LoadDevicesAsync()
    {
        if (DeviceList == null || !_auth.HasSession || AppView.Visibility != Visibility.Visible)
        {
            return;
        }
        var epoch = _accountEpoch;
        try
        {
            var account = await _auth.LoadAccountAsync();
            if (epoch != _accountEpoch)
            {
                return;
            }
            var limit = Math.Max(1, account.Subscription?.DeviceLimit ?? 1);
            DevicesTitle.Text = Loc.I.Format("account.devicesTitle", ("used", account.Devices.Count), ("limit", limit));
            DeviceList.ItemsSource = account.Devices
                .OrderByDescending(device => device.Current)
                .Select(device => new ColituDeviceRow
                {
                    Id = device.Id ?? "",
                    Name = device.Name ?? "",
                    IsCurrent = device.Current,
                    Detail = string.Join(" · ", new[]
                    {
                        PlatformName(device.Platform),
                        DateTimeOffset.TryParse(device.LastActiveAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var seen)
                            ? Loc.I.Format("account.lastSeen", ("date", FormatDate(seen)))
                            : null
                    }.Where(part => !string.IsNullOrWhiteSpace(part)))
                })
                .ToList();
            ApplyAccount();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.LoadDevicesAsync", ex);
        }
    }

    private async void RemoveDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ColituDeviceRow device })
        {
            return;
        }

        var confirm = MessageBox.Show(this, Loc.I.Format("account.removeConfirm", ("name", device.Name)), "Colitu VPN", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            // Removing this computer ends the session; the session-expired handler then
            // disconnects and forgets the account. Disconnecting first would leave the user
            // offline (but signed in) whenever the removal itself fails.
            await _auth.RemoveDeviceAsync(device.Id);
            if (!device.IsCurrent)
            {
                await LoadDevicesAsync();
            }
        }
        catch (Exception ex)
        {
            ShowToast(ex is ColituApiException api ? api.Message : Loc.I["err.generic"], true);
        }
    }

    private static string? PlatformName(string? platform) => platform?.Trim().ToLowerInvariant() switch
    {
        "windows" => "Windows",
        "ios" => "iOS",
        "android" => "Android",
        "macos" or "mac" => "macOS",
        "linux" => "Linux",
        null or "" => null,
        var other => other
    };

    private void ManageAccount_Click(object sender, RoutedEventArgs e) => OpenUrl(LocalizedPath("/account"));

    private void Support_Click(object sender, RoutedEventArgs e) => Navigate("support");

    private void MailSupport_Click(object sender, RoutedEventArgs e) => OpenUrl($"mailto:{ColituAuthService.SupportEmail}");

    // ── Settings ───────────────────────────────────────────────────────────
    private async void Startup_Click(object sender, RoutedEventArgs e)
    {
        if (_applyingPreferences || !IsLoaded || (SettingsStartup.IsChecked == true) == _vpn.LaunchAtStartup)
        {
            return;
        }
        try
        {
            await _vpn.SetLaunchAtStartupAsync(SettingsStartup.IsChecked == true);
            ShowToast(Loc.I["settings.saved"]);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.Startup_Click", ex);
            _applyingPreferences = true;
            SettingsStartup.IsChecked = _vpn.LaunchAtStartup;
            _applyingPreferences = false;
            ShowToast(Loc.I["err.generic"], true);
        }
    }

    private void Website_Click(object sender, RoutedEventArgs e) => OpenUrl(ColituAuthService.WebBaseUrl);

    private void Privacy_Click(object sender, RoutedEventArgs e) => OpenUrl(LocalizedPath("/legal/privacy"));

    private void Terms_Click(object sender, RoutedEventArgs e) => OpenUrl(LocalizedPath("/legal/terms"));

    private void Logs_Click(object sender, RoutedEventArgs e) => ColituShell.OpenFolder(Utils.GetLogPath());

    // ── Updates ────────────────────────────────────────────────────────────
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(quiet: false);

    private async Task CheckForUpdatesAsync(bool quiet)
    {
        if (_updateBusy)
        {
            return;
        }
        if (!quiet)
        {
            CheckUpdatesButton.IsEnabled = false;
            CheckUpdatesText.Text = Loc.I["settings.checking"];
        }
        try
        {
            var update = await _updater.CheckForUpdateAsync(ignoreAttemptCache: !quiet);
            if (update == null && !quiet)
            {
                // Offline or a rejected manifest is not "up to date".
                ShowToast(Loc.I[_updater.LastCheckFailed ? "settings.updateCheckFailed" : "settings.upToDate"], _updater.LastCheckFailed);
            }
        }
        finally
        {
            if (!quiet)
            {
                CheckUpdatesButton.IsEnabled = true;
                // Back to the language-following binding (setting Text replaced it).
                CheckUpdatesText.SetBinding(TextBlock.TextProperty, new Binding("[settings.checkUpdates]") { Source = Loc.I, Mode = BindingMode.OneWay });
            }
        }
    }

    private void ShowUpdate(ColituUpdateInfo info)
    {
        var first = _pendingUpdate?.VersionCode != info.VersionCode;
        _pendingUpdate = info;
        UpdateButtonText.Text = Loc.I.Format("update.available", ("version", info.NewVersion));
        UpdateButton.Visibility = Visibility.Visible;
        FadeIn(UpdateButton, 6);
        // Ask once per start and version; "Later" keeps the title-bar button.
        if (first && !_updateBusy)
        {
            ShowUpdatePrompt(info);
        }
    }

    private void ShowUpdatePrompt(ColituUpdateInfo info)
    {
        UpdatePromptTitle.Text = Loc.I.Format("update.title", ("version", info.NewVersion));
        UpdatePromptBody.Text = Loc.I[info.Force ? "update.force" : "update.ask"];
        UpdatePromptStatus.Visibility = Visibility.Collapsed;
        UpdatePromptLater.Visibility = info.Force ? Visibility.Collapsed : Visibility.Visible;
        SetUpdatePromptBusy(false);
        UpdatePrompt.Visibility = Visibility.Visible;
        FadeIn(UpdatePrompt, 8);
    }

    private void SetUpdatePromptBusy(bool busy)
    {
        UpdatePromptNow.IsEnabled = !busy;
        UpdatePromptLater.IsEnabled = !busy;
        UpdatePromptSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdatePromptNowText.Text = Loc.I["update.now"];
    }

    private void UpdatePromptLater_Click(object sender, RoutedEventArgs e) => UpdatePrompt.Visibility = Visibility.Collapsed;

    private async void UpdatePromptNow_Click(object sender, RoutedEventArgs e) => await InstallUpdateAsync();

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is { } info && !_updateBusy)
        {
            ShowUpdatePrompt(info);
            await InstallUpdateAsync();
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (_pendingUpdate is not { } info || _updateBusy)
        {
            return;
        }
        _updateBusy = true;
        UpdateButton.IsEnabled = false;
        SetUpdatePromptBusy(true);
        UpdatePromptStatus.Text = Loc.I["update.restart"];
        UpdatePromptStatus.Visibility = Visibility.Visible;
        try
        {
            var progress = new Progress<ColituDownloadProgress>(value =>
            {
                var text = Loc.I.Format("update.downloading", ("percent", value.Percent));
                UpdateButtonText.Text = text;
                UpdatePromptNowText.Text = text;
            });
            await _updater.DownloadUpdateAsync(info, progress);
            UpdateButtonText.Text = Loc.I["update.installing"];
            UpdatePromptNowText.Text = Loc.I["update.installing"];
            if (_vpn.Status is ColituVpnStatus.Connected or ColituVpnStatus.Connecting or ColituVpnStatus.Reconnecting)
            {
                await _vpn.DisconnectAsync();
            }
            _exiting = true;
            _updater.LaunchUpdaterAndExit(info);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.InstallUpdateAsync", ex);
            _exiting = false;
            ShowToast(Loc.I["update.failed"], true);
            UpdateButtonText.Text = Loc.I.Format("update.available", ("version", info.NewVersion));
            UpdatePromptStatus.Text = Loc.I["update.failed"];
        }
        finally
        {
            _updateBusy = false;
            UpdateButton.IsEnabled = true;
            SetUpdatePromptBusy(false);
        }
    }
}

public sealed class ColituDeviceRow
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public bool IsCurrent { get; init; }
    public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
}
