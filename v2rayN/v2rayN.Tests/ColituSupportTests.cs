using System.IO;
using System.Linq;
using AwesomeAssertions;
using v2rayN.Services;
using Xunit;

namespace v2rayN.Tests;

public class ColituSupportTests
{
    [Fact]
    public void Redact_MasksCredentialsInLogs()
    {
        var log = """
            Trying vless://0f5c2d9e-1111-2222-3333-444455556666@pro.example.org:8443?security=reality#Colitu
            hysteria2://s3cr3t@pro.example.org:8443?sni=pro.example.org
            Authorization: Bearer eyJhbGciOiJFZERTQSJ9.payload.sig
            {"password":"hunter2","uuid":"abc","server":"pro.example.org"}
            """;

        var redacted = ColituSupportService.Redact(log);

        redacted.Should().NotContain("0f5c2d9e").And.NotContain("s3cr3t").And.NotContain("eyJhbGci").And.NotContain("hunter2");
        redacted.Should().Contain("vless://***@pro.example.org:8443").And.Contain("\"server\":\"pro.example.org\"");
    }

    [Theory]
    [InlineData("screenshot.png", true)]
    [InlineData("report.PDF", true)]
    [InlineData("setup.exe", false)]
    [InlineData("script.ps1", false)]
    public void AllowedExtensions_MatchWhatThePanelStores(string name, bool allowed)
    {
        ColituSupportService.AllowedExtensions.Contains(Path.GetExtension(name).ToLowerInvariant()).Should().Be(allowed);
    }
}
