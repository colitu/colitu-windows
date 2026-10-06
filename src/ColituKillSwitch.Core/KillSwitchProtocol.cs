using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Colitu.KillSwitch;

/// <summary>
/// Wire contract between Colitu VPN (the elevated app) and the Colitu kill-switch service.
/// This file is compiled into both executables; any change must keep <see cref="KsProtocol.Version"/>
/// in step on both sides.
///
/// Framing: one request and one response per pipe connection, each a 4-byte little-endian length
/// followed by that many bytes of UTF-8 JSON (at most <see cref="KsProtocol.MaxMessageBytes"/>).
/// Unknown JSON members are rejected, never ignored.
/// </summary>
public static class KsProtocol
{
    public const int Version = 1;
    public const string PipeName = "Colitu.KillSwitch.v1";
    public const string ServiceName = "ColituKillSwitch";
    public const string ServiceExeName = "ColituKillSwitchService.exe";
    public const string AppExeName = "ColituVPN.exe";

    public const int MaxMessageBytes = 16 * 1024;
    public const int MaxEndpoints = 64;
    public const int MaxApps = 64;
    public const int MaxNetworks = 256;
    public const int MaxReasonLength = 120;
    public const int MaxIdLength = 64;
    public const int MaxPathLength = 260;

    public const string CmdArm = "arm";
    public const string CmdDisarm = "disarm";
    public const string CmdStatus = "status";

    /// <summary>The cores may reach anything (direct routing is on: privacy mode off, split tunneling).</summary>
    public const string CoreAccessFull = "full";
    /// <summary>The cores may only reach the listed server endpoints.</summary>
    public const string CoreAccessEndpoints = "endpoints";

    public const string SplitOff = "off";
    /// <summary>The listed apps and networks bypass the VPN: the kill switch lets them out directly.</summary>
    public const string SplitBypass = "bypass";
    /// <summary>Only the listed apps and networks use the VPN: only they are blocked outside it.</summary>
    public const string SplitOnly = "only";

    public const string ErrBadRequest = "bad_request";
    public const string ErrUnsupportedVersion = "unsupported_version";
    public const string ErrUnauthorized = "unauthorized_client";
    public const string ErrFirewall = "firewall_error";
    public const string ErrInternal = "internal_error";
}

public sealed class KsRequest
{
    public int V { get; set; }
    public string? Id { get; set; }
    public string? Cmd { get; set; }
    public KsArm? Arm { get; set; }
    public string? Reason { get; set; }
    /// <summary>Disarm only: also remove the provider and sublayer (uninstall).</summary>
    public bool Purge { get; set; }
}

public sealed class KsArm
{
    public bool AllowLan { get; set; } = true;
    public string? CoreAccess { get; set; }
    public List<KsEndpoint>? Endpoints { get; set; }
    public string? SplitMode { get; set; }
    /// <summary>Full paths of the split-tunnel apps (bypass: let out; only: blocked outside the tunnel).</summary>
    public List<string>? Apps { get; set; }
    /// <summary>Split-tunnel networks as address/prefix (bypass: let out; only: blocked outside the tunnel).</summary>
    public List<string>? Networks { get; set; }
    /// <summary>Stay armed across a reboot (the app starts at sign-in and reconnects by itself).</summary>
    public bool KeepAfterReboot { get; set; }
}

public sealed class KsEndpoint
{
    public string? Ip { get; set; }
    public int Port { get; set; }
    /// <summary>"tcp", "udp" or "any".</summary>
    public string? Proto { get; set; }
}

public sealed class KsResponse
{
    public int V { get; set; } = KsProtocol.Version;
    public string? Id { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public KsStatus? Status { get; set; }
}

public sealed class KsStatus
{
    public bool Armed { get; set; }
    public DateTimeOffset? ArmedAt { get; set; }
    public string? Reason { get; set; }
    public int? OwnerPid { get; set; }
    /// <summary>The app process that armed the switch is still running.</summary>
    public bool OwnerAlive { get; set; }
    public bool KeepAfterReboot { get; set; }
    /// <summary>Armed before the current Windows boot (a crash or power loss while connected).</summary>
    public bool ArmedBeforeBoot { get; set; }
    public string? SplitMode { get; set; }
    public int Filters { get; set; }
    public string? LastDisarmReason { get; set; }
    public DateTimeOffset? LastDisarmAt { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    MaxDepth = 8)]
[JsonSerializable(typeof(KsRequest))]
[JsonSerializable(typeof(KsResponse))]
internal sealed partial class KsJsonContext : JsonSerializerContext;

/// <summary>A request that passed <see cref="KsValidator"/>: every value parsed and in range.</summary>
public sealed record KsValidRequest(string Cmd, string? Id, string Reason, KsValidArm? Arm, bool Purge);

public sealed record KsValidEndpoint(IPAddress Address, ushort Port, KsProto Proto);

public sealed record KsNetwork(IPAddress Address, int Prefix)
{
    public bool IsV6 => Address.AddressFamily == AddressFamily.InterNetworkV6;
    public override string ToString() => $"{Address}/{Prefix}";
}

public enum KsProto
{
    Any,
    Tcp,
    Udp
}

public enum KsSplitMode
{
    Off,
    Bypass,
    Only
}

public sealed record KsValidArm(
    bool AllowLan,
    bool CoreFullAccess,
    IReadOnlyList<KsValidEndpoint> Endpoints,
    KsSplitMode SplitMode,
    IReadOnlyList<string> Apps,
    IReadOnlyList<KsNetwork> Networks,
    bool KeepAfterReboot);

public static class KsValidator
{
    /// <summary>
    /// Checks every field of a request. Only IP literals, ports, fixed keywords, plain local exe
    /// paths and short printable reasons get through; anything else rejects the whole request.
    /// </summary>
    public static bool TryValidate(KsRequest? request, out KsValidRequest? valid, out string error)
    {
        valid = null;
        if (request == null)
        {
            error = "empty request";
            return false;
        }
        if (request.V != KsProtocol.Version)
        {
            error = KsProtocol.ErrUnsupportedVersion;
            return false;
        }
        if (request.Id != null && (request.Id.Length > KsProtocol.MaxIdLength || !request.Id.All(IsIdChar)))
        {
            error = "invalid id";
            return false;
        }
        var reason = request.Reason ?? "";
        if (reason.Length > KsProtocol.MaxReasonLength || reason.Any(ch => char.IsControl(ch)))
        {
            error = "invalid reason";
            return false;
        }

        switch (request.Cmd)
        {
            case KsProtocol.CmdStatus:
                if (request.Arm != null || request.Purge)
                {
                    error = "status takes no arguments";
                    return false;
                }
                valid = new KsValidRequest(KsProtocol.CmdStatus, request.Id, reason, null, false);
                error = "";
                return true;
            case KsProtocol.CmdDisarm:
                if (request.Arm != null)
                {
                    error = "disarm takes no allow list";
                    return false;
                }
                valid = new KsValidRequest(KsProtocol.CmdDisarm, request.Id, reason, null, request.Purge);
                error = "";
                return true;
            case KsProtocol.CmdArm:
                if (request.Purge)
                {
                    error = "arm cannot purge";
                    return false;
                }
                if (!TryValidateArm(request.Arm, out var arm, out error))
                {
                    return false;
                }
                valid = new KsValidRequest(KsProtocol.CmdArm, request.Id, reason, arm, false);
                return true;
            default:
                error = "unknown command";
                return false;
        }
    }

    public static bool TryValidateArm(KsArm? arm, out KsValidArm? valid, out string error)
    {
        valid = null;
        if (arm == null)
        {
            error = "arm needs an allow list";
            return false;
        }

        bool coreFull;
        switch (arm.CoreAccess)
        {
            case KsProtocol.CoreAccessFull:
                coreFull = true;
                break;
            case KsProtocol.CoreAccessEndpoints:
                coreFull = false;
                break;
            default:
                error = "invalid coreAccess";
                return false;
        }

        var endpoints = new List<KsValidEndpoint>();
        var rawEndpoints = arm.Endpoints ?? [];
        if (rawEndpoints.Count > KsProtocol.MaxEndpoints)
        {
            error = "too many endpoints";
            return false;
        }
        foreach (var endpoint in rawEndpoints)
        {
            if (endpoint == null || !TryParseHostAddress(endpoint.Ip, out var address) || endpoint.Port is < 1 or > 65535)
            {
                error = "invalid endpoint";
                return false;
            }
            KsProto proto;
            switch (endpoint.Proto)
            {
                case "tcp":
                    proto = KsProto.Tcp;
                    break;
                case "udp":
                    proto = KsProto.Udp;
                    break;
                case "any":
                    proto = KsProto.Any;
                    break;
                default:
                    error = "invalid endpoint protocol";
                    return false;
            }
            var item = new KsValidEndpoint(address, (ushort)endpoint.Port, proto);
            if (!endpoints.Contains(item))
            {
                endpoints.Add(item);
            }
        }

        KsSplitMode split;
        switch (arm.SplitMode ?? KsProtocol.SplitOff)
        {
            case KsProtocol.SplitOff:
                split = KsSplitMode.Off;
                break;
            case KsProtocol.SplitBypass:
                split = KsSplitMode.Bypass;
                break;
            case KsProtocol.SplitOnly:
                split = KsSplitMode.Only;
                break;
            default:
                error = "invalid splitMode";
                return false;
        }

        var rawApps = arm.Apps ?? [];
        var rawNetworks = arm.Networks ?? [];
        if (rawApps.Count > KsProtocol.MaxApps || rawNetworks.Count > KsProtocol.MaxNetworks)
        {
            error = "too many split-tunnel entries";
            return false;
        }
        if (split == KsSplitMode.Off && (rawApps.Count > 0 || rawNetworks.Count > 0))
        {
            error = "split-tunnel entries without a split mode";
            return false;
        }
        var apps = new List<string>();
        foreach (var app in rawApps)
        {
            if (!IsPlainExePath(app))
            {
                error = "invalid app path";
                return false;
            }
            if (!apps.Contains(app, StringComparer.OrdinalIgnoreCase))
            {
                apps.Add(app);
            }
        }
        var networks = new List<KsNetwork>();
        foreach (var text in rawNetworks)
        {
            if (!TryParseNetwork(text, out var network) || network.Prefix == 0)
            {
                error = "invalid network";
                return false;
            }
            if (!networks.Contains(network))
            {
                networks.Add(network);
            }
        }

        valid = new KsValidArm(arm.AllowLan, coreFull, endpoints, split, apps, networks, arm.KeepAfterReboot);
        error = "";
        return true;
    }

    /// <summary>A literal unicast address: no host names, no zone ids, nothing unspecified or multicast.</summary>
    public static bool TryParseHostAddress(string? text, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrEmpty(text) || text.Length > 45 || text.Contains('%') || !IPAddress.TryParse(text, out var parsed))
        {
            return false;
        }
        if (parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            // IPAddress.TryParse also accepts "1" or "0x7f.1": only dotted quads.
            if (text.Count(ch => ch == '.') != 3 || !text.All(ch => char.IsAsciiDigit(ch) || ch == '.'))
            {
                return false;
            }
        }
        else if (parsed.AddressFamily != AddressFamily.InterNetworkV6 || parsed.IsIPv4MappedToIPv6)
        {
            return false;
        }
        if (parsed.Equals(IPAddress.Any) || parsed.Equals(IPAddress.IPv6Any) || parsed.Equals(IPAddress.Broadcast)
            || IsMulticast(parsed) || IPAddress.IsLoopback(parsed))
        {
            return false;
        }
        address = parsed;
        return true;
    }

    /// <summary>"192.0.2.0/24", "2001:db8::/32" or a single address; host bits are cleared.</summary>
    public static bool TryParseNetwork(string? text, out KsNetwork network)
    {
        network = new KsNetwork(IPAddress.None, 0);
        if (string.IsNullOrWhiteSpace(text) || text.Length > 50)
        {
            return false;
        }
        var parts = text.Trim().Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var address) || parts[0].Contains('%'))
        {
            return false;
        }
        if (address.AddressFamily == AddressFamily.InterNetwork
            && (parts[0].Count(ch => ch == '.') != 3 || !parts[0].All(ch => char.IsAsciiDigit(ch) || ch == '.')))
        {
            return false;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
        {
            return false;
        }
        var max = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = max;
        if (parts.Length == 2 && (!int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out prefix) || prefix < 0 || prefix > max))
        {
            return false;
        }
        network = new KsNetwork(Mask(address, prefix), prefix);
        return true;
    }

    /// <summary>A rooted local path to an .exe: no UNC, device or relative paths, no wildcards or streams.</summary>
    public static bool IsPlainExePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > KsProtocol.MaxPathLength || path.Length < 8)
        {
            return false;
        }
        if (!(char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\'))
        {
            return false;
        }
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.IndexOfAny(['*', '?', '"', '<', '>', '|', '/']) >= 0
            || path.IndexOf(':', 2) >= 0 || path.Any(char.IsControl))
        {
            return false;
        }
        // No "..", no empty segments ("a\\b") and no names Windows would silently trim ("app.exe.").
        if (path[3..].Split('\\').Any(segment => segment.Length == 0 || segment.EndsWith('.') || segment.EndsWith(' ')))
        {
            return false;
        }
        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    internal static IPAddress Mask(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bits = Math.Clamp(prefix - i * 8, 0, 8);
            bytes[i] &= unchecked((byte)(0xFF << (8 - bits)));
        }
        return new IPAddress(bytes);
    }

    private static bool IsMulticast(IPAddress address) => address.AddressFamily == AddressFamily.InterNetwork
        ? address.GetAddressBytes()[0] >= 224
        : address.IsIPv6Multicast;

    private static bool IsIdChar(char ch) => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_';
}

/// <summary>JSON encoding and the length-prefixed framing.</summary>
public static class KsCodec
{
    public static byte[] Encode(KsRequest request) => JsonSerializer.SerializeToUtf8Bytes(request, KsJsonContext.Default.KsRequest);

    public static byte[] Encode(KsResponse response) => JsonSerializer.SerializeToUtf8Bytes(response, KsJsonContext.Default.KsResponse);

    public static bool TryDecodeRequest(ReadOnlySpan<byte> json, out KsRequest? request, out string error)
    {
        request = null;
        try
        {
            request = JsonSerializer.Deserialize(json, KsJsonContext.Default.KsRequest);
            error = request == null ? "empty request" : "";
            return request != null;
        }
        catch (JsonException)
        {
            error = "malformed JSON";
            return false;
        }
    }

    public static bool TryDecodeResponse(ReadOnlySpan<byte> json, out KsResponse? response)
    {
        response = null;
        try
        {
            response = JsonSerializer.Deserialize(json, KsJsonContext.Default.KsResponse);
            return response != null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken token)
    {
        if (payload.Length > KsProtocol.MaxMessageBytes)
        {
            throw new InvalidDataException("message too large");
        }
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        await stream.WriteAsync(frame, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    /// <summary>Reads one frame; a length outside 1..MaxMessageBytes is refused before anything is allocated.</summary>
    public static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 1 || length > KsProtocol.MaxMessageBytes)
        {
            throw new InvalidDataException("invalid frame length");
        }
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        return payload;
    }
}
