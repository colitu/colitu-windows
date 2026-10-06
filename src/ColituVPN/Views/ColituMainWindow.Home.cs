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
            : blocked ? loc[_vpn.KillSwitchHeldAfterCrash ? "home.sub.blockedCrash" : "home.sub.blocked"]
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
        ProtocolChipText.Text = protocol is { Length: > 0 } ? $"{loc["home.protocol"]} · {ColituTransportNames.Of(protocol, loc)}".ToUpper(loc.Culture) : "";

        StatusChipText.Text = on ? loc["status.protected"]
            : blocked ? loc["status.blocked"]
            : status == ColituVpnStatus.Reconnecting ? loc["status.reconnecting"]
            : busy ? loc["status.connecting"]
            : loc["status.unprotected"];
        TrayIcon.ToolTipText = $"Colitu VPN · {StatusChipText.Text}";
        TrayConnectItem.Header = on || busy || blocked ? loc["tray.disconnect"] : loc["tray.connect"];
        UnblockButton.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
        ApplyRuDirectIndicator();
        ApplySplitChip();
        ApplyProxyCoverageChip();

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
        // The clock ticks every second: the rotation countdown rides on it.
        ApplyRouteChip();
    }

    // ── Multihop route and rotating exit IP ────────────────────────────────
    private ColituRotationStatus? _rotationStatus;
    private string? _rotationNode;
    private DateTimeOffset _rotationPollAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRotationPoll = DateTimeOffset.MinValue;
    private bool _rotationPolling;
    private int _rotationEpoch;

    /// <summary>
    /// A chip under the status: "Entry FI → Exit DE" on a multihop route; with a rotating IP the
    /// current exit and the time to the next change.
    /// </summary>
    private void ApplyRouteChip()
    {
        if (RouteChip == null)
        {
            return;
        }
        string? text = null;
        var connected = _vpn.Status == ColituVpnStatus.Connected;
        var server = connected ? _vpn.ConnectedServer : null;
        if (server is { IsMultihop: true })
        {
            ForgetRotationStatus();
            text = Loc.I.Format("multihop.home", ("entry", RouteEndText(server.Entry)), ("exit", RouteEndText(server.Exit)));
        }
        else if (connected && _vpn.RotationActive && server?.Id is { Length: > 0 } nodeId)
        {
            if (_rotationNode != nodeId)
            {
                // Another node: what the last one reported does not apply.
                _rotationNode = nodeId;
                ResetRotationStatus();
            }
            text = RotationText();
            PollRotationIfDue();
        }
        else
        {
            ForgetRotationStatus();
        }
        RouteChip.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        RouteChipText.Text = text?.ToUpper(Loc.I.Culture) ?? "";
    }

    private static string RouteEndText(ColituRouteEndpoint? end) => end?.Country ?? end?.Label ?? "?";

    private string? RotationText()
    {
        if (_rotationStatus is not { Active: true, CurrentExit: { } exit, NextChangeAt: { } next })
        {
            return null;
        }
        var label = exit.Country is { Length: 2 } country && !string.Equals(exit.Label, country, StringComparison.OrdinalIgnoreCase)
            ? $"{exit.Label} ({country})"
            : exit.Label;
        var left = next - DateTimeOffset.UtcNow;
        return left > TimeSpan.Zero
            ? Loc.I.Format("rotation.home", ("exit", label), ("time", ColituRotation.FormatCountdown(left)))
            : Loc.I.Format("rotation.homeDue", ("exit", label));
    }

    /// <summary>Asks for the status at the announced change time, never more often than every 60 s.</summary>
    private void PollRotationIfDue()
    {
        var now = DateTimeOffset.UtcNow;
        if (_rotationPolling || now < _rotationPollAt || now - _lastRotationPoll < TimeSpan.FromSeconds(ColituRotation.MinStatusPollSeconds))
        {
            return;
        }
        _ = PollRotationAsync();
    }

    private async Task PollRotationAsync()
    {
        _rotationPolling = true;
        _lastRotationPoll = DateTimeOffset.UtcNow;
        var epoch = _rotationEpoch;
        try
        {
            var status = await _vpn.GetRotationStatusAsync();
            if (epoch != _rotationEpoch)
            {
                return;
            }
            _rotationStatus = status;
            var now = DateTimeOffset.UtcNow;
            // Inactive (the node is not in the rotation mesh, too few exits): look again later.
            _rotationPollAt = now + (status is { Active: true } ? ColituRotation.NextPollDelay(status.NextChangeAt, now) : TimeSpan.FromMinutes(5));
        }
        catch (Exception ex)
        {
            if (epoch == _rotationEpoch)
            {
                _rotationPollAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(ColituRotation.MinStatusPollSeconds);
            }
            Logging.SaveLog("ColituMainWindow.PollRotationAsync", ex);
        }
        finally
        {
            _rotationPolling = false;
        }
    }

    /// <summary>The shown status is stale (new node, new preference): poll again as soon as allowed.</summary>
    private void ResetRotationStatus()
    {
        _rotationStatus = null;
        _rotationPollAt = DateTimeOffset.MinValue;
        _rotationEpoch++;
    }

    private void ForgetRotationStatus()
    {
        if (_rotationStatus != null || _rotationNode != null)
        {
            _rotationNode = null;
            ResetRotationStatus();
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e) => await ToggleConnectionAsync();

    private async void Unblock_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _vpn.DisconnectAsync();
            ShowToast(Loc.I["info.disconnected"]);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.Unblock", ex);
            ShowToast(Loc.I["err.generic"], true);
        }
        ApplyStatus();
    }

    private DateTime _lastToggle;

    private async Task ToggleConnectionAsync()
    {
        // A double click would start a connection and cancel it again right away.
        if (DateTime.UtcNow - _lastToggle < TimeSpan.FromMilliseconds(600))
        {
            return;
        }
        _lastToggle = DateTime.UtcNow;
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
        catch (ColituDevicePausedException paused)
        {
            await ShowPausedAsync(paused.Info);
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

        var free = IsFreePlan(subscription);
        var expiresSoon = active && !free && ExpiresAt(subscription!) is { } end && end - DateTimeOffset.UtcNow < TimeSpan.FromDays(3);
        PlanCtaButton.Visibility = !active || expiresSoon || free ? Visibility.Visible : Visibility.Collapsed;
        PlanCtaText.Text = free ? loc["plan.upgrade"] : active ? loc["plan.extend"] : loc["plan.choose"];

        // Account page
        var email = _auth.CurrentUser?.Email ?? "";
        AccountEmail.Text = email;
        AvatarText.Text = email.Length >= 2 ? email[..2].ToUpperInvariant() : "C";
        AccountId.Text = _auth.CurrentUser?.Id is { Length: > 0 } id ? $"ID · {id[..Math.Min(8, id.Length)]}" : "";
        AccountPlan.Text = active ? PlanTitle(subscription!) : loc["plan.none"];
        SetBadge(AccountPlanBadge, AccountPlanBadgeText, status);
        AccountUntil.Text = free ? loc["plan.freeHint"] : ExpiresAt(subscription) is { } until ? FormatDate(until) : "—";

        // Plan page strip
        CurrentPlanText.Text = active ? $"{PlanTitle(subscription!)} · {PlanDetailText(subscription!)}" : loc["plan.none"];
        SetBadge(CurrentPlanBadge, CurrentPlanBadgeText, status);
        CreditText.Text = loc.Format("brand.credit", ("brand", "COLITU LIMITED"));
        ApplyTrialBanner();
    }

    /// <summary>The free plan: 10 GB a month, renewed on the 1st.</summary>
    private static bool IsFreePlan(ColituSubscription? subscription) =>
        string.Equals(subscription?.PlanName, "Free", StringComparison.OrdinalIgnoreCase);

    private string PlanTitle(ColituSubscription subscription)
    {
        if (IsFreePlan(subscription))
        {
            return Loc.I["plan.freeName"];
        }
        if (string.Equals(subscription.Status, "trialing", StringComparison.OrdinalIgnoreCase))
        {
            return Loc.I["plan.trialName"];
        }
        return string.IsNullOrWhiteSpace(subscription.PlanName) ? "Colitu VPN" : subscription.PlanName!;
    }

    private string PlanDetailText(ColituSubscription subscription)
    {
        if (IsFreePlan(subscription))
        {
            return Loc.I["plan.freeHint"];
        }
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
            "quota_exceeded" => "plan.status.quota",
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
            SettingsKillSwitchLan.IsChecked = preferences.KillSwitchAllowLan;
            HomeAutoConnect.IsChecked = SettingsAutoConnect.IsChecked = preferences.AutoConnectEnabled;
            SettingsDns.IsChecked = preferences.DnsLeakProtectionEnabled;
            SettingsAdBlock.IsChecked = preferences.AdBlockEnabled;
            SettingsAdBlockRow.Visibility = ColituVpnService.AdBlockAvailable ? Visibility.Visible : Visibility.Collapsed;
            SettingsPrivacy.IsChecked = preferences.PrivacyModeEnabled;
            SettingsTray.IsChecked = preferences.CloseToTray;
            SettingsStartup.IsChecked = _vpn.LaunchAtStartup;
            ApplyModeHint();
            ApplySplitUi();
            ApplyRotationUi();
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
        await SavePreferencesAsync(_vpn.Preferences with { ConnectionMode = normalized, TunModePromptShown = true });
        if (normalized == ColituConnectionModes.Proxy)
        {
            // Said every time proxy mode is chosen: what it leaves unprotected.
            ShowToast(Loc.I["proxy.coverage"], true);
        }
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
            : box == SettingsAdBlock ? preferences with { AdBlockEnabled = on }
            : box == SettingsPrivacy ? preferences with { PrivacyModeEnabled = on, RuDirectNoticeShown = true }
            : box == SettingsTray ? preferences with { CloseToTray = on }
            : box == SettingsKillSwitchLan ? preferences with { KillSwitchAllowLan = on }
            : preferences;
        await SavePreferencesAsync(preferences);
    }

    private async Task SavePreferencesAsync(ColituVpnPreferences preferences)
    {
        // In TUN mode the kill switch also decides the core's strict routing, so it needs a
        // reconnect too (in proxy mode it only acts when the tunnel drops).
        var tunnelSettingsChanged = preferences.ConnectionMode != _vpn.Preferences.ConnectionMode
            || preferences.DnsLeakProtectionEnabled != _vpn.Preferences.DnsLeakProtectionEnabled
            || preferences.AdBlockEnabled != _vpn.Preferences.AdBlockEnabled
            || preferences.PrivacyModeEnabled != _vpn.Preferences.PrivacyModeEnabled
            || SplitSettingsChanged(preferences, _vpn.Preferences)
            || (preferences.IsTunMode && preferences.KillSwitchEnabled != _vpn.Preferences.KillSwitchEnabled);
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
            // Mode, kill switch, DNS protection, ad blocking and privacy mode are part of the core config: reconnect to apply.
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

    // ── Privacy mode (Russian sites outside the tunnel) ───────────────────
    private const string SplitTunnelingDocPath = "split-tunneling";

    /// <summary>
    /// The chip under "Connected" while Russian sites leave outside the tunnel, and the one-time
    /// notice the first time that happens (also for users who connected before this setting existed).
    /// </summary>
    private void ApplyRuDirectIndicator()
    {
        if (RuDirectChip == null)
        {
            return;
        }
        var active = _vpn.RussianSitesDirectActive;
        RuDirectChip.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        RuDirectChipText.Text = Loc.I["privacy.chip"].ToUpper(Loc.I.Culture);
        if (active && !_vpn.Preferences.RuDirectNoticeShown && RuDirectNotice.Visibility != Visibility.Visible)
        {
            RuDirectNotice.Visibility = Visibility.Visible;
            FadeIn(RuDirectNotice, 8);
        }
        else if (!active && RuDirectNotice.Visibility == Visibility.Visible)
        {
            // Disconnected or switched to a Russian server before answering: ask next time.
            RuDirectNotice.Visibility = Visibility.Collapsed;
        }
    }

    private void RuDirectChip_Click(object sender, MouseButtonEventArgs e)
    {
        Navigate("settings");
        SettingsPrivacyRow.BringIntoView();
    }

    private void PrivacyScope_Click(object sender, RoutedEventArgs e) =>
        OpenUrl($"https://docs.colitu.com/{Loc.I.Language}/{SplitTunnelingDocPath}");

    private async void RuDirectNoticeKeep_Click(object sender, RoutedEventArgs e)
    {
        RuDirectNotice.Visibility = Visibility.Collapsed;
        try
        {
            await _vpn.UpdatePreferencesAsync(_vpn.Preferences with { RuDirectNoticeShown = true });
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.RuDirectNoticeKeep_Click", ex);
        }
    }

    private async void RuDirectNoticeEnable_Click(object sender, RoutedEventArgs e)
    {
        RuDirectNotice.Visibility = Visibility.Collapsed;
        await SavePreferencesAsync(_vpn.Preferences with { PrivacyModeEnabled = true, RuDirectNoticeShown = true });
    }
}
