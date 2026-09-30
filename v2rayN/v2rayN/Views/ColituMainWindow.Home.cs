using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using v2rayN.Services;

namespace v2rayN.Views;

public partial class ColituMainWindow
{
    private List<ColituVpnServer> _servers = [];
    private ColituStatsResponse? _usage;
    private bool _planRequired;
    private ColituVpnStatus? _shownStatus;

    // ── Orb ────────────────────────────────────────────────────────────────
    private static readonly Color Violet = Color.FromRgb(0x9F, 0x8C, 0xFF);
    private static readonly Color Blue2 = Color.FromRgb(0x94, 0x83, 0xFF);
    private static readonly Color Lilac = Color.FromRgb(0xC4, 0xB5, 0xFD);
    private static readonly Color Pink = Color.FromRgb(0xE2, 0xD6, 0xFF);

    /// <summary>
    /// Drives the particle ring and the button glow like the phone power button:
    /// the ring idles when off, churns while connecting and glows when on.
    /// </summary>
    private void AnimateOrb(ColituVpnStatus status)
    {
        var busy = status is ColituVpnStatus.Connecting or ColituVpnStatus.Reconnecting;
        var on = status == ColituVpnStatus.Connected;

        OrbParticles.Energy = on ? 0.62 : busy ? 1.0 : 0.22;
        OrbParticles.Color = on ? Violet : Blue2;
        OrbParticles.Color2 = on ? Pink : Lilac;

        ConnectButton.ApplyTemplate();
        if (ConnectButton.Template.FindName("Glow", ConnectButton) is Ellipse glow)
        {
            if (on || busy)
            {
                glow.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 0.55 : 0.35, 0.8, TimeSpan.FromMilliseconds(on ? 1600 : 800))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                });
            }
            else
            {
                glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.28, TimeSpan.FromMilliseconds(600)) { EasingFunction = Ease });
            }
        }

        if (_shownStatus != status && on)
        {
            // A small spring when the tunnel comes up.
            var pop = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(700) };
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.06, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220)), new SineEase()));
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700)), new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut }));
            ConnectScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            ConnectScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }

        var dotColor = on ? Color.FromRgb(0x5E, 0xE0, 0xA0) : busy ? Color.FromRgb(0xF5, 0xC3, 0x6B) : Color.FromRgb(0xFF, 0x6B, 0x7A);
        StatusDot.Fill = new SolidColorBrush(dotColor);
        StatusDotHalo.Fill = new SolidColorBrush(dotColor);
        if (on || busy)
        {
            var pulse = TimeSpan.FromSeconds(1.8);
            StatusDotHalo.BeginAnimation(OpacityProperty, new DoubleAnimation(0.6, 0, pulse) { RepeatBehavior = RepeatBehavior.Forever });
            StatusDotHaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.8, 2.4, pulse) { RepeatBehavior = RepeatBehavior.Forever });
            StatusDotHaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.8, 2.4, pulse) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            StatusDotHalo.BeginAnimation(OpacityProperty, null);
            StatusDotHalo.Opacity = 0;
        }
    }

    // ── Status ─────────────────────────────────────────────────────────────
    private void ApplyStatus()
    {
        if (HomeTitle == null)
        {
            return;
        }

        var status = _vpn.Status;
        var on = status == ColituVpnStatus.Connected;
        var busy = status is ColituVpnStatus.Connecting or ColituVpnStatus.Reconnecting;
        var blocked = !on && _vpn.KillSwitchEngaged;
        var loc = Loc.I;

        HomeTitle.Text = on ? loc["home.title.on"]
            : blocked ? loc["home.title.blocked"]
            : busy ? loc["home.title.connecting"]
            : _planRequired ? loc["home.title.noplan"]
            : loc["home.title.off"];
        HomeSubtitle.Text = on ? loc.Format("home.sub.on", ("server", ServerLabel(_vpn.ConnectedServer) ?? loc["server.auto"]))
            : blocked ? loc["home.sub.blocked"]
            : busy ? loc["home.sub.connecting"]
            : _planRequired ? loc["home.sub.noplan"]
            : status == ColituVpnStatus.Error && _vpn.LastError is { Length: > 0 } error ? error
            : loc["home.sub.off"];
        ConnectLabel.Text = on ? loc["home.tapOff"]
            : busy ? loc["home.tapCancel"]
            : _planRequired ? loc["plan.choose"]
            : loc["home.tap"];
        ConnectButton.ToolTip = on ? loc["home.disconnect"] : busy ? loc["home.cancel"] : loc["home.connect"];
        var protocol = on ? _vpn.ConnectedProtocol : null;
        ProtocolChip.Visibility = protocol is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        ProtocolChipText.Text = protocol is { Length: > 0 } ? $"{loc["home.protocol"]} · {protocol}".ToUpper(loc.Culture) : "";

        StatusChipText.Text = on ? loc["status.protected"]
            : blocked ? loc["status.blocked"]
            : status == ColituVpnStatus.Reconnecting ? loc["status.reconnecting"]
            : busy ? loc["status.connecting"]
            : loc["status.unprotected"];
        TrayIcon.ToolTipText = $"Colitu VPN · {StatusChipText.Text}";
        TrayConnectItem.Header = on || busy || blocked ? loc["tray.disconnect"] : loc["tray.connect"];
        UnblockButton.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;

        if (_shownStatus != status)
        {
            AnimateOrb(status);
            _shownStatus = status;
        }
        ApplyLocationCard();
        UpdateSessionTimer();
    }

    private void UpdateSessionTimer()
    {
        if (SessionTimer == null)
        {
            return;
        }
        var duration = _vpn.GetConnectionDuration();
        SessionTimer.Text = $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
        // The button shows the session clock while connected and the power glyph otherwise.
        var on = _vpn.Status == ColituVpnStatus.Connected;
        SessionTimer.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        PowerIcon.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e) => await ToggleConnectionAsync();

    private async void Unblock_Click(object sender, RoutedEventArgs e)
    {
        await _vpn.DisconnectAsync();
        ApplyStatus();
        ShowToast(Loc.I["info.disconnected"]);
    }

    private async Task ToggleConnectionAsync()
    {
        var status = _vpn.Status;
        if (status is ColituVpnStatus.Connected or ColituVpnStatus.Connecting or ColituVpnStatus.Reconnecting)
        {
            try
            {
                await _vpn.DisconnectAsync();
                ShowToast(Loc.I["info.disconnected"]);
            }
            catch (Exception ex)
            {
                Logging.SaveLog("ColituMainWindow.Disconnect", ex);
            }
            ApplyStatus();
            return;
        }

        if (_planRequired && !_offline)
        {
            Navigate("plan");
            return;
        }

        try
        {
            await _vpn.ConnectSavedAsync();
            _ = RefreshUsageSoonAsync();
        }
        catch (ColituPlanRequiredException)
        {
            _planRequired = true;
            ApplyAccount();
            ShowToast(Loc.I["err.noPlan"], true);
            Navigate("plan");
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, true);
        }
        finally
        {
            ApplyStatus();
        }
    }

    private async Task RefreshUsageSoonAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        _usage = await _vpn.GetStatsAsync().ContinueWith(task => task.IsCompletedSuccessfully ? task.Result : _usage);
        ApplyAccount();
    }

    // ── Location card ──────────────────────────────────────────────────────
    private void ApplyLocationCard()
    {
        var server = _vpn.Status is ColituVpnStatus.Connected && _vpn.ConnectedServer != null
            ? _vpn.ConnectedServer
            : _vpn.IsAutoSelection ? null : _vpn.SelectedServer ?? _servers.FirstOrDefault(s => s.Id == _vpn.SavedServerId);
        var row = server == null ? ColituServerRow.Auto() : ColituServerRow.From(server);
        if (_vpn.IsAutoSelection && server != null)
        {
            row.Subtitle = Loc.I["server.auto"] + " · " + row.Subtitle;
        }
        HomeFlag.Content = row;
        HomeServerTitle.Text = row.Title;
        HomeServerSubtitle.Text = row.Subtitle;
        var connected = _vpn.Status == ColituVpnStatus.Connected;
        HomeServerCheck.Background = connected ? (Brush)FindResource("SuccessBrush") : Brushes.Transparent;
        HomeServerCheck.BorderBrush = (Brush)FindResource(connected ? "SuccessBrush" : "DimBrush");
        HomeServerCheckMark.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string? ServerLabel(ColituVpnServer? server)
    {
        if (server == null)
        {
            return null;
        }
        return ColituServerRow.From(server).Title;
    }

    private void LocationCard_Click(object sender, MouseButtonEventArgs e) => Navigate("locations");

    private void ChangeServer_Click(object sender, RoutedEventArgs e) => Navigate("locations");

    // ── Plan card and account summary ──────────────────────────────────────
    private void ApplyAccount()
    {
        if (PlanName == null)
        {
            return;
        }

        var subscription = _auth.CurrentSubscription;
        var loc = Loc.I;
        var active = subscription?.Active == true;
        var status = subscription?.Status ?? "inactive";

        PlanName.Text = active ? PlanTitle(subscription!) : loc["plan.none"];
        PlanDetail.Text = active ? PlanDetailText(subscription!) : loc["plan.noneHint"];
        SetBadge(PlanBadge, PlanBadgeText, status);

        var limit = subscription?.TrafficLimitBytes;
        var used = _usage?.Stats.TotalUsedBytes ?? subscription?.TrafficUsedBytes ?? 0;
        TrafficBlock.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (limit is > 0)
        {
            TrafficText.Text = loc.Format("home.trafficOf", ("used", FormatBytes(used)), ("limit", FormatBytes(limit.Value)));
            TrafficTrack.Visibility = Visibility.Visible;
            var ratio = Math.Clamp((double)used / limit.Value, 0, 1);
            Dispatcher.BeginInvoke(() =>
            {
                TrafficFill.BeginAnimation(WidthProperty, new DoubleAnimation(Math.Max(6, TrafficTrack.ActualWidth * ratio), TimeSpan.FromMilliseconds(900)) { EasingFunction = Ease });
            }, DispatcherPriority.Loaded);
        }
        else
        {
            TrafficText.Text = used > 0 ? FormatBytes(used) : loc["home.unlimited"];
            TrafficTrack.Visibility = Visibility.Collapsed;
        }

        DevicesText.Text = subscription == null ? "—" : $"{Math.Max(1, subscription.DevicesUsed)} / {Math.Max(1, subscription.DeviceLimit)}";

        var expiresSoon = active && ExpiresAt(subscription!) is { } end && end - DateTimeOffset.UtcNow < TimeSpan.FromDays(3);
        PlanCtaButton.Visibility = !active || expiresSoon ? Visibility.Visible : Visibility.Collapsed;
        PlanCtaText.Text = active ? loc["plan.extend"] : loc["plan.choose"];

        // Account page
        var email = _auth.CurrentUser?.Email ?? "";
        AccountEmail.Text = email;
        AvatarText.Text = email.Length >= 2 ? email[..2].ToUpperInvariant() : "C";
        AccountId.Text = _auth.CurrentUser?.Id is { Length: > 0 } id ? $"ID · {id[..Math.Min(8, id.Length)]}" : "";
        AccountPlan.Text = active ? PlanTitle(subscription!) : loc["plan.none"];
        SetBadge(AccountPlanBadge, AccountPlanBadgeText, status);
        AccountUntil.Text = ExpiresAt(subscription) is { } until ? FormatDate(until) : "—";

        // Plan page strip
        CurrentPlanText.Text = active ? $"{PlanTitle(subscription!)} · {PlanDetailText(subscription!)}" : loc["plan.none"];
        SetBadge(CurrentPlanBadge, CurrentPlanBadgeText, status);
        CreditText.Text = loc.Format("brand.credit", ("brand", "Avenlith"));
    }

    private string PlanTitle(ColituSubscription subscription)
    {
        if (string.Equals(subscription.Status, "trialing", StringComparison.OrdinalIgnoreCase))
        {
            return Loc.I["plan.trialName"];
        }
        return string.IsNullOrWhiteSpace(subscription.PlanName) ? "Colitu VPN" : subscription.PlanName!;
    }

    private string PlanDetailText(ColituSubscription subscription)
    {
        if (ExpiresAt(subscription) is not { } end)
        {
            return "";
        }
        var left = end - DateTimeOffset.UtcNow;
        var leftText = left.TotalDays >= 1
            ? Loc.I.Count("day", (long)Math.Floor(left.TotalDays))
            : Loc.I.Count("hour", Math.Max(1, (long)Math.Ceiling(left.TotalHours)));
        return $"{Loc.I.Format("plan.until", ("date", FormatDate(end)))} · {Loc.I.Format("plan.left", ("left", leftText))}";
    }

    private static DateTimeOffset? ExpiresAt(ColituSubscription? subscription)
    {
        return DateTimeOffset.TryParse(subscription?.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;
    }

    private void SetBadge(Border badge, TextBlock text, string status)
    {
        var key = status switch
        {
            "active" => "plan.status.active",
            "trialing" => "plan.status.trialing",
            "expired" => "plan.status.expired",
            _ => "plan.status.inactive"
        };
        text.Text = Loc.I[key];
        var active = status is "active" or "trialing";
        // Lavender badge with dark text when active, rose glass otherwise (ColituBadge).
        badge.Background = active ? (Brush)FindResource("BadgeBrush") : new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0x6B, 0x7A));
        badge.BorderBrush = Brushes.Transparent;
        text.Foreground = (Brush)FindResource(active ? "OnAccentBrush" : "DangerBrush");
    }

    private void PlanCta_Click(object sender, RoutedEventArgs e) => Navigate("plan");

    private static string FormatDate(DateTimeOffset value) => value.ToLocalTime().ToString("d MMMM yyyy", Loc.I.Culture);

    private static string FormatBytes(long bytes)
    {
        string[] units = Loc.I.Language == "ru" ? ["Б", "КБ", "МБ", "ГБ", "ТБ"] : ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value.ToString(unit == 0 ? "0" : "0.#", Loc.I.Culture)} {units[unit]}";
    }

    // ── Preferences (home quick settings and the settings page) ───────────
    private bool _applyingPreferences;

    private void ApplyPreferencesToUi()
    {
        _applyingPreferences = true;
        try
        {
            var preferences = _vpn.Preferences;
            var tun = preferences.IsTunMode;
            HomeModeTun.IsChecked = SettingsModeTun.IsChecked = tun;
            HomeModeProxy.IsChecked = SettingsModeProxy.IsChecked = !tun;
            HomeKillSwitch.IsChecked = SettingsKillSwitch.IsChecked = preferences.KillSwitchEnabled;
            HomeAutoConnect.IsChecked = SettingsAutoConnect.IsChecked = preferences.AutoConnectEnabled;
            SettingsDns.IsChecked = preferences.DnsLeakProtectionEnabled;
            SettingsTray.IsChecked = preferences.CloseToTray;
            SettingsStartup.IsChecked = _vpn.LaunchAtStartup;
            ApplyModeHint();
        }
        finally
        {
            _applyingPreferences = false;
        }
    }

    private void ApplyModeHint()
    {
        if (ModeHint != null)
        {
            ModeHint.Text = Loc.I[_vpn.Preferences.IsTunMode ? "settings.mode.tunHint" : "settings.mode.proxyHint"];
        }
    }

    private async void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_applyingPreferences || !IsLoaded || sender is not RadioButton { Tag: string mode })
        {
            return;
        }
        var normalized = mode == ColituConnectionModes.Tun ? ColituConnectionModes.Tun : ColituConnectionModes.Proxy;
        if (_vpn.Preferences.ConnectionMode == normalized)
        {
            return;
        }
        await SavePreferencesAsync(_vpn.Preferences with { ConnectionMode = normalized });
    }

    private async void Preference_Click(object sender, RoutedEventArgs e)
    {
        if (_applyingPreferences || !IsLoaded || sender is not CheckBox box)
        {
            return;
        }
        var on = box.IsChecked == true;
        var preferences = _vpn.Preferences;
        preferences = box == HomeKillSwitch || box == SettingsKillSwitch ? preferences with { KillSwitchEnabled = on }
            : box == HomeAutoConnect || box == SettingsAutoConnect ? preferences with { AutoConnectEnabled = on }
            : box == SettingsDns ? preferences with { DnsLeakProtectionEnabled = on }
            : box == SettingsTray ? preferences with { CloseToTray = on }
            : preferences;
        await SavePreferencesAsync(preferences);
    }

    private async Task SavePreferencesAsync(ColituVpnPreferences preferences)
    {
        var tunnelSettingsChanged = preferences.ConnectionMode != _vpn.Preferences.ConnectionMode
            || preferences.DnsLeakProtectionEnabled != _vpn.Preferences.DnsLeakProtectionEnabled;
        try
        {
            await _vpn.UpdatePreferencesAsync(preferences);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.SavePreferencesAsync", ex);
        }
        ApplyPreferencesToUi();

        if (tunnelSettingsChanged && _vpn.Status == ColituVpnStatus.Connected)
        {
            // Mode, kill switch and DNS protection are part of the core config: reconnect to apply.
            try
            {
                await _vpn.ReconnectAsync();
            }
            catch (Exception ex)
            {
                ShowToast(ex.Message, true);
            }
            ApplyStatus();
        }
        else if (!tunnelSettingsChanged)
        {
            ShowToast(Loc.I["settings.saved"]);
        }
    }
}
