using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AwesomeAssertions;
using v2rayN.Services;
using Xunit;

namespace v2rayN.Tests;

/// <summary>API certificate pinning: the built chain must contain a pinned public CA root until the pins expire.</summary>
public class ColituCertPinsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static string RootsDir => Path.Combine(AppContext.BaseDirectory, "TestData", "roots");

    private static X509Certificate2 LoadRoot(string file) =>
        X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(RootsDir, file)));

    private static X509Certificate2 SelfSigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=api.colitu.com", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(Now.AddDays(-1), Now.AddDays(30));
    }

    public static IEnumerable<object[]> RootFiles() =>
        new[] { "isrgrootx1.pem", "isrg-root-x2.pem", "root-ye.pem", "root-yr.pem", "gtsr1.pem", "gtsr4.pem" }
            .Select(file => new object[] { file });

    [Theory]
    [MemberData(nameof(RootFiles))]
    public void PinnedRoot_InBuiltChain_Matches(string file)
    {
        using var root = LoadRoot(file);
        using var leaf = SelfSigned();

        ColituCertPins.SpkiSha256Pins.Should().Contain(ColituCertPins.SpkiHash(root));
        ColituCertPins.Matches([leaf, root], Now).Should().BeTrue();
    }

    [Fact]
    public void SelfSignedChain_IsRejected()
    {
        using var leaf = SelfSigned();

        ColituCertPins.Matches([leaf], Now).Should().BeFalse();
        ColituCertPins.Matches([], Now).Should().BeFalse();
    }

    [Fact]
    public void AfterExpiry_PinsAreSkipped()
    {
        using var leaf = SelfSigned();

        ColituCertPins.Matches([leaf], ColituCertPins.PinsExpireAt.AddSeconds(1)).Should().BeTrue();
        ColituCertPins.Matches([leaf], ColituCertPins.PinsExpireAt).Should().BeFalse();
        ColituCertPins.PinsExpireAt.Should().Be(new DateTimeOffset(2027, 12, 31, 23, 59, 59, TimeSpan.Zero));
    }

    [Fact]
    public void BuiltChainElements_AreUsed()
    {
        using var root = LoadRoot("isrgrootx1.pem");
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        chain.Build(root);

        ColituCertPins.Matches(chain, Now).Should().BeTrue();
        ColituCertPins.Validate("api.colitu.com", chain, SslPolicyErrors.None, Now).Should().BeTrue();
    }

    [Fact]
    public void Validate_RequiresNormalValidationFirst()
    {
        using var root = LoadRoot("isrgrootx1.pem");
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        chain.Build(root);

        ColituCertPins.Validate("api.colitu.com", chain, SslPolicyErrors.RemoteCertificateChainErrors, Now).Should().BeFalse();
        ColituCertPins.Validate("api.colitu.com", chain, SslPolicyErrors.RemoteCertificateNameMismatch, Now).Should().BeFalse();
    }

    [Fact]
    public void Validate_SelfSignedWithNoPolicyErrors_IsRejectedUntilExpiry()
    {
        using var leaf = SelfSigned();
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        chain.Build(leaf);

        ColituCertPins.Validate("api.colitu.com", chain, SslPolicyErrors.None, Now).Should().BeFalse();
        ColituCertPins.Validate("api.colitu.com", chain, SslPolicyErrors.None, ColituCertPins.PinsExpireAt.AddDays(1)).Should().BeTrue();
        ColituCertPins.Validate("api.colitu.com", null, SslPolicyErrors.None, Now).Should().BeFalse();
    }
}
