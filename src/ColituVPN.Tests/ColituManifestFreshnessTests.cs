using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using v2rayN.Services;
using Xunit;

namespace v2rayN.Tests;

/// <summary>The signed update manifest carries issued_at / expires_at: stale, expired or undated ones are not offered.</summary>
public class ColituManifestFreshnessTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

    private static ColituVersionPayload Manifest(DateTimeOffset? issued, DateTimeOffset? expires) => new()
    {
        LatestVersionCode = 290,
        VersionName = "2.9.0",
        DownloadUrl = "https://colitu.com/downloads/windows/ColituVPN-Setup-2.9.0-x64.exe",
        Sha256 = new string('a', 64),
        IssuedAt = issued == null ? null : Iso(issued.Value),
        ExpiresAt = expires == null ? null : Iso(expires.Value)
    };

    [Fact]
    public void FreshManifest_IsAccepted()
    {
        ColituUpdateSignature.IsFresh(Manifest(Now.AddDays(-1), Now.AddDays(29)), Now, out var reason).Should().BeTrue();
        reason.Should().BeEmpty();
        ColituUpdateSignature.IsFresh(Manifest(Now, Now.AddDays(30)), Now, out _).Should().BeTrue();
    }

    [Fact]
    public void ManifestIssuedMoreThan30DaysAgo_IsRejected()
    {
        ColituUpdateSignature.IsFresh(Manifest(Now.AddDays(-31), Now.AddDays(10)), Now, out var reason).Should().BeFalse();
        reason.Should().Contain("30 days");
        ColituUpdateSignature.IsFresh(Manifest(Now.AddDays(-30), Now.AddDays(10)), Now, out _).Should().BeTrue();
    }

    [Fact]
    public void ExpiredManifest_IsRejected()
    {
        ColituUpdateSignature.IsFresh(Manifest(Now.AddDays(-2), Now.AddSeconds(-1)), Now, out var reason).Should().BeFalse();
        reason.Should().Contain("expires_at");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void ManifestWithoutDates_IsRejected(bool hasIssued, bool hasExpires)
    {
        var manifest = Manifest(hasIssued ? Now : null, hasExpires ? Now.AddDays(30) : null);
        ColituUpdateSignature.IsFresh(manifest, Now, out _).Should().BeFalse();
        manifest.IssuedAt = "not a date";
        manifest.ExpiresAt = "";
        ColituUpdateSignature.IsFresh(manifest, Now, out _).Should().BeFalse();
    }

    [Fact]
    public void Dates_AreCoveredByTheSignature()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload = Manifest(Now, Now.AddDays(30));
        payload.SignatureV2 = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(ColituUpdateSignature.MessageV2(payload)), HashAlgorithmName.SHA256));
        var pem = key.ExportSubjectPublicKeyInfoPem();
        ColituUpdateSignature.Verify(payload, pem).Should().BeTrue();

        // Extending the lifetime or stripping the dates breaks the signature.
        payload.ExpiresAt = Iso(Now.AddDays(300));
        ColituUpdateSignature.Verify(payload, pem).Should().BeFalse();
        payload.ExpiresAt = null;
        ColituUpdateSignature.Verify(payload, pem).Should().BeFalse();
    }

    /// <summary>The 2.8.4 verification, copied: legacy text (no dates), the <c>signature</c> field.</summary>
    private static bool Legacy284Verify(ColituVersionPayload payload, string publicKeyPem)
    {
        var message = string.Join("\n",
            "colitu-windows-update-v1",
            payload.LatestVersionCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            payload.VersionName ?? "",
            payload.DownloadUrl ?? "",
            (payload.Sha256 ?? "").Trim().ToLowerInvariant(),
            payload.ForceUpdate ? "true" : "false");
        if (string.IsNullOrWhiteSpace(payload.Signature)) return false;
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        return key.VerifyData(Encoding.UTF8.GetBytes(message), Convert.FromBase64String(payload.Signature.Trim()), HashAlgorithmName.SHA256);
    }

    private static (string Pem, ColituVersionPayload Payload) DualSigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload = Manifest(Now, Now.AddDays(30));
        // Exactly what build-installer.ps1 does: signature = legacy text, signature_v2 = legacy text + dates.
        payload.Signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(ColituUpdateSignature.Message(payload)), HashAlgorithmName.SHA256));
        payload.SignatureV2 = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(ColituUpdateSignature.MessageV2(payload)), HashAlgorithmName.SHA256));
        return (key.ExportSubjectPublicKeyInfoPem(), payload);
    }

    [Fact]
    public void LegacySignature_StillVerifiesWithThe284Logic()
    {
        var (pem, payload) = DualSigned();

        Legacy284Verify(payload, pem).Should().BeTrue();
        ColituUpdateSignature.Message(payload).Should().NotContain(payload.IssuedAt!);
        // The new client accepts the same manifest through signature_v2.
        ColituUpdateSignature.Verify(payload, pem).Should().BeTrue();
    }

    [Fact]
    public void ChangedDates_BreakSignatureV2_ButNotTheLegacyOne()
    {
        var (pem, payload) = DualSigned();
        payload.ExpiresAt = Iso(Now.AddDays(365));

        ColituUpdateSignature.Verify(payload, pem).Should().BeFalse();
        Legacy284Verify(payload, pem).Should().BeTrue();
    }

    [Fact]
    public void MissingSignatureV2_IsRejected()
    {
        var (pem, payload) = DualSigned();
        payload.SignatureV2 = null;

        // A manifest that only carries the legacy signature (replayable: it has no dates) is refused.
        ColituUpdateSignature.Verify(payload, pem).Should().BeFalse();
        Legacy284Verify(payload, pem).Should().BeTrue();
    }

    [Fact]
    public void ManifestJson_ReadsSnakeCaseDates()
    {
        const string json = """{"latestVersionCode":290,"versionName":"2.9.0","downloadUrl":"https://colitu.com/x.exe","sha256":"aa","forceUpdate":false,"issued_at":"2026-10-10T12:00:00Z","expires_at":"2026-11-09T12:00:00Z","signature":"x","signature_v2":"y"}""";
        var payload = JsonSerializer.Deserialize<ColituVersionPayload>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        payload.IssuedAt.Should().Be("2026-10-10T12:00:00Z");
        payload.ExpiresAt.Should().Be("2026-11-09T12:00:00Z");
        payload.SignatureV2.Should().Be("y");
        ColituUpdateSignature.IsFresh(payload, Now, out _).Should().BeTrue();
    }
}
