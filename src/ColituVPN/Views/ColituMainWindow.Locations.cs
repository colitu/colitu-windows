using System.Windows.Controls;
using System.Windows.Media;
using v2rayN.Services;

namespace v2rayN.Views;

public partial class ColituMainWindow
{
    private bool _renderingServers;
    private bool _buildingCategories;
    private string _category = "all";

    /// <summary>Country groups the user has opened (kept while the window is open).</summary>
    private readonly HashSet<string> _expandedGroups = new(StringComparer.Ordinal);
    /// <summary>Server whose country was last opened automatically, so a manual collapse sticks.</summary>
    private string? _autoExpandedFor;

    /// <summary>Use-case filters over the location list, in the panel's order.</summary>
    private static readonly string[] Categories = ["all", "streaming", "gaming", "privacy", "speed", "torrent", "ai"];

    private void BuildCategoryFilter()
    {
        if (CategoryList == null)
        {
            return;
        }

        _buildingCategories = true;
        try
        {
            CategoryList.Children.Clear();
            foreach (var category in Categories)
            {
                var count = category == "all" ? _servers.Count : _servers.Count(server => server.Categories.Contains(category));
                var option = new RadioButton
                {
                    Content = category == "all" ? Loc.I["cat.all"] : $"{Loc.I["cat." + category]}  {count}",
                    GroupName = "ServerCategory",
                    Tag = category,
                    Style = (Style)FindResource("SegmentOption"),
                    IsChecked = category == _category,
                    MinHeight = 36,
                    FontSize = 13.5,
                    Padding = new Thickness(14, 0, 14, 0),
                    Opacity = category == "all" || count > 0 ? 1 : 0.55
                };
                option.Checked += Category_Checked;
                CategoryList.Children.Add(option);
            }
        }
        finally
        {
            _buildingCategories = false;
        }
    }

    private void Category_Checked(object sender, RoutedEventArgs e)
    {
        if (_buildingCategories || sender is not RadioButton { Tag: string category })
        {
            return;
        }
        _category = category;
        RenderServers();
        FadeIn(ServerList, 8);
    }

    private void RenderServers()
    {
        if (ServerList == null)
        {
            return;
        }
        if (CategoryList.Children.Count == 0)
        {
            BuildCategoryFilter();
        }

        var query = ServerSearchBox.Text.Trim();
        // Simple mode: a flat country list, no category filters and no multihop routes.
        var advanced = _vpn.AdvancedMode;
        CategoryHost.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        var filtered = advanced && _category != "all";
        var connectedId = _vpn.Status == ColituVpnStatus.Connected ? _vpn.ConnectedServer?.Id : null;
        var rows = new List<ColituServerRow>();
        var auto = ColituServerRow.Auto(ServerLabel(_vpn.RecommendedServer));
        if (query.Length == 0 && !filtered)
        {
            rows.Add(auto);
        }

        ColituServerRow BuildRow(ColituVpnServer server, bool city = false)
        {
            var row = ColituServerRow.From(server, _pings.TryGetValue(server.Id ?? "", out var ping) ? ping : null, city);
            row.MarkState(server.Id == connectedId, server.Id == _vpn.SavedServerId && !_vpn.IsAutoSelection);
            return row;
        }

        // The country of the picked (or connected) server starts open; after that the user decides.
        var focusId = connectedId ?? (_vpn.IsAutoSelection ? null : _vpn.SavedServerId);
        if (focusId != _autoExpandedFor)
        {
            _autoExpandedFor = focusId;
            var focus = _servers.FirstOrDefault(s => s.Id == focusId);
            if (focus != null)
            {
                _expandedGroups.Add(ColituServerGroups.KeyOf(focus));
            }
        }

        // With a search text the matches are shown flat, so none is hidden inside a closed country.
        var flat = query.Length > 0;
        var candidates = _servers.Where(server => (!filtered || server.Categories.Contains(_category))
            && (!flat || ColituServerRow.From(server).Matches(query)));
        foreach (var group in ColituServerGroups.Build(candidates, ColituServerRow.CountryName, StringComparer.Create(Loc.I.Culture, true), flat))
        {
            if (group.IsSingle)
            {
                rows.Add(BuildRow(group.Servers[0]));
                continue;
            }

            var expanded = _expandedGroups.Contains(group.Key);
            var best = ColituServerGroups.BestPing(group.Servers, server => _pings.TryGetValue(server.Id ?? "", out var ping) ? ping : null);
            var header = ColituServerRow.Group(group, best, expanded);
            if (!expanded)
            {
                // A closed country still tells that its picked / connected city is in it.
                header.MarkGroupState(group.Servers.Any(s => s.Id == connectedId), group.Servers.Any(s => s.Id == _vpn.SavedServerId && !_vpn.IsAutoSelection));
            }
            rows.Add(header);
            if (expanded)
            {
                rows.AddRange(group.Servers.Select(server => BuildRow(server, true)));
            }
        }

        // Double-VPN routes follow the locations in a section of their own. They have no use-case
        // categories, so a category filter hides them. Ping is measured to the entry node only.
        if (!filtered && advanced)
        {
            var routes = new List<ColituServerRow>();
            foreach (var route in _vpn.MultihopRoutes.OrderBy(s => s.Name ?? "", StringComparer.Create(Loc.I.Culture, true)))
            {
                var row = ColituServerRow.From(route, _pings.TryGetValue(route.Id ?? "", out var ping) ? ping : null);
                if (query.Length > 0 && !row.Matches(query))
                {
                    continue;
                }
                row.MarkState(route.Id == connectedId, route.Id == _vpn.SavedServerId && !_vpn.IsAutoSelection);
                routes.Add(row);
            }
            if (routes.Count > 0)
            {
                rows.Add(ColituServerRow.Header(Loc.I["multihop.section"], Loc.I["multihop.sectionHint"]));
                rows.AddRange(routes);
            }
        }

        auto.MarkState(_vpn.IsAutoSelection && _vpn.Status == ColituVpnStatus.Connected, _vpn.IsAutoSelection);

        _renderingServers = true;
        try
        {
            ServerList.ItemsSource = rows;
            ServerList.SelectedItem = rows.FirstOrDefault(row => row.IsAuto ? _vpn.IsAutoSelection : !row.IsHeader && !row.IsGroup && !_vpn.IsAutoSelection && row.Server?.Id == _vpn.SavedServerId);
        }
        finally
        {
            _renderingServers = false;
        }

        var online = _servers.Count;
        LocationsSubtitle.Text = Loc.I.Format("locations.sub", ("n", online));
        ServerListEmpty.Text = _servers.Count == 0 && query.Length == 0
            ? (_planRequired ? Loc.I["plan.noneHint"] : Loc.I["server.none"])
            : filtered && query.Length == 0 ? Loc.I["cat.empty"] : Loc.I["locations.empty"];
        ServerListEmpty.Visibility = rows.Count(row => !row.IsAuto && !row.IsHeader) == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ServerSearch_TextChanged(object sender, TextChangedEventArgs e) => RenderServers();

    private void ToggleGroup(string key)
    {
        if (!_expandedGroups.Remove(key))
        {
            _expandedGroups.Add(key);
        }

        // Rebuilding the list resets the scroll position; put it back (item-based offset).
        var scroll = FindScrollViewer(ServerList);
        var offset = scroll?.VerticalOffset ?? 0;
        RenderServers();
        ServerList.UpdateLayout();
        scroll?.ScrollToVerticalOffset(offset);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }
            if (FindScrollViewer(child) is { } nested)
            {
                return nested;
            }
        }
        return null;
    }

    // ── Pings ───────────────────────────────────────────────────────────────
    /// <summary>Last measured ping per server id. The panel sends where to measure, not a number.</summary>
    private readonly Dictionary<string, int> _pings = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _pingsMeasuredAt = DateTimeOffset.MinValue;
    private bool _measuringPings;

    /// <summary>
    /// Pings every location from this PC, outside the tunnel (see ColituLatency), at most
    /// every 30 seconds; called when the list loads and whenever the Locations tab opens.
    /// </summary>
    private async Task MeasurePingsAsync(bool force = false)
    {
        if (_measuringPings || _servers.Count + _vpn.MultihopRoutes.Count == 0 || (!force && DateTimeOffset.UtcNow - _pingsMeasuredAt < TimeSpan.FromSeconds(30)))
        {
            return;
        }
        _measuringPings = true;
        try
        {
            // Through the service: it keeps the results (failed pings too) for the automatic order.
            var measured = await _vpn.MeasurePingsAsync(_servers.Concat(_vpn.MultihopRoutes).ToList());
            _pingsMeasuredAt = DateTimeOffset.UtcNow;
            foreach (var (id, ms) in measured)
            {
                _pings[id] = ms;
            }
            if (_page == "locations" && measured.Count > 0)
            {
                RenderServers();
            }
            // The recommended server may have changed with the pings.
            ApplyLocationCard();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.MeasurePingsAsync", ex);
        }
        finally
        {
            _measuringPings = false;
        }
    }

    private async void ServerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_renderingServers)
        {
            return;
        }

        // A country header opens / closes its cities instead of picking a server.
        if (ServerList.SelectedItem is ColituServerRow { IsGroup: true } group)
        {
            ToggleGroup(group.GroupKey);
            return;
        }

        if (ServerList.SelectedItem is not ColituServerRow { IsHeader: false } row)
        {
            return;
        }

        var server = row.IsAuto ? null : row.Server;
        var wasActive = _vpn.Status is ColituVpnStatus.Connected or ColituVpnStatus.Reconnecting;
        if (server == null) _vpn.SelectAuto(); else _vpn.SelectServer(server);
        ApplyLocationCard();

        if (!wasActive)
        {
            Navigate("home");
            return;
        }

        Navigate("home");
        ShowToast(Loc.I.Format("locations.switching", ("server", row.Title)));
        try
        {
            await _vpn.SwitchServerAsync(server);
            // Not after a switch that another choice cancelled.
            if (_vpn.Status == ColituVpnStatus.Connected)
            {
                ShowToast(Loc.I.Format("locations.switched", ("server", ServerLabel(_vpn.ConnectedServer) ?? row.Title)));
            }
        }
        catch (ColituPlanRequiredException)
        {
            _planRequired = true;
            ApplyAccount();
            Navigate("plan");
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, true);
        }
        ApplyStatus();
    }
}

/// <summary>One line of the locations list (and the home location card).</summary>
public sealed class ColituServerRow
{
    private static readonly Brush PingGood = new SolidColorBrush(Color.FromRgb(0x5E, 0xE0, 0xA0));
    private static readonly Brush PingFair = new SolidColorBrush(Color.FromRgb(0xFF, 0xB5, 0x47));
    private static readonly Brush PingPoor = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x81));
    private static readonly Brush Unlit = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    // State badges follow ColituBadge: green glass when connected, neutral glass when offline, lavender when picked.
    private static readonly Brush ConnectedBrush = new SolidColorBrush(Color.FromArgb(0x26, 0x5E, 0xE0, 0xA0));
    private static readonly Brush ConnectedText = new SolidColorBrush(Color.FromRgb(0x5E, 0xE0, 0xA0));
    private static readonly Brush OfflineBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
    private static readonly Brush OfflineText = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xAB));
    private static readonly Brush SelectedText = new SolidColorBrush(Color.FromRgb(0x0B, 0x0A, 0x14));
    private static readonly Brush SelectedBrush = new LinearGradientBrush(Color.FromRgb(0xB4, 0xA4, 0xFF), Color.FromRgb(0x8B, 0x78, 0xFF), 0);

    public ColituVpnServer? Server { get; init; }
    public bool IsAuto { get; init; }
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public Uri? FlagUri { get; init; }
    public Visibility FlagVisibility => FlagUri == null ? Visibility.Collapsed : Visibility.Visible;
    /// <summary>A country header (two or more locations): opens / closes its city rows.</summary>
    public bool IsGroup { get; init; }
    public string GroupKey { get; init; } = "";
    public bool Expanded { get; init; }
    /// <summary>A city row inside an open country: indented, no flag.</summary>
    public bool IsCity { get; init; }
    public double ChevronAngle => Expanded ? 90 : 0;
    public Visibility ChevronVisibility => IsGroup ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>A section title between locations (not selectable).</summary>
    public bool IsHeader { get; init; }
    public string HeaderHint { get; init; } = "";
    public Visibility RowVisibility => IsHeader ? Visibility.Collapsed : Visibility.Visible;
    public Visibility HeaderVisibility => IsHeader ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HeaderHintVisibility => HeaderHint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>A double-VPN route: two flags (entry, exit) instead of one.</summary>
    public bool IsMultihop => Server?.IsMultihop == true;
    public Uri? EntryFlagUri { get; init; }
    public Uri? ExitFlagUri { get; init; }
    public Visibility PairFlagVisibility => IsMultihop ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SingleFlagVisibility => IsMultihop || IsCity ? Visibility.Collapsed : Visibility.Visible;
    /// <summary>"Estimated · +1 hop": the ping of a route is measured to its entry node only.</summary>
    public string PingNote => IsMultihop ? Loc.I["multihop.ping"] : "";
    public Visibility PingNoteVisibility => IsMultihop ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AutoVisibility => IsAuto ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>The node runs one of Colitu's ad-blocking DNS servers.</summary>
    public bool AdBlock { get; init; }
    public Visibility AdBlockVisibility => AdBlock ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AdFreeYoutubeVisibility => AdFreeYoutube ? Visibility.Visible : Visibility.Collapsed;
    public string AdBlockText => Loc.I["cat.adblock"];
    /// <summary>The panel marks this node as giving ad-free YouTube.</summary>
    public bool AdFreeYoutube { get; init; }
    public string AdFreeYoutubeText => Loc.I["service.youtube_adfree"];
    public int LoadLevel { get; init; }
    public string? LoadText => LoadLevel > 0 ? Loc.I[LoadLevel switch { 1 => "server.load.low", 2 => "server.load.medium", _ => "server.load.high" }] : null;

    /// <summary>Ping from this PC in ms, measured outside the tunnel; null until measured.</summary>
    public int? Ping { get; init; }
    public Visibility PingVisibility => IsAuto || IsHeader ? Visibility.Collapsed : Visibility.Visible;
    public string PingText => Ping is > 0 ? $"{Ping} ms" : "— ms";
    public Brush PingBrush => Ping switch
    {
        null => Unlit,
        <= 80 => PingGood,
        <= 200 => PingFair,
        _ => PingPoor
    };
    public Brush PingTextBrush => Ping == null ? OfflineText : PingBrush;
    /// <summary>Signal bars from the ping: three under 80 ms, two under 200 ms, one above.</summary>
    private int PingLevel => Ping switch { null => 0, <= 80 => 3, <= 200 => 2, _ => 1 };
    public Brush PingBar1 => PingLevel >= 1 ? PingBrush : Unlit;
    public Brush PingBar2 => PingLevel >= 2 ? PingBrush : Unlit;
    public Brush PingBar3 => PingLevel >= 3 ? PingBrush : Unlit;
    public string? PingToolTip => IsMultihop ? Loc.I["multihop.ping"] : LoadText;
    public bool IsSelectable => IsAuto || IsGroup || (!IsHeader && Server?.Available == true);
    public string StateText { get; private set; } = "";
    public Visibility StateVisibility => StateText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Brush? StateBackground { get; private set; }
    public Brush? StateForeground { get; private set; }

    public void MarkState(bool connected, bool selected)
    {
        StateText = connected ? Loc.I["server.connected"] : !IsSelectable ? Loc.I["server.offline"] : selected ? Loc.I["server.selected"] : "";
        StateBackground = connected ? ConnectedBrush : !IsSelectable ? OfflineBrush : SelectedBrush;
        StateForeground = connected ? ConnectedText : !IsSelectable ? OfflineText : SelectedText;
    }

    /// <summary>State badge of a closed country that holds the connected / picked location.</summary>
    public void MarkGroupState(bool connected, bool selected)
    {
        StateText = connected ? Loc.I["server.connected"] : selected ? Loc.I["server.selected"] : "";
        StateBackground = connected ? ConnectedBrush : SelectedBrush;
        StateForeground = connected ? ConnectedText : SelectedText;
    }

    public bool Matches(string query)
    {
        return new[] { Title, Subtitle, Server?.Country, Server?.CountryCode, Server?.City, Server?.Entry?.City, Server?.Entry?.Country, Server?.Exit?.City, Server?.Exit?.Country }
            .Any(value => value?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true);
    }

    /// <summary>"Best server"; with <paramref name="recommended"/> (the server it connects to first) named under it.</summary>
    public static ColituServerRow Auto(string? recommended = null) => new()
    {
        IsAuto = true,
        Title = Loc.I["server.auto"],
        Subtitle = recommended is { Length: > 0 } ? Loc.I.Format("server.autoNow", ("server", recommended)) : Loc.I["server.autoHint"]
    };

    public static ColituServerRow Header(string title, string hint = "") => new()
    {
        IsHeader = true,
        Title = title,
        HeaderHint = hint
    };

    /// <summary>Header of a country with two or more locations: flag, name, count, best ping, chevron.</summary>
    public static ColituServerRow Group(ColituServerGroup group, int? bestPing, bool expanded) => new()
    {
        IsGroup = true,
        GroupKey = group.Key,
        Expanded = expanded,
        Title = group.Name,
        Subtitle = Loc.I.Count("location", group.Servers.Count),
        FlagUri = ColituVpnServer.FlagUriFor(group.CountryCode),
        Ping = bestPing
    };

    /// <param name="city">A row inside an open country: the city is the title.</param>
    public static ColituServerRow From(ColituVpnServer server, int? ping = null, bool city = false)
    {
        if (server.IsMultihop)
        {
            return FromRoute(server, ping);
        }
        var country = CountryName(server.CountryCode) ?? server.Country;
        var name = FirstNonEmpty(server.DisplayName, server.Name, country, "Colitu");
        var categories = server.Categories.Count == 0 ? [] : new[] { string.Join(", ", server.Categories.Select(category => Loc.Has("cat." + category) ? Loc.I.Get("cat." + category) : category)) };
        var title = name;
        var lead = new[] { country, server.City };
        if (city)
        {
            // Under its country header: the city names the row; the full name only if it adds something (e.g. "Letonya (Köprü)").
            title = FirstNonEmpty(server.City, name);
            lead = [name, null];
        }
        var details = lead
            .Where(value => !string.IsNullOrWhiteSpace(value)
                && !string.Equals(value, title, StringComparison.CurrentCultureIgnoreCase)
                && !(city && string.Equals(value, country, StringComparison.CurrentCultureIgnoreCase)))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Concat(categories);
        return new ColituServerRow
        {
            Server = server,
            Title = title,
            Subtitle = string.Join(" · ", details),
            IsCity = city,
            FlagUri = server.HasLocalFlag ? server.FlagResourceUri : null,
            Ping = ping,
            AdBlock = ColituVpnService.HostsAdBlockDns(server.Host),
            AdFreeYoutube = server.HasAdFreeYoutube,
            LoadLevel = server.Load switch
            {
                null => 0,
                <= 40 => 1,
                <= 70 => 2,
                _ => 3
            }
        };
    }

    /// <summary>"🇫🇮 → 🇩🇪 Helsinki → Frankfurt": both flags, the route's name and the countries.</summary>
    private static ColituServerRow FromRoute(ColituVpnServer route, int? ping)
    {
        var entry = CountryName(route.Entry?.Country) ?? route.Entry?.Label ?? "";
        var exit = CountryName(route.Exit?.Country) ?? route.Exit?.Label ?? "";
        var name = FirstNonEmpty(route.DisplayName, route.Name,
            string.Join(" → ", new[] { route.Entry?.Label, route.Exit?.Label }.Where(value => !string.IsNullOrWhiteSpace(value))), "Colitu");
        return new ColituServerRow
        {
            Server = route,
            Title = name,
            Subtitle = Loc.I.Format("multihop.sub", ("entry", entry), ("exit", exit)),
            FlagUri = route.HasLocalFlag ? route.FlagResourceUri : null,
            EntryFlagUri = ColituVpnServer.FlagUriFor(route.Entry?.Country),
            ExitFlagUri = ColituVpnServer.FlagUriFor(route.Exit?.Country),
            Ping = ping,
            LoadLevel = route.Load switch
            {
                null => 0,
                <= 40 => 1,
                <= 70 => 2,
                _ => 3
            }
        };
    }

    /// <summary>Country name in the app language; English from Windows for the rest.</summary>
    public static string? CountryName(string? code)
    {
        code = (code ?? "").Trim().ToUpperInvariant();
        if (code == "UK") code = "GB";
        if (code.Length != 2)
        {
            return null;
        }
        if (Countries.TryGetValue(code, out var names))
        {
            return Loc.I.Language switch { "ru" => names[0], "tr" => names[1], _ => names[2] };
        }
        try
        {
            return new RegionInfo(code).EnglishName;
        }
        catch (ArgumentException)
        {
            return code;
        }
    }

    // code → [ru, tr, en]
    private static readonly Dictionary<string, string[]> Countries = new()
    {
        ["AE"] = ["ОАЭ", "BAE", "United Arab Emirates"],
        ["AL"] = ["Албания", "Arnavutluk", "Albania"],
        ["AM"] = ["Армения", "Ermenistan", "Armenia"],
        ["AT"] = ["Австрия", "Avusturya", "Austria"],
        ["AU"] = ["Австралия", "Avustralya", "Australia"],
        ["AZ"] = ["Азербайджан", "Azerbaycan", "Azerbaijan"],
        ["BE"] = ["Бельгия", "Belçika", "Belgium"],
        ["BG"] = ["Болгария", "Bulgaristan", "Bulgaria"],
        ["BR"] = ["Бразилия", "Brezilya", "Brazil"],
        ["BY"] = ["Беларусь", "Belarus", "Belarus"],
        ["CA"] = ["Канада", "Kanada", "Canada"],
        ["CH"] = ["Швейцария", "İsviçre", "Switzerland"],
        ["CY"] = ["Кипр", "Kıbrıs", "Cyprus"],
        ["CZ"] = ["Чехия", "Çekya", "Czechia"],
        ["DE"] = ["Германия", "Almanya", "Germany"],
        ["DK"] = ["Дания", "Danimarka", "Denmark"],
        ["EE"] = ["Эстония", "Estonya", "Estonia"],
        ["ES"] = ["Испания", "İspanya", "Spain"],
        ["FI"] = ["Финляндия", "Finlandiya", "Finland"],
        ["FR"] = ["Франция", "Fransa", "France"],
        ["GB"] = ["Великобритания", "Birleşik Krallık", "United Kingdom"],
        ["GE"] = ["Грузия", "Gürcistan", "Georgia"],
        ["GR"] = ["Греция", "Yunanistan", "Greece"],
        ["HK"] = ["Гонконг", "Hong Kong", "Hong Kong"],
        ["HU"] = ["Венгрия", "Macaristan", "Hungary"],
        ["IE"] = ["Ирландия", "İrlanda", "Ireland"],
        ["IL"] = ["Израиль", "İsrail", "Israel"],
        ["IN"] = ["Индия", "Hindistan", "India"],
        ["IS"] = ["Исландия", "İzlanda", "Iceland"],
        ["IT"] = ["Италия", "İtalya", "Italy"],
        ["JP"] = ["Япония", "Japonya", "Japan"],
        ["KR"] = ["Южная Корея", "Güney Kore", "South Korea"],
        ["KZ"] = ["Казахстан", "Kazakistan", "Kazakhstan"],
        ["LT"] = ["Литва", "Litvanya", "Lithuania"],
        ["LU"] = ["Люксембург", "Lüksemburg", "Luxembourg"],
        ["LV"] = ["Латвия", "Letonya", "Latvia"],
        ["MD"] = ["Молдова", "Moldova", "Moldova"],
        ["NL"] = ["Нидерланды", "Hollanda", "Netherlands"],
        ["NO"] = ["Норвегия", "Norveç", "Norway"],
        ["PL"] = ["Польша", "Polonya", "Poland"],
        ["PT"] = ["Португалия", "Portekiz", "Portugal"],
        ["RO"] = ["Румыния", "Romanya", "Romania"],
        ["RS"] = ["Сербия", "Sırbistan", "Serbia"],
        ["RU"] = ["Россия", "Rusya", "Russia"],
        ["SE"] = ["Швеция", "İsveç", "Sweden"],
        ["SG"] = ["Сингапур", "Singapur", "Singapore"],
        ["SK"] = ["Словакия", "Slovakya", "Slovakia"],
        ["TR"] = ["Турция", "Türkiye", "Türkiye"],
        ["UA"] = ["Украина", "Ukrayna", "Ukraine"],
        ["US"] = ["США", "ABD", "United States"],
        ["UZ"] = ["Узбекистан", "Özbekistan", "Uzbekistan"],
    };

    private static string FirstNonEmpty(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
}
