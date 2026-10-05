using System.IO;
using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using v2rayN.Services;
using Xunit;

// Several tests switch the shared UI language (Loc.I); run the classes one after another.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace v2rayN.Tests;

/// <summary>Regression tests for the fixes of the 2026-10 security audit.</summary>
public class ColituAuditFixTests
{
    private static JsonElement Payload(string host) => JsonDocument.Parse(
        """{"schema_version":1,"protocol":"shadowsocks","endpoint":{"host":""" + JsonSerializer.Serialize(host)
        + ""","port":8388},"credentials":{"method":"aes-128-gcm","password":"p"},"transport":{"type":"tcp"},"security":{"type":"none"}}""")
        .RootElement.Clone();

    [Theory]
    [InlineData("vpn.example.com")]
    [InlineData("203.0.113.9")]
    public void ShareLink_AcceptsHostNamesAndAddresses(string host)
    {
        ColituShareLinkBuilder.Build(Payload(host), "x").Should().Contain($"@{host}:8388");
    }

    [Theory]
    [InlineData("evil.example?allowInsecure=1")]
    [InlineData("a@b.example")]
    [InlineData("host.example#frag")]
    [InlineData("host.example\nline")]
    [InlineData("host/path")]
    [InlineData("bücher.example")]
    [InlineData("fe80::1%eth0")]
    public void ShareLink_RejectsHostsThatCouldInjectParameters(string host)
    {
        ColituShareLinkBuilder.Build(Payload(host), "x").Should().BeNull();
    }

    [Fact]
    public void ShareLink_RejectsANonIntegerSchemaVersion()
    {
        var payload = JsonDocument.Parse("""{"schema_version":1.5,"protocol":"shadowsocks"}""").RootElement.Clone();
        ColituShareLinkBuilder.Build(payload, "x").Should().BeNull();
    }

    [Theory]
    [InlineData("203.0.113.7", true)]
    [InlineData("8.8.8.8", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    public void Latency_OnlyDialsPublicAddresses(string address, bool allowed)
    {
        ColituLatency.IsPublic(IPAddress.Parse(address)).Should().Be(allowed);
    }

    [Fact]
    public void Redact_MasksQuerySecretsEmailsTokensAndUrlCredentials()
    {
        var log = string.Join('\n',
            "GET https://api.example.com/x?token=tok123&lang=en",
            "vless://u@h:443?pbk=pbk456&sid=sid789&sni=www.example.com",
            "account mail@example.com signed in",
            "proxy http://user:pa55@10.0.0.1:8080",
            "socks://dXNlcjpwYXNz@h:1080",
            "vmess://eyJ2IjoiMiIsImlkIjoic2VjcmV0LXV1aWQifQ==",
            "ss://YWVzLTEyOC1nY206c2VjcmV0cGFzcw==",
            "Authorization: Basic dXNlcjpzZWNyZXQ=",
            "token eyJhbGciOiJFUzI1NiJ9.eyJzdWIiOiJ1c2VyIn0.c2lnbmF0dXJl",
            "{\"id\":\"0b2c-uuid\",\"auth\":\"hy-secret\",\"server\":\"h\"}");

        var redacted = ColituSupportService.Redact(log);

        foreach (var secret in new[] { "tok123", "pbk456", "sid789", "mail@", "pa55", "dXNlcjpwYXNz", "c2VjcmV0LXV1aWQ", "YWVzLTEyOC1nY206", "dXNlcjpzZWNyZXQ", "eyJzdWIiOiJ1c2VyIn0", "0b2c-uuid", "hy-secret" })
        {
            redacted.Should().NotContain(secret);
        }
        redacted.Should().Contain("lang=en").And.Contain("\"server\":\"h\"");
    }

    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("setup.exe", "setup.exe.download")]
    [InlineData("..\\..\\Windows\\evil.lnk", "evil.lnk.download")]
    [InlineData("photo.PNG", "photo.PNG")]
    [InlineData("", "file.download")]
    public void SupportAttachment_NeverGetsARunnableName(string fileName, string expectedSuffix)
    {
        var name = ColituSupportService.AttachmentFileName(new ColituSupportAttachment { Id = "../ab:cd-1234567", FileName = fileName });

        name.Should().EndWith(expectedSuffix);
        name.Should().StartWith("abcd-123-");
        name.IndexOfAny(Path.GetInvalidFileNameChars()).Should().Be(-1);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Colitu VPN\guiLogs", @"C:\Program Files\Colitu VPN", true)]
    [InlineData(@"C:\Program Files\Colitu VPN", @"C:\Program Files\Colitu VPN\", true)]
    [InlineData(@"C:\Program Files\Colitu VPN2\x", @"C:\Program Files\Colitu VPN", false)]
    [InlineData(@"C:\Windows", @"C:\Program Files\Colitu VPN", false)]
    public void Shell_OnlyOpensFoldersInsideItsOwn(string path, string root, bool inside)
    {
        ColituShell.IsInside(path, root).Should().Be(inside);
    }

    [Theory]
    [InlineData("2.5.4", "2.5.4", true)]
    [InlineData("2.5.4.0", "2.5.4", true)]
    [InlineData("2.5.40", "2.5.4", false)]
    [InlineData("2.5.4", "2.5.40", false)]
    [InlineData("2.6.0+abc", "2.6.0", true)]
    public void Update_VersionCheckIsExact(string actual, string expected, bool same)
    {
        ColituUpdateService.SameVersion(actual, expected).Should().Be(same);
    }

    [Fact]
    public void ProxyMode_IsKeptWhenTheAppRunsAsTheSignedInUser()
    {
        // The test runs as the user of its own session: proxy mode must not be forced to TUN.
        ColituHardening.ElevatedAsAnotherUser.Should().BeFalse();
    }

    [Fact]
    public void ApiErrorWithNonStringFields_StaysAnApiError()
    {
        var message = ColituAuthService.FriendlyMessage(null, null, HttpStatusCode.BadRequest);
        message.Should().NotBeNullOrWhiteSpace();
        ColituAuthService.FriendlyMessage(null, "already ends.", HttpStatusCode.BadRequest).Should().Be("Already ends.");
    }
}
