using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using v2rayN.Services;

namespace v2rayN.Views;

/// <summary>
/// 2.6.0 states: the two-factor code step, the paused-device screen, the one-time TUN question,
/// the trial-ending banner, the proxy-coverage and split-tunnel chips and the split-tunnel settings.
/// </summary>
public partial class ColituMainWindow
{
    // ── Two-factor code step ───────────────────────────────────────────────
    private string? _mfaToken;
    private string _mfaEmail = "";
    private bool _mfaRecovery;
    private bool _mfaByEmail;
    private bool _mfaBusy;
    private string? _mfaLastAutoCode;
    private DateTime _mfaExpiresAt;

    private void ShowMfa(ColituAuthResult challenge)
    {
        _mfaToken = challenge.MfaToken;
        _mfaEmail = challenge.Message ?? EmailBox.Text.Trim();
        _mfaExpiresAt = DateTime.UtcNow.AddSeconds(Math.Max(30, challenge.MfaExpiresIn));
        _mfaRecovery = false;
        _mfaByEmail = challenge.MfaByEmail;
        _mfaLastAutoCode = null;
        ShowView(MfaView);
        HideMfaError();
        ApplyMfaTexts();
        Dispatcher.BeginInvoke(() => MfaCodeBox.Focus(), DispatcherPriority.Input);
    }

    private void ApplyMfaTexts()
    {
        if (MfaSubtitle == null)
        {
            return;
        }
        if (_mfaByEmail)
        {
            // Unfamiliar-country sign-in: the code came by e-mail, there are no recovery codes to offer.
            _mfaRecovery = false;
            MfaTitle.Text = Loc.I["mfa.loginEmailTitle"];
            MfaSubtitle.Text = Loc.I["mfa.loginEmailBody"];
            MfaModeButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            MfaTitle.Text = Loc.I["mfa.title"];
            MfaSubtitle.Text = _mfaRecovery ? Loc.I["mfa.subRecovery"] : Loc.I.Format("mfa.sub", ("email", _mfaEmail));
            MfaModeButton.Visibility = Visibility.Visible;
        }
        MfaCodeLabel.Text = Loc.I[_mfaRecovery ? "mfa.recoveryCode" : _mfaByEmail ? "mfa.codeEmail" : "mfa.code"];
        MfaModeText.Text = Loc.I[_mfaRecovery ? "mfa.useApp" : "mfa.useRecovery"];
        // Six digits in a large box; recovery codes are longer and carry letters and dashes.
        MfaCodeBox.MaxLength = _mfaRecovery ? ColituMfa.MaxRecoveryCodeLength : ColituMfa.CodeLength;
        MfaCodeBox.FontSize = _mfaRecovery ? 18 : 30;
    }

    private void MfaMode_Click(object sender, RoutedEventArgs e)
    {
        _mfaRecovery = !_mfaRecovery;
        MfaCodeBox.Clear();
        HideMfaError();
        ApplyMfaTexts();
        MfaCodeBox.Focus();
    }

    private void MfaBack_Click(object sender, RoutedEventArgs e)
    {
        _mfaToken = null;
        MfaCodeBox.Clear();
        AuthLoginTab.IsChecked = true;
        ShowAuth();
    }

    /// <summary>Pasted codes keep their digits ("123 456", "123-456"); recovery codes are kept as typed.</summary>
    private void MfaCode_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (_mfaRecovery)
        {
            return;
        }
        e.CancelCommand();
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text)
        {
            return;
        }
        var digits = ColituMfa.DigitsOfPaste(text);
        if (digits.Length > 0)
        {
            MfaCodeBox.Text = digits;
            MfaCodeBox.CaretIndex = digits.Length;
        }
    }

    private void MfaCode_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = _mfaRecovery
            ? !e.Text.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or ' ')
            : !e.Text.All(char.IsAsciiDigit);
    }

    private async void MfaCode_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_mfaRecovery || _mfaBusy || MfaView.Visibility != Visibility.Visible)
        {
            return;
        }
        var code = ColituMfa.NormalizeCode(MfaCodeBox.Text, recoveryCode: false);
        if (code != null && code != _mfaLastAutoCode)
        {
            _mfaLastAutoCode = code;
            await SubmitMfaAsync();
        }
    }

    private async void MfaCode_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SubmitMfaAsync();
        }
    }

    private async void MfaSubmit_Click(object sender, RoutedEventArgs e) => await SubmitMfaAsync();

    private async Task SubmitMfaAsync()
    {
        if (_mfaBusy || _mfaToken == null)
        {
            return;
        }
        if (DateTime.UtcNow > _mfaExpiresAt)
        {
            ExpireMfa();
            return;
        }
        if (ColituMfa.NormalizeCode(MfaCodeBox.Text, _mfaRecovery) == null)
        {
            ShowMfaError(Loc.I[_mfaRecovery ? "mfa.err.recoveryFormat" : "mfa.err.format"]);
            return;
        }

        SetMfaBusy(true);
        HideMfaError();
        try
        {
            var result = await _auth.LoginMfaAsync(_mfaToken, _mfaEmail, MfaCodeBox.Text, _mfaRecovery);
            if (!result.Success)
            {
                switch (result.ErrorCode)
                {
                    case "MFA_TOKEN_EXPIRED":
                        ExpireMfa();
                        return;
                    default:
                        // Wrong code, rate limit, update required: stay on the code step.
                        ShowMfaError(result.Error ?? Loc.I["err.generic"]);
                        MfaCodeBox.SelectAll();
                        _ = Dispatcher.BeginInvoke(() => MfaCodeBox.Focus(), DispatcherPriority.Input);
                        return;
                }
            }

            _mfaToken = null;
            MfaCodeBox.Clear();
            if (result.RequiresEmailVerification)
            {
                ShowVerify(codeJustSent: true);
                return;
            }
            await EnterAppAsync(offline: false);
            if (result.DevicePaused != null)
            {
                await ShowPausedAsync(result.DevicePaused);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.SubmitMfaAsync", ex);
            ShowMfaError(Loc.I["err.network"]);
        }
        finally
        {
            SetMfaBusy(false);
        }
    }

    /// <summary>The mfa_token ran out: back to the password, with the reason.</summary>
    private void ExpireMfa()
    {
        _mfaToken = null;
        MfaCodeBox.Clear();
        AuthLoginTab.IsChecked = true;
        ShowAuth();
        ShowAuthError(Loc.I["mfa.err.expired"]);
    }

    private void SetMfaBusy(bool busy)
    {
        _mfaBusy = busy;
        MfaSubmitButton.IsEnabled = !busy;
        MfaCodeBox.IsEnabled = !busy;
        MfaModeButton.IsEnabled = !busy;
        MfaSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowMfaError(string message)
    {
        MfaErrorText.Text = message;
        MfaErrorBox.Visibility = Visibility.Visible;
        FadeIn(MfaErrorBox, 6);
    }

    private void HideMfaError() => MfaErrorBox.Visibility = Visibility.Collapsed;

    // ── Paused device ──────────────────────────────────────────────────────
    private bool _pausedBusy;

    /// <summary>
    /// "This device is paused": no connection, no auto-connect, and the kill switch released
    /// (nothing could reconnect, so it must not keep the internet closed).
    /// </summary>
    private async Task ShowPausedAsync(ColituDeviceOverLimit? info)
    {
        info ??= _auth.DevicePaused ?? new ColituDeviceOverLimit();
        try
        {
            await _vpn.EnterDevicePausedAsync();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.ShowPausedAsync", ex);
        }
        PausedBody.Text = info.Describe(Loc.I);
        PausedErrorBox.Visibility = Visibility.Collapsed;
        var canActivate = !string.IsNullOrWhiteSpace(_auth.RegisteredDeviceId);
        PausedUseThisButton.Visibility = PausedUseThisHint.Visibility = canActivate ? Visibility.Visible : Visibility.Collapsed;
        if (!canActivate)
        {
            PausedErrorText.Text = Loc.I["paused.noDeviceId"];
            PausedErrorBox.Visibility = Visibility.Visible;
        }
        if (PausedPrompt.Visibility != Visibility.Visible)
        {
            PausedPrompt.Visibility = Visibility.Visible;
            FadeIn(PausedPrompt, 8);
        }
        ApplyStatus();
        ShowFromTray();
    }

    private void HidePaused()
    {
        if (PausedPrompt != null)
        {
            PausedPrompt.Visibility = Visibility.Collapsed;
        }
    }

    private async void PausedUseThis_Click(object sender, RoutedEventArgs e)
    {
        if (_pausedBusy)
        {
            return;
        }
        _pausedBusy = true;
        PausedUseThisButton.IsEnabled = false;
        PausedSpinner.Visibility = Visibility.Visible;
        PausedErrorBox.Visibility = Visibility.Collapsed;
        try
        {
            await _auth.ActivateThisDeviceAsync();
            HidePaused();
            ShowToast(Loc.I["paused.activated"]);
            // Fresh account, servers and plan; then carry on as after a normal start.
            await RefreshDataAsync(includeAccount: false);
            if (_vpn.Preferences.AutoConnectEnabled && _vpn.Status == ColituVpnStatus.Disconnected && !_planRequired)
            {
                await ToggleConnectionAsync();
            }
        }
        catch (ColituApiException ex) when (ex.ErrorCode is "DEVICE_OVER_LIMIT")
        {
            PausedBody.Text = (ex.OverLimit ?? new ColituDeviceOverLimit()).Describe(Loc.I);
        }
        catch (Exception ex)
        {
            PausedErrorText.Text = ex is ColituApiException api ? api.Message : Loc.I["err.network"];
            PausedErrorBox.Visibility = Visibility.Visible;
        }
        finally
        {
            PausedSpinner.Visibility = Visibility.Collapsed;
            PausedUseThisButton.IsEnabled = true;
            _pausedBusy = false;
        }
    }

    private void PausedPremium_Click(object sender, RoutedEventArgs e) => OpenUrl(ColituAuthService.PricingUrl);

    private void PausedSignOut_Click(object sender, RoutedEventArgs e)
    {
        HidePaused();
        SignOut_Click(sender, e);
    }

    // ── One-time TUN question ──────────────────────────────────────────────
    private void ShowTunPrompt()
    {
        if (TunPrompt.Visibility != Visibility.Visible)
        {
            TunPrompt.Visibility = Visibility.Visible;
            FadeIn(TunPrompt, 8);
        }
    }

    private async void TunPromptSwitch_Click(object sender, RoutedEventArgs e)
    {
        TunPrompt.Visibility = Visibility.Collapsed;
        await SavePreferencesAsync(_vpn.Preferences with { ConnectionMode = ColituConnectionModes.Tun, TunModePromptShown = true });
    }

    private async void TunPromptKeep_Click(object sender, RoutedEventArgs e)
    {
        TunPrompt.Visibility = Visibility.Collapsed;
        try
        {
            await _vpn.UpdatePreferencesAsync(_vpn.Preferences with { TunModePromptShown = true });
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.TunPromptKeep_Click", ex);
        }
        ApplyStatus();
    }

    // ── Trial banner ───────────────────────────────────────────────────────
    private void ApplyTrialBanner()
    {
        if (TrialBanner == null)
        {
            return;
        }
        var text = ColituTrial.BannerText(_auth.CurrentSubscription, DateTimeOffset.UtcNow, _vpn.Preferences.TrialBannerDismissedOn, Loc.I);
        TrialBanner.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        TrialBannerText.Text = text ?? "";
    }

    private async void TrialDismiss_Click(object sender, RoutedEventArgs e)
    {
        TrialBanner.Visibility = Visibility.Collapsed;
        try
        {
            await _vpn.UpdatePreferencesAsync(_vpn.Preferences with
            {
                TrialBannerDismissedOn = DateTimeOffset.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            });
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.TrialDismiss_Click", ex);
        }
    }

    private void TrialKeep_Click(object sender, RoutedEventArgs e) => OpenUrl(ColituAuthService.PricingUrl);

    // ── Proxy coverage and split-tunnel chips ──────────────────────────────
    private void ApplyProxyCoverageChip()
    {
        if (ProxyCoverageChip == null)
        {
            return;
        }
        var proxy = _vpn.ProxyCoverageLimited;
        ProxyCoverageChip.Visibility = proxy && _vpn.AdvancedMode ? Visibility.Visible : Visibility.Collapsed;
        ProxyCoverageChipText.Text = Loc.I["proxy.chip"].ToUpper(Loc.I.Culture);
        ProxyCoverageChip.ToolTip = Loc.I["proxy.coverage"];
    }

    private void ProxyCoverageChip_Click(object sender, MouseButtonEventArgs e)
    {
        ShowToast(Loc.I["proxy.coverage"], true);
        Navigate("settings");
    }

    private void ApplySplitChip()
    {
        if (SplitChip == null)
        {
            return;
        }
        var preferences = _vpn.Preferences;
        var tun = preferences.IsTunMode;
        var active = ColituSplitTunnel.IsActive(preferences, tun);
        SplitChip.Visibility = active && _vpn.AdvancedMode ? Visibility.Visible : Visibility.Collapsed;
        if (active)
        {
            var count = Loc.I.Count("entry", ColituSplitTunnel.EntryCount(preferences, tun));
            SplitChipText.Text = Loc.I.Format(preferences.SplitTunnelMode == ColituSplitTunnelModes.Only ? "split.chip.only" : "split.chip.bypass", ("n", count))
                .ToUpper(Loc.I.Culture);
        }
    }

    private void SplitChip_Click(object sender, MouseButtonEventArgs e)
    {
        Navigate("settings");
        SplitCard.BringIntoView();
    }

    // ── Split-tunnel settings ──────────────────────────────────────────────
    private static bool SplitSettingsChanged(ColituVpnPreferences next, ColituVpnPreferences current) =>
        next.SplitTunnelMode != current.SplitTunnelMode
        || !(next.SplitTunnelApps ?? []).SequenceEqual(current.SplitTunnelApps ?? [], StringComparer.OrdinalIgnoreCase)
        || !(next.SplitTunnelDomains ?? []).SequenceEqual(current.SplitTunnelDomains ?? [], StringComparer.OrdinalIgnoreCase)
        || !(next.SplitTunnelNetworks ?? []).SequenceEqual(current.SplitTunnelNetworks ?? [], StringComparer.OrdinalIgnoreCase);

    private void ApplySplitUi()
    {
        if (SplitModeOff == null)
        {
            return;
        }
        var preferences = _vpn.Preferences;
        var applying = _applyingPreferences;
        _applyingPreferences = true;
        try
        {
            SplitModeOff.IsChecked = preferences.SplitTunnelMode == ColituSplitTunnelModes.Off;
            SplitModeBypass.IsChecked = preferences.SplitTunnelMode == ColituSplitTunnelModes.Bypass;
            SplitModeOnly.IsChecked = preferences.SplitTunnelMode == ColituSplitTunnelModes.Only;
        }
        finally
        {
            _applyingPreferences = applying;
        }
        SplitModeHint.Text = preferences.SplitTunnelMode switch
        {
            ColituSplitTunnelModes.Bypass => Loc.I["split.mode.bypassHint"],
            ColituSplitTunnelModes.Only => Loc.I["split.mode.onlyHint"],
            _ => ""
        };
        SplitModeHint.Visibility = SplitModeHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SplitLists.Visibility = preferences.SplitTunnelMode == ColituSplitTunnelModes.Off ? Visibility.Collapsed : Visibility.Visible;
        SplitProxyNote.Visibility = preferences.IsTunMode ? Visibility.Collapsed : Visibility.Visible;
        SplitAppsList.ItemsSource = SplitRows(preferences.SplitTunnelApps, "app", path => System.IO.Path.GetFileName(path), path => path);
        SplitDomainsList.ItemsSource = SplitRows(preferences.SplitTunnelDomains, "domain", value => value, _ => null);
        SplitNetworksList.ItemsSource = SplitRows(preferences.SplitTunnelNetworks, "network", value => value, _ => null);
        ApplySplitChip();
    }

    private List<FrameworkElement> SplitRows(List<string>? values, string kind, Func<string, string> title, Func<string, string?> detail)
    {
        var rows = new List<FrameworkElement>();
        if (values is not { Count: > 0 })
        {
            rows.Add(new TextBlock { Text = Loc.I["split.empty"], Style = (Style)FindResource("Small"), Margin = new Thickness(2, 0, 0, 4) });
            return rows;
        }
        foreach (var value in values)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = title(value), FontSize = 13.5, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
            if (detail(value) is { } extra)
            {
                text.Children.Add(new TextBlock { Text = extra, FontSize = 11.5, Foreground = (Brush)FindResource("MutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = extra });
            }
            var remove = new Button
            {
                Style = (Style)FindResource("IconButton"),
                ToolTip = Loc.I["split.remove"],
                Tag = (kind, value),
                Content = new System.Windows.Shapes.Path { Width = 11, Height = 11, Data = (Geometry)FindResource("IconX"), Style = (Style)FindResource("Stroke") }
            };
            remove.Click += SplitRemove_Click;
            Grid.SetColumn(remove, 1);
            grid.Children.Add(text);
            grid.Children.Add(remove);
            rows.Add(new Border { Padding = new Thickness(12, 8, 6, 8), Style = (Style)FindResource("Tile"), Child = grid, Margin = new Thickness(0, 0, 0, 6) });
        }
        return rows;
    }

    private async void SplitMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_applyingPreferences || !IsLoaded || sender is not RadioButton { Tag: string mode })
        {
            return;
        }
        var normalized = ColituSplitTunnelModes.Normalize(mode);
        if (normalized == _vpn.Preferences.SplitTunnelMode)
        {
            return;
        }
        await SaveSplitAsync(_vpn.Preferences with { SplitTunnelMode = normalized });
    }

    private async void SplitAddApp_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Programs (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        var app = ColituSplitTunnel.NormalizeApp(dialog.FileName);
        if (app == null)
        {
            ShowSplitError(Loc.I["split.err.app"]);
            return;
        }
        var apps = _vpn.Preferences.SplitTunnelApps ?? [];
        if (apps.Count >= ColituSplitTunnel.MaxApps)
        {
            ShowSplitError(Loc.I["split.err.limit"]);
            return;
        }
        await SaveSplitAsync(_vpn.Preferences with { SplitTunnelApps = [.. apps, app] });
    }

    private async void SplitDomainBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await AddSplitDomainAsync();
        }
    }

    private async void SplitAddDomain_Click(object sender, RoutedEventArgs e) => await AddSplitDomainAsync();

    private async Task AddSplitDomainAsync()
    {
        var domain = ColituSplitTunnel.NormalizeDomain(SplitDomainBox.Text);
        if (domain == null)
        {
            ShowSplitError(Loc.I["split.err.domain"]);
            return;
        }
        var domains = _vpn.Preferences.SplitTunnelDomains ?? [];
        if (domains.Count >= ColituSplitTunnel.MaxDomains)
        {
            ShowSplitError(Loc.I["split.err.limit"]);
            return;
        }
        SplitDomainBox.Clear();
        await SaveSplitAsync(_vpn.Preferences with { SplitTunnelDomains = [.. domains, domain] });
    }

    private async void SplitNetworkBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await AddSplitNetworkAsync();
        }
    }

    private async void SplitAddNetwork_Click(object sender, RoutedEventArgs e) => await AddSplitNetworkAsync();

    private async Task AddSplitNetworkAsync()
    {
        var network = ColituSplitTunnel.NormalizeNetwork(SplitNetworkBox.Text);
        if (network == null)
        {
            ShowSplitError(Loc.I["split.err.ip"]);
            return;
        }
        var networks = _vpn.Preferences.SplitTunnelNetworks ?? [];
        if (networks.Count >= ColituSplitTunnel.MaxNetworks)
        {
            ShowSplitError(Loc.I["split.err.limit"]);
            return;
        }
        SplitNetworkBox.Clear();
        await SaveSplitAsync(_vpn.Preferences with { SplitTunnelNetworks = [.. networks, network] });
    }

    private async void SplitRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: (string kind, string value) })
        {
            return;
        }
        var preferences = _vpn.Preferences;
        preferences = kind switch
        {
            "app" => preferences with { SplitTunnelApps = (preferences.SplitTunnelApps ?? []).Where(item => !string.Equals(item, value, StringComparison.OrdinalIgnoreCase)).ToList() },
            "domain" => preferences with { SplitTunnelDomains = (preferences.SplitTunnelDomains ?? []).Where(item => item != value).ToList() },
            _ => preferences with { SplitTunnelNetworks = (preferences.SplitTunnelNetworks ?? []).Where(item => item != value).ToList() }
        };
        await SaveSplitAsync(preferences);
    }

    /// <summary>Saves the lists; a change that alters routing reconnects a running tunnel (like privacy mode).</summary>
    private async Task SaveSplitAsync(ColituVpnPreferences preferences)
    {
        SplitError.Visibility = Visibility.Collapsed;
        await SavePreferencesAsync(preferences);
        ApplySplitUi();
        ApplyStatus();
    }

    private void ShowSplitError(string message)
    {
        SplitError.Text = message;
        SplitError.Visibility = Visibility.Visible;
    }
}
