using v2rayN.Services;

namespace v2rayN.Views;

/// <summary>
/// Plan page, as on iOS and Android. Nothing is bought in the app: the
/// subscription is managed in the customer account on app.colitu.com. The page
/// shows the current plan, opens the account and refreshes the status after the
/// user changed something there.
/// </summary>
public partial class ColituMainWindow
{
    private const string AccountPortalUrl = "https://app.colitu.com";
    private bool _refreshingPlan;

    private void ManagePlan_Click(object sender, RoutedEventArgs e) => OpenUrl(AccountPortalUrl);

    private async void PlanRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshingPlan)
        {
            return;
        }
        _refreshingPlan = true;
        PlanRefreshButton.IsEnabled = false;
        PlanRefreshSpinner.Visibility = Visibility.Visible;
        try
        {
            await RefreshDataAsync();
            ShowToast(Loc.I["plan.refreshed"]);
        }
        finally
        {
            PlanRefreshSpinner.Visibility = Visibility.Collapsed;
            PlanRefreshButton.IsEnabled = true;
            _refreshingPlan = false;
        }
    }
}
