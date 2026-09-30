using System.Text.Json.Serialization;

namespace v2rayN.Services;

/// <summary>
/// Plan purchase against the panel billing API, the same flow as
/// colitu.com/pricing: catalog → quote → checkout (provider page opens in the
/// browser) → order status polling until the payment settles.
/// </summary>
public sealed class ColituBillingService
{
    public static ColituBillingService Instance { get; } = new();

    private ColituBillingService() { }

    public async Task<ColituCatalog> GetCatalogAsync()
    {
        var envelope = await ColituAuthService.Instance.GetAuthorizedJsonAsync<ColituEnvelope<ColituCatalog>>("/billing/catalog");
        var catalog = envelope?.Data ?? new ColituCatalog();
        catalog.Plans = catalog.Plans
            .Where(plan => plan.Enabled && plan.DurationMonths > 0)
            .OrderBy(plan => plan.DurationMonths)
            .ToList();
        return catalog;
    }

    public async Task<List<ColituPaymentMethod>> GetPaymentMethodsAsync()
    {
        var envelope = await ColituAuthService.Instance.GetAuthorizedJsonAsync<ColituEnvelope<List<ColituPaymentMethod>>>("/billing/payment-methods");
        return (envelope?.Data ?? [])
            .Where(method => method.Enabled && method.SupportsOneTime)
            .OrderBy(method => method.SortOrder)
            .ToList();
    }

    public async Task<ColituQuote?> QuoteAsync(int durationMonths, int deviceCount, string method)
    {
        var envelope = await ColituAuthService.Instance.PostAuthorizedJsonAsync<ColituEnvelope<ColituQuote>>("/billing/quote", new
        {
            duration_months = durationMonths,
            device_count = deviceCount,
            payment_method = method
        });
        return envelope?.Data;
    }

    public async Task<ColituCheckout?> CheckoutAsync(int durationMonths, int deviceCount, string method)
    {
        var envelope = await ColituAuthService.Instance.PostAuthorizedJsonAsync<ColituEnvelope<ColituCheckout>>("/billing/checkout", new
        {
            duration_months = durationMonths,
            device_count = deviceCount,
            payment_method = method
        });
        return envelope?.Data;
    }

    public async Task<ColituOrder?> GetOrderAsync(string orderId)
    {
        var envelope = await ColituAuthService.Instance.GetAuthorizedJsonAsync<ColituEnvelope<ColituOrder>>($"/billing/orders/{Uri.EscapeDataString(orderId)}");
        return envelope?.Data;
    }

    public static bool IsSettled(string? status) => status is "succeeded" or "failed" or "canceled" or "cancelled" or "expired" or "refunded" or "chargeback";
}

public sealed class ColituEnvelope<T>
{
    [JsonPropertyName("data")] public T? Data { get; set; }
}

public sealed class ColituCatalog
{
    [JsonPropertyName("currency")] public string Currency { get; set; } = "RUB";
    [JsonPropertyName("base_monthly_device_price_minor")] public long BaseMonthlyDevicePriceMinor { get; set; }
    [JsonPropertyName("plans")] public List<ColituCatalogPlan> Plans { get; set; } = [];
}

public sealed class ColituCatalogPlan
{
    [JsonPropertyName("duration_months")] public int DurationMonths { get; set; }
    [JsonPropertyName("price_per_device_minor")] public long PricePerDeviceMinor { get; set; }
    [JsonPropertyName("regular_price_per_device_minor")] public long RegularPricePerDeviceMinor { get; set; }
    [JsonPropertyName("discount_percent")] public int DiscountPercent { get; set; }
    [JsonPropertyName("effective_monthly_minor")] public long EffectiveMonthlyMinor { get; set; }
    [JsonPropertyName("saving_per_device_minor")] public long SavingPerDeviceMinor { get; set; }
    [JsonPropertyName("badge")] public string? Badge { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("sort_order")] public int SortOrder { get; set; }

    public bool IsBestValue => string.Equals(Badge, "BEST_VALUE", StringComparison.OrdinalIgnoreCase);
}

public sealed class ColituPaymentMethod
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    [JsonPropertyName("commission_bps")] public int CommissionBps { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("supports_one_time")] public bool SupportsOneTime { get; set; }
    [JsonPropertyName("sort_order")] public int SortOrder { get; set; }
}

public sealed class ColituQuote
{
    [JsonPropertyName("currency")] public string Currency { get; set; } = "RUB";
    [JsonPropertyName("duration_months")] public int DurationMonths { get; set; }
    [JsonPropertyName("device_count")] public int DeviceCount { get; set; }
    [JsonPropertyName("regular_price_minor")] public long RegularPriceMinor { get; set; }
    [JsonPropertyName("discount_percent")] public int DiscountPercent { get; set; }
    [JsonPropertyName("discount_minor")] public long DiscountMinor { get; set; }
    [JsonPropertyName("package_price_minor")] public long PackagePriceMinor { get; set; }
    [JsonPropertyName("commission")] public ColituQuoteCommission? Commission { get; set; }
    [JsonPropertyName("customer_total_minor")] public long CustomerTotalMinor { get; set; }
}

public sealed class ColituQuoteCommission
{
    [JsonPropertyName("method")] public string? Method { get; set; }
    [JsonPropertyName("rate_bps")] public int RateBps { get; set; }
    [JsonPropertyName("amount_minor")] public long AmountMinor { get; set; }
}

public sealed class ColituCheckout
{
    [JsonPropertyName("order_id")] public string? OrderId { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("redirect_url")] public string? RedirectUrl { get; set; }
    [JsonPropertyName("expires_at")] public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed class ColituOrder
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("expires_at")] public DateTimeOffset? ExpiresAt { get; set; }
}
