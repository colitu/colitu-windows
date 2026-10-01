using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using v2rayN.Services;

namespace v2rayN.Views;

/// <summary>
/// The Colitu shell: one window that hosts sign-in and the signed-in app, styled
/// like colitu.com. Page logic lives in the partial files next to this one.
/// </summary>
public partial class ColituMainWindow
{
    private static readonly IEasingFunction Ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };

    private readonly ColituVpnService _vpn = ColituVpnService.Instance;
    private readonly ColituAuthService _auth = ColituAuthService.Instance;
    private readonly ColituUpdateService _updater = ColituUpdateService.Instance;
    private readonly DispatcherTimer _clock;
    private readonly DispatcherTimer _refresh;
    private readonly DispatcherTimer _toastTimer;
    private bool _exiting;
    private bool _trayHintShown;
    private string _page = "home";
    private bool _offline;

    public ColituMainWindow()
    {
        InitializeComponent();
        ApplyLanguage(_vpn.Preferences.Language);
        Loc.I.Changed += OnLanguageChanged;

        _vpn.StatusChanged += _ => Dispatcher.BeginInvoke(ApplyStatus);
        _vpn.Notice += key => Dispatcher.BeginInvoke(() =>
        {
            ShowToast(Loc.I[key], key.StartsWith("err", StringComparison.Ordinal));
            ApplyStatus();
        });
        _auth.SessionExpired += _ => Dispatcher.BeginInvoke(async () => await OnSessionExpiredAsync());
        _updater.UpdateAvailable += info => Dispatcher.BeginInvoke(() => ShowUpdate(info));

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateSessionTimer();
        _clock.Start();

        _refresh = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _refresh.Tick += async (_, _) => await RefreshDataAsync();

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
        _toastTimer.Tick += (_, _) => HideToast();

        Loaded += OnLoaded;
        Closing += OnClosing;
        SourceInitialized += (_, _) => ApplyRoundedCorners();
        StateChanged += (_, _) => ApplyWindowShape();
        Root.SizeChanged += (_, _) => UpdateRootClip();

        BuildLanguageSelectors();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        TrayIcon.TrayMouseDoubleClick += (_, _) => ShowFromTray();
        TrayIcon.TrayLeftMouseUp += (_, _) => ShowFromTray();
        VersionText.Text = Loc.I.Format("settings.version", ("version", ColituAuthService.ClientVersion));
        await StartAsync();
    }

    private async Task StartAsync()
    {
        ShowView(LoadingView);
        _ = CheckForUpdatesAsync(quiet: true);
        await _vpn.RecoverFromPreviousRunAsync();
        var state = await _auth.InitializeAsync();
        if (state == ColituStartupState.VerificationRequired)
        {
            ShowVerify(codeJustSent: false);
            return;
        }
        if (state == ColituStartupState.SignedOut)
        {
            ShowAuth();
            if (_auth.SessionWasRevoked)
            {
                ShowAuthError(Loc.I["auth.expired"]);
            }
            return;
        }

        await EnterAppAsync(state == ColituStartupState.Offline);
    }

    private async Task EnterAppAsync(bool offline)
    {
        _offline = offline;
        ShowView(AppView);
        NavHome.IsChecked = true;
        Navigate("home", animate: false);
        ApplyPreferencesToUi();
        ApplyAccount();
        ApplyStatus();
        _ = Dispatcher.BeginInvoke(() => MoveNavThumb(false), DispatcherPriority.Loaded);

        if (offline)
        {
            ShowToast(Loc.I["home.offline"], true);
            _refresh.Interval = TimeSpan.FromSeconds(20);
        }
        else
        {
            await RefreshDataAsync(includeAccount: false);
        }
        _refresh.Start();
        StartSupportPolling();

        if (!_planRequired || offline)
        {
            if (await _vpn.TryAutoConnectAsync())
            {
                ApplyStatus();
            }
        }
    }

    /// <summary>Reloads the account, plan, servers and usage. Also resumes an offline session.</summary>
    private async Task RefreshDataAsync(bool includeAccount = true)
    {
        if (!_auth.HasSession)
        {
            return;
        }

        try
        {
            if (_offline)
            {
                var state = await _auth.ResumeAsync();
                if (state == ColituStartupState.SignedOut)
                {
                    await OnSessionExpiredAsync();
                    return;
                }
                if (state == ColituStartupState.VerificationRequired)
                {
                    ShowVerify(codeJustSent: false);
                    return;
                }
                if (state == ColituStartupState.Offline)
                {
                    return;
                }
                _offline = false;
                _refresh.Interval = TimeSpan.FromMinutes(5);
            }
            else if (includeAccount)
            {
                await _auth.RefreshAccountAsync();
            }

            var servers = await _vpn.GetServersAsync();
            _servers = servers.Servers;
            BuildCategoryFilter();
            _planRequired = servers.PlanRequired || _auth.CurrentSubscription?.Active != true;
            _usage = _planRequired ? null : await _vpn.GetStatsAsync();
            ApplyAccount();
            RenderServers();
            ApplyStatus();
            _ = MeasurePingsAsync();
            if (_page == "account")
            {
                await LoadDevicesAsync();
            }
        }
        catch (ColituApiException ex) when (ex.Terminal)
        {
            await OnSessionExpiredAsync();
        }
        catch (Exception ex) when (ColituVpnService.IsNetworkFailure(ex))
        {
            if (!_offline)
            {
                _offline = true;
                _refresh.Interval = TimeSpan.FromSeconds(20);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.RefreshDataAsync", ex);
        }
    }

    private async Task OnSessionExpiredAsync()
    {
        if (AuthView.Visibility == Visibility.Visible)
        {
            return;
        }
        await _vpn.ForgetAccountAsync();
        StopSupportPolling();
        ShowAuth();
        ShowAuthError(Loc.I["auth.expired"]);
        ShowFromTray();
    }

    // ── Views and navigation ───────────────────────────────────────────────
    private void ShowView(FrameworkElement view)
    {
        foreach (var candidate in new FrameworkElement[] { LoadingView, AuthView, ResetView, VerifyView, AppView })
        {
            candidate.Visibility = candidate == view ? Visibility.Visible : Visibility.Collapsed;
        }
        FadeIn(view, 10);
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string page } && IsLoaded)
        {
            Navigate(page);
        }
    }

    private void Navigate(string page, bool animate = true)
    {
        _page = page;
        var target = page switch
        {
            "locations" => (FrameworkElement)LocationsPage,
            "plan" => PlanPage,
            "account" => AccountPage,
            "settings" => SettingsPage,
            "support" => SupportPage,
            _ => HomePage
        };
        foreach (FrameworkElement child in Pages.Children)
        {
            child.Visibility = child == target ? Visibility.Visible : Visibility.Collapsed;
        }

        // Support has no tab of its own: it opens from the floating launcher.
        var nav = page switch
        {
            "locations" => NavLocations,
            "plan" => NavPlan,
            "account" => NavAccount,
            "settings" => NavSettings,
            "support" => null,
            _ => NavHome
        };
        if (nav == null)
        {
            foreach (var item in NavList.Children.OfType<RadioButton>())
            {
                item.IsChecked = false;
            }
        }
        else if (nav.IsChecked != true)
        {
            nav.IsChecked = true;
        }
        NavThumb.BeginAnimation(OpacityProperty, new DoubleAnimation(nav == null ? 0 : 1, TimeSpan.FromMilliseconds(260)));
        MoveNavThumb(animate);
        if (animate)
        {
            FadeIn(target, 14);
        }
        if (target == HomePage)
        {
            StaggerIn(HomeCards);
        }

        switch (page)
        {
            case "locations":
                RenderServers();
                _ = MeasurePingsAsync();
                break;
            case "account":
                _ = LoadDevicesAsync();
                break;
            case "support":
                _ = OpenSupportAsync();
                break;
        }
        UpdateSupportPolling();
    }

    private void MoveNavThumb(bool animate)
    {
        var active = NavList.Children.OfType<RadioButton>().FirstOrDefault(item => item.IsChecked == true);
        if (active == null || active.ActualWidth <= 0)
        {
            return;
        }

        var x = active.TranslatePoint(new Point(0, 0), NavList).X;
        if (!animate)
        {
            NavThumbShift.BeginAnimation(TranslateTransform.XProperty, null);
            NavThumb.BeginAnimation(WidthProperty, null);
            NavThumbShift.X = x;
            NavThumb.Width = active.ActualWidth;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(520);
        NavThumbShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, duration) { EasingFunction = Ease });
        NavThumb.BeginAnimation(WidthProperty, new DoubleAnimation(active.ActualWidth, duration) { EasingFunction = Ease });
    }

    private static void FadeIn(FrameworkElement element, double offset)
    {
        var shift = new TranslateTransform(0, offset);
        element.RenderTransform = shift;
        var duration = TimeSpan.FromMilliseconds(560);
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = Ease });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset, 0, duration) { EasingFunction = Ease });
    }

    // ── Language ───────────────────────────────────────────────────────────
    private void ApplyLanguage(string? language)
    {
        Loc.I.SetLanguage(language);
        var culture = Loc.I.Culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    private void OnLanguageChanged()
    {
        var culture = Loc.I.Culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        BuildLanguageSelectors();
        VersionText.Text = Loc.I.Format("settings.version", ("version", ColituAuthService.ClientVersion));
        ApplyAuthMode();
        ApplyAccount();
        ApplyStatus();
        ApplyModeHint();
        BuildCategoryFilter();
        RenderServers();
        ApplyVerifyTexts();
        RenderSupportList();
        _ = LoadDevicesAsync();
        Dispatcher.BeginInvoke(() => MoveNavThumb(false), DispatcherPriority.Loaded);
    }

    private void BuildLanguageSelectors()
    {
        foreach (var (host, group) in new[] { ((Panel)AuthLanguageList, "AuthLang"), (SettingsLanguageList, "SettingsLang") })
        {
            host.Children.Clear();
            foreach (var language in Loc.Languages)
            {
                var option = new RadioButton
                {
                    Content = language switch { "ru" => "Русский", "tr" => "Türkçe", _ => "English" },
                    GroupName = group,
                    Tag = language,
                    Style = (Style)FindResource("SegmentOption"),
                    IsChecked = language == Loc.I.Language,
                    MinHeight = group == "AuthLang" ? 30 : 40,
                    FontSize = group == "AuthLang" ? 12.5 : 13.5
                };
                if (group == "AuthLang")
                {
                    option.Padding = new Thickness(4, 0, 4, 0);
                    System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(option, true);
                }
                option.Checked += LanguageOption_Checked;
                host.Children.Add(option);
            }
        }
    }

    private async void LanguageOption_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string language } || language == Loc.I.Language)
        {
            return;
        }

        // Rebuilding the selectors inside their own Checked event would recurse; defer it.
        await Dispatcher.InvokeAsync(() => ApplyLanguage(language), DispatcherPriority.Background);
        await _vpn.UpdatePreferencesAsync(_vpn.Preferences with { Language = language });
    }

    // ── Toast ──────────────────────────────────────────────────────────────
    private void ShowToast(string text, bool error = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        ToastText.Text = text;
        ToastDot.Fill = (Brush)FindResource(error ? "DangerBrush" : "SuccessBrush");
        var duration = TimeSpan.FromMilliseconds(450);
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, duration) { EasingFunction = Ease });
        ToastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, duration) { EasingFunction = Ease });
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(text.Length / 14.0, 3.5, 8));
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        var duration = TimeSpan.FromMilliseconds(400);
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, duration) { EasingFunction = Ease });
        ToastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, duration) { EasingFunction = Ease });
    }

    // ── Motion ─────────────────────────────────────────────────────────────
    /// <summary>Fades and lifts the children of a panel in one after another.</summary>
    private static void StaggerIn(Panel panel, double offset = 18)
    {
        var index = 0;
        foreach (UIElement child in panel.Children)
        {
            var shift = new TranslateTransform(0, offset);
            child.RenderTransform = shift;
            var begin = TimeSpan.FromMilliseconds(80 * index++);
            var duration = TimeSpan.FromMilliseconds(640);
            child.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { BeginTime = begin, EasingFunction = Ease });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset, 0, duration) { BeginTime = begin, EasingFunction = Ease });
        }
    }

    // ── Window chrome and tray ─────────────────────────────────────────────
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        var keepRunning = _vpn.Preferences.CloseToTray && AppView.Visibility == Visibility.Visible;
        if (keepRunning)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                TrayIcon.ShowNotification("Colitu VPN", Loc.I["tray.hidden"]);
            }
            return;
        }

        e.Cancel = true;
        _ = ExitAsync();
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        Topmost = true;
        Topmost = false;
    }

    /// <summary>Called when a second copy of the app is started.</summary>
    public void BringToFront() => ShowFromTray();

    private void TrayOpen_Click(object sender, RoutedEventArgs e) => ShowFromTray();

    private async void TrayConnect_Click(object sender, RoutedEventArgs e)
    {
        if (AppView.Visibility != Visibility.Visible)
        {
            ShowFromTray();
            return;
        }
        if (_vpn.KillSwitchEngaged && _vpn.Status != ColituVpnStatus.Connected)
        {
            await _vpn.DisconnectAsync();
            ApplyStatus();
            return;
        }
        await ToggleConnectionAsync();
    }

    private async void TrayExit_Click(object sender, RoutedEventArgs e) => await ExitAsync();

    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }
        _exiting = true;
        Hide();
        try
        {
            if (_vpn.Status is ColituVpnStatus.Connected or ColituVpnStatus.Connecting or ColituVpnStatus.Reconnecting)
            {
                await _vpn.DisconnectAsync();
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.ExitAsync", ex);
        }
        TrayIcon.Dispose();
        Application.Current.Shutdown();
    }

    private const double FrameRadius = 18;

    private void ApplyRoundedCorners()
    {
        // The window is transparent and draws its own rounded frame (WindowFrame), so the
        // corners are soft on Windows 10 and 11 alike. A maximized borderless window must
        // stop at the work area instead of covering the taskbar.
        var area = SystemParameters.WorkArea;
        MaxWidth = area.Width + 14;
        MaxHeight = area.Height + 14;
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var dark = 1;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        }
        catch
        {
            // Older Windows: no dark title-bar hint needed.
        }
        ApplyWindowShape();
    }

    private void ApplyWindowShape()
    {
        var maximized = WindowState == WindowState.Maximized;
        // A maximized chrome window hangs 7px past each screen edge.
        WindowFrame.Margin = maximized ? new Thickness(7) : new Thickness(0);
        WindowFrame.CornerRadius = new CornerRadius(maximized ? 0 : FrameRadius);
        WindowFrame.BorderThickness = new Thickness(maximized ? 0 : 1);
        UpdateRootClip();
    }

    private void UpdateRootClip()
    {
        if (Root.ActualWidth <= 0 || Root.ActualHeight <= 0)
        {
            return;
        }
        var radius = WindowState == WindowState.Maximized ? 0 : FrameRadius - 1;
        Root.Clip = new RectangleGeometry(new Rect(0, 0, Root.ActualWidth, Root.ActualHeight), radius, radius);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static void OpenUrl(string url) => ColituShell.OpenUrl(url);

    private string LocalizedPath(string path) => $"{ColituAuthService.WebBaseUrl}{path}";
}
