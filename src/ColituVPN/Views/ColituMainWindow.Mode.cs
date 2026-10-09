using System.Windows.Controls;
using v2rayN.Services;

namespace v2rayN.Views;

/// <summary>
/// Simple / Advanced mode. Simple shows connect, location, plan, notices, language, start-up,
/// account, support and updates; Advanced adds the expert settings. Hidden settings keep their
/// values and keep working (the kill switch too); only what is shown changes.
/// </summary>
public partial class ColituMainWindow
{
    /// <summary>Settings page: the expert settings show only in Advanced mode.</summary>
    private void ApplyUiMode()
    {
        if (SettingsAdvanced == null)
        {
            return;
        }
        var advanced = _vpn.AdvancedMode;
        SettingsAdvanced.IsChecked = advanced;
        var shown = advanced ? Visibility.Visible : Visibility.Collapsed;

        // The connection card keeps its title and auto-connect in Simple mode.
        if (SettingsAutoConnect.Parent is FrameworkElement autoConnectRow && autoConnectRow.Parent is Panel connection)
        {
            for (var i = 0; i < connection.Children.Count; i++)
            {
                var child = connection.Children[i];
                if (i == 0 || child == autoConnectRow || child == SettingsNote)
                {
                    continue;
                }
                child.Visibility = !advanced ? Visibility.Collapsed
                    : child == SettingsAdBlockRow && !ColituVpnService.AdBlockAvailable ? Visibility.Collapsed
                    : Visibility.Visible;
            }
        }
        SplitCard.Visibility = shown;
        if (!advanced)
        {
            RotationCard.Visibility = Visibility.Collapsed;
        }
        if (SettingsTray.Parent is FrameworkElement trayRow)
        {
            trayRow.Visibility = shown;
        }
        ApplyHomeMode();
    }

    /// <summary>Home: quick settings in Advanced mode; the "Advanced mode" button (and, when needed, "Advanced settings on") in Simple mode.</summary>
    private void ApplyHomeMode()
    {
        if (QuickSettingsCard == null)
        {
            return;
        }
        var advanced = _vpn.AdvancedMode;
        QuickSettingsCard.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        HomeAdvancedButton.Visibility = advanced ? Visibility.Collapsed : Visibility.Visible;
        AdvancedSettingsOnButton.Visibility = !advanced && _vpn.AdvancedSettingsActive ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetUiMode(bool advanced)
    {
        if (advanced == _vpn.AdvancedMode)
        {
            return;
        }
        _vpn.SetAdvancedMode(advanced);
        if (!advanced)
        {
            _category = "all";
        }
        ApplyPreferencesToUi();
        ApplyStatus();
        BuildCategoryFilter();
        RenderServers();
        if (_vpn.Rotation != null)
        {
            ApplyRotationUi();
        }
        ShowToast(Loc.I[advanced ? "mode.advancedOn" : "mode.simpleOn"]);
    }

    private void Advanced_Click(object sender, RoutedEventArgs e)
    {
        if (_applyingPreferences || !IsLoaded)
        {
            return;
        }
        SetUiMode(SettingsAdvanced.IsChecked == true);
    }

    /// <summary>One tap from Home: Advanced mode on at once, the new items appear.</summary>
    private void HomeAdvanced_Click(object sender, RoutedEventArgs e) => SetUiMode(true);

    /// <summary>"Advanced settings on": opens the settings in Advanced mode.</summary>
    private void AdvancedSettingsOn_Click(object sender, RoutedEventArgs e)
    {
        SetUiMode(true);
        Navigate("settings");
    }
}
