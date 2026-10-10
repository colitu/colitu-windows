using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace v2rayN.Services;

/// <summary>
/// The release switch of Adaptive Connect 3.0. While <see cref="Enabled"/> is false the hinted
/// start (network hints <c>preferred</c>) and the whole recovery set (<c>GET /client/recovery</c>,
/// its store, its use in a connect) are off: nothing is fetched, stored or used, and a connect
/// behaves like the pre-3.0 code. One constant, flipped when the feature ships; the decision
/// functions take the flag as a parameter so that both values stay tested.
/// </summary>
internal static class ColituAdaptiveConnect3
{
    /// <summary>Hinted start and recovery set. Off for this release.</summary>
    public const bool Enabled = false;

    /// <summary>The hinted start may decide the transport order (not when the stall marks are ignored this round).</summary>
    internal static bool HintedStartActive(bool marksIgnored) => HintedStartActive(Enabled, marksIgnored);

    internal static bool HintedStartActive(bool enabled, bool marksIgnored) => enabled && !marksIgnored;

    /// <summary>The recovery set may be fetched and stored.</summary>
    internal static bool RecoveryFetchAllowed() => RecoveryFetchAllowed(Enabled);

    internal static bool RecoveryFetchAllowed(bool enabled) => enabled;

    /// <summary>The recovery set takes over a connect (see <see cref="ColituRecoverySet.ShouldUse"/>).</summary>
    internal static bool ShouldUseRecovery(bool automatic, bool apiUnreachableOnEveryBase, bool cacheUsable) =>
        ShouldUseRecovery(Enabled, automatic, apiUnreachableOnEveryBase, cacheUsable);

    internal static bool ShouldUseRecovery(bool enabled, bool automatic, bool apiUnreachableOnEveryBase, bool cacheUsable) =>
        enabled && ColituRecoverySet.ShouldUse(automatic, apiUnreachableOnEveryBase, cacheUsable);
}

/// <summary>
/// Adaptive Connect 3.0, network hints v2: <c>preferred</c> are the protocols that worked for most
/// devices on this access network, best first. Pure logic, no I/O (spec: panel repo
/// docs/adaptive-connect-3-apps.md, section 1; Android reference ColituNetworkHintsPolicy.hintedStart).
/// </summary>
public static class ColituNetworkHintsPolicy
{
    /// <summary>The panel sends a handful of protocols; more than this is noise.</summary>
    public const int MaxProtocols = 8;

    /// <summary>Lowercase, trimmed, no empties, no duplicates (in the panel's order).</summary>
    public static List<string> ParseProtocols(IEnumerable<string?>? raw) =>
        (raw ?? [])
            .Where(protocol => !string.IsNullOrWhiteSpace(protocol))
            .Select(protocol => protocol!.Trim().ToLowerInvariant())
            .Distinct()
            .Take(MaxProtocols)
            .ToList();

    /// <summary><c>preferred</c> like <c>blocked</c>, without any protocol that is also blocked.</summary>
    public static List<string> ParsePreferred(IEnumerable<string?>? preferred, IEnumerable<string?>? blocked)
    {
        var blockedSet = ParseProtocols(blocked).ToHashSet(StringComparer.Ordinal);
        return ParseProtocols(preferred).Where(protocol => !blockedSet.Contains(protocol)).ToList();
    }

    /// <summary>
    /// Start order when this device has no memory of the network (no last good transport for the
    /// server): the preferred protocols that the server offers and that did not stall go first, in
    /// the hint's order, then the rest in <paramref name="rank"/> order. Null: no usable hint, the
    /// usual order (with its probe) decides. The device's own memory always beats the hint.
    /// </summary>
    public static List<T>? HintedStart<T>(IReadOnlyList<T> configs, Func<T, string?> protocolOf, IReadOnlyList<string> preferred,
        Func<string, bool> stalled, string? lastGood, Func<IReadOnlyList<T>, IEnumerable<T>> rank)
    {
        if (!string.IsNullOrEmpty(lastGood) || preferred.Count == 0)
        {
            return null;
        }
        var first = new List<T>();
        foreach (var protocol in preferred)
        {
            if (stalled(protocol))
            {
                continue;
            }
            var match = configs.FirstOrDefault(config => string.Equals(protocolOf(config), protocol, StringComparison.OrdinalIgnoreCase));
            if (match != null && !first.Contains(match))
            {
                first.Add(match);
            }
        }
        if (first.Count == 0)
        {
            return null;
        }
        var rest = configs.Where(config => !first.Contains(config)).ToList();
        return [.. first, .. rank(rest)];
    }
}

/// <summary>
/// Adaptive Connect 3.0, recovery set: <c>GET /client/recovery</c>, up to four config envelopes (one
/// per country first) with <c>recovery_until</c> about 14 days ahead. It is the last resort of an
/// automatic connect when the panel cannot be reached on any API base and the cached settings do not
/// connect. Pure logic here; the store and the connect path are in <see cref="ColituVpnService"/>.
/// </summary>
internal sealed class ColituRecoverySet
{
    public const int MaxServers = 4;

    /// <summary>A stored set older than this is refreshed.</summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(24);

    /// <summary>At most one fetch attempt in this time, successful or not.</summary>
    public static readonly TimeSpan AttemptEvery = TimeSpan.FromHours(6);

    public DateTimeOffset GeneratedAt { get; private init; }
    public DateTimeOffset RecoveryUntil { get; private init; }
    /// <summary>Servers in the panel's order (one per country first); every envelope has a server id.</summary>
    public IReadOnlyList<ColituConfigEnvelopeDto> Configs { get; private init; } = [];

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The set, or null when the JSON is not one: no valid <c>recovery_until</c>, no usable server.</summary>
    public static ColituRecoverySet? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            var dto = JsonSerializer.Deserialize<ColituRecoveryDto>(json, Options);
            if (dto?.RecoveryUntil is not { } until)
            {
                return null;
            }
            var configs = new List<ColituConfigEnvelopeDto>();
            foreach (var envelope in dto.Configs ?? [])
            {
                if (envelope?.Profile == null || string.IsNullOrWhiteSpace(envelope.Server?.Id)
                    || configs.Any(item => SameServer(item.Server?.Id, envelope.Server.Id)))
                {
                    continue;
                }
                configs.Add(envelope);
                if (configs.Count >= MaxServers)
                {
                    break;
                }
            }
            return configs.Count == 0
                ? null
                : new ColituRecoverySet { GeneratedAt = dto.GeneratedAt ?? DateTimeOffset.MinValue, RecoveryUntil = until, Configs = configs };
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Past <c>recovery_until</c>: not used, and deleted.</summary>
    public bool IsExpired(DateTimeOffset now) => RecoveryUntil <= now;

    /// <summary>
    /// The recovery set takes over only in automatic server mode, when every API base failed at the
    /// network level, and the regular cached settings cannot connect (none, past their offline
    /// grace, or every transport failed).
    /// </summary>
    public static bool ShouldUse(bool automatic, bool apiUnreachableOnEveryBase, bool cacheUsable) =>
        automatic && apiUnreachableOnEveryBase && !cacheUsable;

    /// <summary>The first server in the set's order that did not fail in this connect; null when none is left.</summary>
    public ColituConfigEnvelopeDto? NextServer(IEnumerable<string> failed)
    {
        var skip = failed.Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        return Configs.FirstOrDefault(item => !skip.Any(id => SameServer(id, item.Server?.Id)));
    }

    /// <summary>
    /// A fetch is due when there is no stored set (<paramref name="storedGeneratedAt"/> null) or it
    /// is 24 h old, and the last attempt is 6 h or more ago.
    /// </summary>
    public static bool RefreshDue(DateTimeOffset? storedGeneratedAt, DateTimeOffset? lastAttempt, DateTimeOffset now)
    {
        if (lastAttempt is { } attempt && now - attempt < AttemptEvery && attempt <= now + TimeSpan.FromMinutes(5))
        {
            return false;
        }
        return storedGeneratedAt is not { } generated || now - generated >= RefreshAfter || generated > now + TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// 401/403 (signed out, device revoked, plan ended) delete the set; network errors and 5xx keep
    /// it. A synthetic 401 REFRESH_FAILED is a refresh that could not reach the panel: keep.
    /// </summary>
    public static bool DeletesSetOnFetchFailure(HttpStatusCode status, string? errorCode) =>
        status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && errorCode != "REFRESH_FAILED";

    /// <summary>
    /// A failure of the API itself at the network level (DNS, refused, timeout, TLS, reset) after
    /// every base was tried, as opposed to an answer of the panel (also 4xx/5xx). Call it only when
    /// the caller's own cancellation is not the reason.
    /// </summary>
    public static bool IsApiUnreachable(Exception ex) => ex switch
    {
        ColituApiException => false,
        OperationCanceledException => true,
        _ => ColituEndpointList.ShouldFailover(ex, isGet: true) || (ex.InnerException != null && IsApiUnreachable(ex.InnerException))
    };

    private static bool SameServer(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}

internal sealed class ColituRecoveryDto
{
    [JsonPropertyName("generated_at")] public DateTimeOffset? GeneratedAt { get; set; }
    [JsonPropertyName("recovery_until")] public DateTimeOffset? RecoveryUntil { get; set; }
    [JsonPropertyName("configs")] public List<ColituConfigEnvelopeDto?>? Configs { get; set; }
}
