using System.Windows.Controls;
using System.Windows.Media;
using v2rayN.Services;

namespace v2rayN.Views;

public partial class ColituMainWindow
{
    private bool _renderingServers;
    private bool _buildingCategories;
    private string _category = "all";

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
        var filtered = _category != "all";
        var connectedId = _vpn.Status == ColituVpnStatus.Connected ? _vpn.ConnectedServer?.Id : null;
        var rows = new List<ColituServerRow>();
        var auto = ColituServerRow.Auto();
        if (query.Length == 0 && !filtered)
        {
            rows.Add(auto);
        }

        foreach (var server in _servers
                     .OrderBy(s => ColituServerRow.CountryName(s.CountryCode) ?? s.Country ?? "", StringComparer.Create(Loc.I.Culture, true))
                     .ThenBy(s => s.City ?? s.Name, StringComparer.Create(Loc.I.Culture, true)))
        {
            if (filtered && !server.Categories.Contains(_category))
            {
                continue;
            }
            var row = ColituServerRow.From(server);
            if (query.Length > 0 && !row.Matches(query))
            {
                continue;
            }
            row.MarkState(server.Id == connectedId, server.Id == _vpn.SavedServerId && !_vpn.IsAutoSelection);
            rows.Add(row);
        }

        auto.MarkState(_vpn.IsAutoSelection && _vpn.Status == ColituVpnStatus.Connected, _vpn.IsAutoSelection);

        _renderingServers = true;
        try
        {
            ServerList.ItemsSource = rows;
            ServerList.SelectedItem = rows.FirstOrDefault(row => row.IsAuto ? _vpn.IsAutoSelection : !_vpn.IsAutoSelection && row.Server?.Id == _vpn.SavedServerId);
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
        ServerListEmpty.Visibility = rows.Count(row => !row.IsAuto) == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ServerSearch_TextChanged(object sender, TextChangedEventArgs e) => RenderServers();

    private async void ServerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_renderingServers || ServerList.SelectedItem is not ColituServerRow row)
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
            ShowToast(Loc.I.Format("locations.switched", ("server", ServerLabel(_vpn.ConnectedServer) ?? row.Title)));
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
    private static readonly Brush Lit = new SolidColorBrush(Color.FromRgb(0xC4, 0xB5, 0xFD));
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
    public Visibility AutoVisibility => IsAuto ? Visibility.Visible : Visibility.Collapsed;
    public int LoadLevel { get; init; }
    public Visibility LoadVisibility => LoadLevel > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string LoadText => Loc.I[LoadLevel switch { 1 => "server.load.low", 2 => "server.load.medium", _ => "server.load.high" }];
    public Brush LoadBar1 => LoadLevel >= 1 ? Lit : Unlit;
    public Brush LoadBar2 => LoadLevel >= 2 ? Lit : Unlit;
    public Brush LoadBar3 => LoadLevel >= 3 ? Lit : Unlit;
    public bool IsSelectable => IsAuto || Server?.Available == true;
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

    public bool Matches(string query)
    {
        return new[] { Title, Subtitle, Server?.Country, Server?.CountryCode, Server?.City }
            .Any(value => value?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true);
    }

    public static ColituServerRow Auto() => new()
    {
        IsAuto = true,
        Title = Loc.I["server.auto"],
        Subtitle = Loc.I["server.autoHint"]
    };

    public static ColituServerRow From(ColituVpnServer server)
    {
        var country = CountryName(server.CountryCode) ?? server.Country;
        var name = FirstNonEmpty(server.DisplayName, server.Name, country, "Colitu");
        var details = new[] { country, server.City }
            .Where(value => !string.IsNullOrWhiteSpace(value) && !string.Equals(value, name, StringComparison.CurrentCultureIgnoreCase))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Concat(server.Categories.Count == 0 ? [] : [string.Join(", ", server.Categories.Select(category => Loc.Has("cat." + category) ? Loc.I.Get("cat." + category) : category))]);
        return new ColituServerRow
        {
            Server = server,
            Title = name,
            Subtitle = string.Join(" · ", details),
            FlagUri = server.HasLocalFlag ? server.FlagResourceUri : null,
            LoadLevel = server.Load switch
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
