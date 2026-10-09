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
                    IsPaused = device.Paused,
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

    /// <summary>Makes a paused device active again (the panel pauses another one in its place).</summary>
    private async void ActivateDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ColituDeviceRow device } button)
        {
            return;
        }
        button.IsEnabled = false;
        try
        {
            if (device.IsCurrent)
            {
                await _auth.ActivateThisDeviceAsync();
                HidePaused();
            }
            else
            {
                await _auth.ActivateDeviceAsync(device.Id);
            }
            ShowToast(Loc.I.Format("account.activated", ("name", device.Name)));
            await LoadDevicesAsync();
        }
        catch (Exception ex)
        {
            ShowToast(ex is ColituApiException api ? api.Message : Loc.I["err.generic"], true);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    /// <summary>Two-factor authentication is set up on the website only.</summary>
    private void Security_Click(object sender, RoutedEventArgs e) => OpenUrl(ColituAuthService.SecuritySettingsUrl);

    /// <summary>Exporting a configuration for other VPN clients is a website page for now.</summary>
    private const string ManualConfigUrl = "https://colitu.com/account/manual-config";

    private void ManualConfig_Click(object sender, RoutedEventArgs e) => OpenUrl(ManualConfigUrl);

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

    // ── Rotating IP ────────────────────────────────────────────────────────
    private bool _rotationHooked;
    private bool _applyingRotation;
    private bool _savingRotation;
    /// <summary>The checklist while it holds a choice the panel would refuse (fewer than two countries).</summary>
    private List<string>? _rotationDraft;

    private void HookRotationUi()
    {
        if (_rotationHooked)
        {
            return;
        }
        _rotationHooked = true;
        // The preference lives on the account: read it whenever the settings open.
        SettingsPage.IsVisibleChanged += async (_, e) =>
        {
            if (e.NewValue is true && _auth.HasSession)
            {
                try
                {
                    await _vpn.LoadRotationAsync();
                    _rotationDraft = null;
                }
                catch (Exception ex)
                {
                    Logging.SaveLog("ColituMainWindow.LoadRotation", ex);
                }
                ApplyRotationUi();
            }
        };
        Loc.I.Changed += () => Dispatcher.BeginInvoke(ApplyRotationUi);
    }

    private void ApplyRotationUi()
    {
        if (RotationCard == null)
        {
            return;
        }
        HookRotationUi();
        var preference = _vpn.Rotation;
        // An older panel has no rotation: the card stays hidden.
        RotationCard.Visibility = preference == null || !_vpn.AdvancedMode ? Visibility.Collapsed : Visibility.Visible;
        if (preference == null)
        {
            return;
        }

        _applyingRotation = true;
        try
        {
            RotationIntervals.Children.Clear();
            foreach (var seconds in ColituRotation.IntervalChoices.Where(value => value == 0 || preference.Intervals.Count == 0 || preference.Intervals.Contains(value)))
            {
                var option = new RadioButton
                {
                    Content = seconds == 0 ? Loc.I["rotation.off"] : Loc.I.Format("rotation.minutes", ("n", seconds / 60)),
                    GroupName = "RotationInterval",
                    Tag = seconds,
                    FontSize = 12.5,
                    Style = (Style)FindResource("SegmentOption"),
                    IsChecked = seconds == preference.IntervalSeconds
                };
                option.Checked += RotationInterval_Checked;
                RotationIntervals.Children.Add(option);
            }

            // Russia (and any country outside the default set) is listed unchecked until the user ticks it.
            RotationCountriesPanel.Visibility = preference.Active ? Visibility.Visible : Visibility.Collapsed;
            var selected = _rotationDraft ?? ColituRotation.SelectedCountries(preference);
            RotationCountries.Children.Clear();
            foreach (var country in preference.AvailableCountries
                         .Where(item => item.Exits > 0)
                         .OrderBy(item => ColituServerRow.CountryName(item.Country) ?? item.Country, StringComparer.Create(Loc.I.Culture, true)))
            {
                var box = new CheckBox
                {
                    Content = ColituServerRow.CountryName(country.Country) ?? country.Country,
                    Tag = country.Country,
                    Style = (Style)FindResource("FieldCheck"),
                    MinWidth = 150,
                    Margin = new Thickness(2, 0, 12, 10),
                    IsChecked = selected.Contains(country.Country, StringComparer.OrdinalIgnoreCase)
                };
                box.Checked += RotationCountry_Changed;
                box.Unchecked += RotationCountry_Changed;
                RotationCountries.Children.Add(box);
            }
        }
        finally
        {
            _applyingRotation = false;
        }
    }

    private List<string> CheckedRotationCountries() =>
        RotationCountries.Children.OfType<CheckBox>()
            .Where(box => box.IsChecked == true && box.Tag is string)
            .Select(box => (string)box.Tag)
            .ToList();

    private async void RotationInterval_Checked(object sender, RoutedEventArgs e)
    {
        if (_applyingRotation || !IsLoaded || sender is not RadioButton { Tag: int seconds } || _vpn.Rotation is not { } preference)
        {
            return;
        }
        if (seconds == preference.IntervalSeconds)
        {
            return;
        }
        await SaveRotationAsync(seconds, _rotationDraft ?? ColituRotation.SelectedCountries(preference));
    }

    private async void RotationCountry_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingRotation || !IsLoaded || _vpn.Rotation is not { } preference)
        {
            return;
        }
        await SaveRotationAsync(preference.IntervalSeconds, CheckedRotationCountries());
    }

    /// <summary>
    /// Saves the interval and countries. Fewer than two countries is refused here the way the panel
    /// would (INVALID_PREFERENCE) and stays in the checklist until it is fixed.
    /// </summary>
    private async Task SaveRotationAsync(int seconds, List<string> selected)
    {
        if (_savingRotation || _vpn.Rotation is not { } preference)
        {
            return;
        }
        if (ColituRotation.Validate(seconds, selected, preference.AvailableCountries) != ColituRotation.Validation.Ok)
        {
            _rotationDraft = selected;
            if (seconds != preference.IntervalSeconds)
            {
                // The interval radio moved but nothing was saved: show what is stored.
                ApplyRotationUi();
            }
            ShowRotationNote(Loc.I["rotation.err.few"]);
            return;
        }

        _savingRotation = true;
        RotationNote.Visibility = Visibility.Collapsed;
        try
        {
            // Off keeps the countries already stored; the default set is sent as an empty list.
            var countries = seconds == 0 ? preference.Countries : ColituRotation.PayloadCountries(selected, preference.AvailableCountries);
            var saved = await _vpn.SaveRotationAsync(seconds, countries);
            _rotationDraft = null;
            ResetRotationStatus();
            ShowToast(Loc.I["settings.saved"]);
            ApplyRotationUi();

            // A connection already running on Hysteria2 (or another non-VLESS transport) cannot rotate:
            // reconnect so the panel's VLESS settings are used.
            if (saved.Active && _vpn.Status == ColituVpnStatus.Connected && !_vpn.OnMultihopRoute
                && !ColituApiClient.IsVless(_vpn.ConnectedProtocol))
            {
                await _vpn.ReconnectAsync();
            }
        }
        catch (ColituApiException ex) when (ex.ErrorCode is "INVALID_PREFERENCE")
        {
            _rotationDraft = selected;
            ShowRotationNote(Loc.I["rotation.err.few"]);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.SaveRotationAsync", ex);
            ShowToast(ex is ColituApiException api ? api.Message : Loc.I["err.generic"], true);
            ApplyRotationUi();
        }
        finally
        {
            _savingRotation = false;
            ApplyStatus();
        }
    }

    private void ShowRotationNote(string message)
    {
        RotationNote.Text = message;
        RotationNote.Visibility = Visibility.Visible;
    }

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
    public bool IsPaused { get; init; }
    public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PausedVisibility => IsPaused ? Visibility.Visible : Visibility.Collapsed;
}
