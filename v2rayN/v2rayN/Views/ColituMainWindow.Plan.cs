using System.Windows.Controls;
using System.Windows.Media;
using Path = System.Windows.Shapes.Path;
using v2rayN.Services;

namespace v2rayN.Views;

public partial class ColituMainWindow
{
    private const int MaxDevices = 10;
    private readonly ColituBillingService _billing = ColituBillingService.Instance;
    private ColituCatalog? _catalog;
    private List<ColituPaymentMethod> _methods = [];
    private int _duration;
    private int _devices = 1;
    private string _method = "";
    private ColituQuote? _quote;
    private int _quoteVersion;
    private ColituCheckout? _checkout;
    private DispatcherTimer? _paymentTimer;
    private string _paymentState = "";

    private async Task LoadPricingAsync(bool force = false)
    {
        if (_paymentState == "pending")
        {
            return;
        }
        if (_catalog != null && _methods.Count > 0 && !force)
        {
            RenderPricing();
            await UpdateQuoteAsync();
            return;
        }

        PricingLoading.Visibility = Visibility.Visible;
        PricingError.Visibility = Visibility.Collapsed;
        PricingContent.Visibility = Visibility.Collapsed;
        try
        {
            var catalogTask = _billing.GetCatalogAsync();
            var methodsTask = _billing.GetPaymentMethodsAsync();
            _catalog = await catalogTask;
            _methods = await methodsTask;
            if (_catalog.Plans.Count == 0 || _methods.Count == 0)
            {
                throw new InvalidOperationException("Empty catalog");
            }

            if (_catalog.Plans.All(plan => plan.DurationMonths != _duration))
            {
                _duration = (_catalog.Plans.FirstOrDefault(plan => plan.IsBestValue) ?? _catalog.Plans[^1]).DurationMonths;
            }
            if (_methods.All(method => method.Key != _method))
            {
                _method = _methods[0].Key;
            }
            _devices = Math.Clamp(Math.Max(_devices, _auth.CurrentSubscription?.DeviceLimit ?? 1), 1, MaxDevices);

            PricingLoading.Visibility = Visibility.Collapsed;
            PricingContent.Visibility = Visibility.Visible;
            RenderPricing();
            FadeIn(PricingContent, 12);
            await UpdateQuoteAsync();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.LoadPricingAsync", ex);
            PricingLoading.Visibility = Visibility.Collapsed;
            PricingError.Visibility = Visibility.Visible;
        }
    }

    private void PricingRetry_Click(object sender, RoutedEventArgs e) => _ = LoadPricingAsync(force: true);

    private string DurationLabel(int months) => months switch
    {
        24 => Loc.I["pricing.twoYears"],
        1 => Loc.I["pricing.oneMonth"],
        _ => Loc.I.Format("pricing.months", ("n", months))
    };

    private string Money(long minor)
    {
        var currency = _catalog?.Currency ?? "RUB";
        var amount = minor / 100m;
        var number = amount.ToString(amount % 1 == 0 ? "#,0" : "#,0.00", Loc.I.Culture);
        return currency == "RUB" ? $"{number} ₽" : $"{number} {currency}";
    }

    private void RenderPricing()
    {
        if (_catalog == null || DurationList == null)
        {
            return;
        }

        DurationList.Children.Clear();
        foreach (var plan in _catalog.Plans)
        {
            var option = new RadioButton
            {
                GroupName = "Duration",
                Tag = plan.DurationMonths,
                Style = (Style)FindResource("SegmentOption"),
                IsChecked = plan.DurationMonths == _duration,
                MinHeight = 48
            };
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new TextBlock { Text = DurationLabel(plan.DurationMonths), FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            TextBlock? discount = null;
            if (plan.DiscountPercent > 0)
            {
                discount = new TextBlock { Text = $"−{plan.DiscountPercent}%", FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
                content.Children.Add(discount);
            }
            option.Content = content;
            // Green on the dark track, dark text on the lavender pill of the picked one.
            void Tint() => discount?.SetValue(TextBlock.ForegroundProperty, (Brush)FindResource(option.IsChecked == true ? "OnAccentBrush" : "SuccessBrush"));
            Tint();
            option.Checked += (_, _) => Tint();
            option.Unchecked += (_, _) => Tint();
            // Two-line options are nearly square: a full pill would turn them into circles.
            option.Loaded += (_, _) =>
            {
                if (option.Template?.FindName("Body", option) is Border body)
                {
                    body.CornerRadius = new CornerRadius(14);
                }
            };
            option.Checked += Duration_Checked;
            DurationList.Children.Add(option);
        }

        MethodList.Children.Clear();
        MethodList.Columns = Math.Clamp(_methods.Count, 1, 3);
        foreach (var method in _methods)
        {
            var option = new RadioButton
            {
                GroupName = "Method",
                Tag = method.Key,
                Style = (Style)FindResource("OptionCard"),
                IsChecked = method.Key == _method,
                Margin = new Thickness(0, 0, 8, 8)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var icon = new Grid { Width = 32, Height = 32 };
            icon.Children.Add(new Border { Background = (Brush)FindResource("Glass06Brush"), CornerRadius = new CornerRadius(10) });
            icon.Children.Add(new Path
            {
                Data = Geometry.Parse(MethodIcon(method.Key)),
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                Stroke = (Brush)FindResource("LilacBrush"),
                StrokeThickness = 1.6,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
            grid.Children.Add(icon);
            var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = MethodName(method), FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock
            {
                Text = method.CommissionBps > 0 ? Loc.I.Format("pricing.fee", ("n", (method.CommissionBps / 100m).ToString("0.##", Loc.I.Culture))) : Loc.I["pricing.noFee"],
                FontSize = 12,
                Foreground = (Brush)FindResource("MutedBrush")
            });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            option.Content = grid;
            option.Checked += Method_Checked;
            MethodList.Children.Add(option);
        }

        RenderPlanFocus();
        RenderSummary();
    }

    private string MethodName(ColituPaymentMethod method) => method.Key switch
    {
        "sbp" => Loc.I.Language == "ru" ? "СБП" : "SBP",
        "card" => Loc.I.Language switch { "ru" => "Банковская карта", "tr" => "Banka kartı", _ => "Bank card" },
        "crypto" => Loc.I.Language switch { "ru" => "Криптовалюта", "tr" => "Kripto para", _ => "Crypto" },
        _ => method.DisplayName ?? method.Key
    };

    private static string MethodIcon(string key) => key switch
    {
        "card" => "M3 6h18v12H3z M3 10h18 M7 15h4",
        "crypto" => "M12 3l8 4.5v9L12 21l-8-4.5v-9z M12 12l8-4.5 M12 12v9 M12 12L4 7.5",
        _ => "M13 2L4.5 13.5H12L11 22l8.5-11.5H12z"
    };

    private ColituCatalogPlan? ChosenPlan => _catalog?.Plans.FirstOrDefault(plan => plan.DurationMonths == _duration);

    private void RenderPlanFocus()
    {
        if (ChosenPlan is not { } plan)
        {
            return;
        }
        FocusDuration.Text = DurationLabel(plan.DurationMonths);
        FocusOff.Visibility = plan.DiscountPercent > 0 ? Visibility.Visible : Visibility.Collapsed;
        FocusOffText.Text = Loc.I.Format("pricing.off", ("n", plan.DiscountPercent));
        FocusBest.Visibility = plan.IsBestValue ? Visibility.Visible : Visibility.Collapsed;
        FocusPrice.Text = Money(plan.EffectiveMonthlyMinor);
        FocusSaving.Text = plan.SavingPerDeviceMinor > 0 ? Loc.I.Format("pricing.saving", ("amount", Money(plan.SavingPerDeviceMinor))) : "";
        FocusSaving.Visibility = plan.SavingPerDeviceMinor > 0 ? Visibility.Visible : Visibility.Collapsed;
        DeviceCountText.Text = _devices.ToString(Loc.I.Culture);
        DeviceMinus.IsEnabled = _devices > 1;
        DevicePlus.IsEnabled = _devices < MaxDevices;
    }

    private void RenderSummary()
    {
        if (ChosenPlan is not { } plan)
        {
            return;
        }
        SummaryTitle.Text = $"{DurationLabel(plan.DurationMonths)} × {Loc.I.Count("device", _devices)}";
        var quote = _quote is { } q && q.DurationMonths == _duration && q.DeviceCount == _devices && q.Commission?.Method == _method ? q : null;
        var regular = quote?.RegularPriceMinor ?? plan.RegularPricePerDeviceMinor * _devices;
        var package = quote?.PackagePriceMinor ?? plan.PricePerDeviceMinor * _devices;
        var discount = quote?.DiscountMinor ?? regular - package;
        var fee = quote?.Commission?.AmountMinor;
        SummaryRegular.Text = Money(regular);
        SummaryDiscountRow.Visibility = discount > 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryDiscountLabel.Text = Loc.I.Format("pricing.discount", ("n", quote?.DiscountPercent ?? plan.DiscountPercent));
        SummaryDiscount.Text = "−" + Money(discount);
        var method = _methods.FirstOrDefault(m => m.Key == _method);
        SummaryFeeLabel.Text = method is { CommissionBps: > 0 }
            ? Loc.I.Format("pricing.fee", ("n", (method.CommissionBps / 100m).ToString("0.##", Loc.I.Culture)))
            : Loc.I["pricing.commission"];
        SummaryFee.Text = fee is { } amount ? Money(amount) : method is { CommissionBps: 0 } ? Money(0) : "…";
        SummaryTotal.Text = quote != null ? Money(quote.CustomerTotalMinor) : "…";
        PayButton.IsEnabled = quote != null;
    }

    private async Task UpdateQuoteAsync()
    {
        if (ChosenPlan == null || _method.Length == 0)
        {
            return;
        }
        var version = ++_quoteVersion;
        RenderSummary();
        try
        {
            var quote = await _billing.QuoteAsync(_duration, _devices, _method);
            if (version == _quoteVersion)
            {
                _quote = quote;
                RenderSummary();
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.UpdateQuoteAsync", ex);
            if (version == _quoteVersion)
            {
                SummaryTotal.Text = "—";
            }
        }
    }

    private async void Duration_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: int months } && months != _duration)
        {
            _duration = months;
            RenderPlanFocus();
            FadeIn(PlanFocus, 6);
            await UpdateQuoteAsync();
        }
    }

    private async void Method_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string key } && key != _method)
        {
            _method = key;
            await UpdateQuoteAsync();
        }
    }

    private async void DeviceMinus_Click(object sender, RoutedEventArgs e) => await ChangeDevicesAsync(-1);

    private async void DevicePlus_Click(object sender, RoutedEventArgs e) => await ChangeDevicesAsync(1);

    private async Task ChangeDevicesAsync(int delta)
    {
        var next = Math.Clamp(_devices + delta, 1, MaxDevices);
        if (next == _devices)
        {
            return;
        }
        _devices = next;
        RenderPlanFocus();
        await UpdateQuoteAsync();
    }

    // ── Checkout ───────────────────────────────────────────────────────────
    private async void Pay_Click(object sender, RoutedEventArgs e)
    {
        PayButton.IsEnabled = false;
        PaySpinner.Visibility = Visibility.Visible;
        try
        {
            _checkout = await _billing.CheckoutAsync(_duration, _devices, _method);
            if (_checkout?.RedirectUrl is not { Length: > 0 } url || _checkout.OrderId is not { Length: > 0 })
            {
                throw new InvalidOperationException("Checkout response was incomplete.");
            }
            OpenUrl(url);
            ShowPaymentState("pending");
            StartPaymentPolling();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.Pay_Click", ex);
            ShowToast(ex is ColituApiException { StatusCode: < System.Net.HttpStatusCode.InternalServerError } api && api.ErrorCode != null ? api.Message : Loc.I["err.payment"], true);
        }
        finally
        {
            PaySpinner.Visibility = Visibility.Collapsed;
            PayButton.IsEnabled = _quote != null;
        }
    }

    private void StartPaymentPolling()
    {
        StopPaymentPolling();
        _paymentTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _paymentTimer.Tick += async (_, _) => await PollPaymentAsync();
        _paymentTimer.Start();
    }

    private void StopPaymentPolling()
    {
        _paymentTimer?.Stop();
        _paymentTimer = null;
    }

    private async Task PollPaymentAsync()
    {
        if (_checkout?.OrderId is not { } orderId)
        {
            StopPaymentPolling();
            return;
        }
        try
        {
            var order = await _billing.GetOrderAsync(orderId);
            var status = order?.Status ?? "pending";
            if (status == "succeeded")
            {
                StopPaymentPolling();
                await _auth.RefreshAccountAsync();
                await RefreshDataAsync(includeAccount: false);
                ShowPaymentState("succeeded");
                ShowToast(Loc.I["pay.success"]);
            }
            else if (ColituBillingService.IsSettled(status))
            {
                StopPaymentPolling();
                ShowPaymentState("failed");
            }
        }
        catch (Exception ex)
        {
            // Keep polling through brief network trouble.
            Logging.SaveLog("ColituMainWindow.PollPaymentAsync", ex);
        }
    }

    private void ShowPaymentState(string state)
    {
        _paymentState = state;
        var pending = state == "pending";
        SummaryPanel.Visibility = state == "" ? Visibility.Visible : Visibility.Collapsed;
        PaymentPanel.Visibility = state == "" ? Visibility.Collapsed : Visibility.Visible;
        DurationList.IsEnabled = MethodList.IsEnabled = DeviceMinus.IsEnabled = DevicePlus.IsEnabled = !pending;
        if (state == "")
        {
            RenderPlanFocus();
            return;
        }

        PaymentSpinner.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        PaymentResultIcon.Visibility = pending ? Visibility.Collapsed : Visibility.Visible;
        var success = state == "succeeded";
        PaymentResultIcon.Data = Geometry.Parse(success ? "M5 12.5l4.5 4.5L19 7.5" : "M6 6l12 12 M18 6L6 18");
        PaymentResultIcon.Stroke = (Brush)FindResource(success ? "SuccessBrush" : "DangerBrush");
        PaymentTitle.Text = Loc.I[pending ? "pay.checking" : success ? "pay.success" : "pay.failed"];
        PaymentHint.Text = Loc.I[pending ? "pay.checkingHint" : success ? "pay.successHint" : "pay.failedHint"];
        PaymentPrimaryText.Text = Loc.I[pending ? "pay.reopen" : success ? "pay.connect" : "pay.back"];
        PaymentSecondaryText.Text = Loc.I[pending ? "pay.cancel" : "pay.back"];
        PaymentSecondary.Visibility = success ? Visibility.Collapsed : Visibility.Visible;
        if (!pending && !success)
        {
            PaymentSecondary.Visibility = Visibility.Collapsed;
        }
        FadeIn(PaymentPanel, 8);
    }

    private async void PaymentPrimary_Click(object sender, RoutedEventArgs e)
    {
        switch (_paymentState)
        {
            case "pending":
                if (_checkout?.RedirectUrl is { } url) OpenUrl(url);
                break;
            case "succeeded":
                ShowPaymentState("");
                Navigate("home");
                if (_vpn.Status != ColituVpnStatus.Connected)
                {
                    await ToggleConnectionAsync();
                }
                break;
            default:
                ShowPaymentState("");
                break;
        }
    }

    private void PaymentSecondary_Click(object sender, RoutedEventArgs e)
    {
        StopPaymentPolling();
        _checkout = null;
        ShowPaymentState("");
    }
}
