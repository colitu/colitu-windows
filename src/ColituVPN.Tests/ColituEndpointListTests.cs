using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using AwesomeAssertions;
using v2rayN.Services;
using Xunit;

namespace v2rayN.Tests;

/// <summary>API failover through the signed endpoint list (panel repo deploy/endpoints/APP_SPEC.md).</summary>
public class ColituEndpointListTests
{
    private const long ValidVersion = 1000;
    private const string Mirror = "https://mirror.example.test";
    private static ColituEndpointList NewList(string? path = null) => new(path, [Mirror]);

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", name));

    [Fact]
    public void ValidFixture_IsAccepted()
    {
        var data = ColituEndpointList.Verify(Fixture("valid.signed.json"), null, out _);

        data.Should().NotBeNull();
        data!.Version.Should().Be(ValidVersion);
        data.Api.Should().Equal("https://api.colitu.com/api/v1", "https://mirror.example.test/capi/v1");
        data.Web.Should().Equal("https://colitu.com", "https://mirror.example.test");
        data.Lists.Should().Equal(
            "https://colitu.com/downloads/endpoints.json",
            "https://mirror.example.test/downloads/endpoints.json");
    }

    [Fact]
    public void TamperedFixture_IsRejected()
    {
        ColituEndpointList.Verify(Fixture("tampered.signed.json"), null, out var reason).Should().BeNull();
        reason.Should().NotBeEmpty();
    }

    [Fact]
    public void WrongKeyIdFixture_IsRejected()
    {
        ColituEndpointList.Verify(Fixture("wrong-key-id.signed.json"), null, out var reason).Should().BeNull();
        reason.Should().Contain("key");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"key_id\":\"e1\",\"payload\":\"!!\",\"signature\":\"!!\"}")]
    public void Garbage_IsRejected(string raw)
    {
        ColituEndpointList.Verify(raw, null, out _).Should().BeNull();
    }

    [Fact]
    public void OlderOrEqualVersion_IsRejected_AfterANewerOneIsStored()
    {
        var list = NewList();

        list.TryAccept(Fixture("valid.signed.json")).Should().BeTrue();
        list.StoredVersion.Should().Be(ValidVersion);

        // The same version again is not newer.
        list.TryAccept(Fixture("valid.signed.json")).Should().BeFalse();
        ColituEndpointList.Verify(Fixture("valid.signed.json"), ValidVersion + 1, out var reason).Should().BeNull();
        reason.Should().Contain("newer");
        ColituEndpointList.Verify(Fixture("valid.signed.json"), ValidVersion - 1, out _).Should().NotBeNull();
    }

    [Fact]
    public void State_SurvivesARestart_AndKeepsTheWorkingBase()
    {
        var path = Path.Combine(Path.GetTempPath(), $"colitu-endpoints-{Guid.NewGuid():N}.json");
        try
        {
            var first = NewList(path);
            first.TryAccept(Fixture("valid.signed.json")).Should().BeTrue();
            first.RememberWorking("https://mirror.example.test/capi/v1/");

            var second = NewList(path);
            second.StoredVersion.Should().Be(ValidVersion);
            second.Bases().Should().Equal("https://mirror.example.test/capi/v1", "https://api.colitu.com/api/v1");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Bases_AreTheBuiltInOnes_UntilAListIsAccepted()
    {
        var list = NewList();

        list.Bases().Should().Equal("https://api.colitu.com/api/v1", "https://mirror.example.test/capi/v1");
        list.ListUrls().Should().Equal(
            "https://colitu.com/downloads/endpoints.json",
            "https://mirror.example.test/downloads/endpoints.json");
        // A build without mirrors knows only the main domain.
        new ColituEndpointList(null, []).Bases().Should().Equal("https://api.colitu.com/api/v1");
    }

    [Fact]
    public void ParseMirrors_KeepsHttpsOriginsOnly_TrimsTheTrailingSlash()
    {
        ColituEndpointList.ParseMirrors(" https://m1.example.test/ ,http://insecure.example.test,junk,,https://m2.example.test")
            .Should().Equal("https://m1.example.test", "https://m2.example.test");
        ColituEndpointList.ParseMirrors(null).Should().BeEmpty();
        ColituEndpointList.BuildApi(["https://m1.example.test"])
            .Should().Equal("https://api.colitu.com/api/v1", "https://m1.example.test/capi/v1");
    }

    [Fact]
    public void Order_PutsTheLastWorkingBaseFirst_AndDeduplicates()
    {
        var order = ColituEndpointList.Order(
            ["https://a.example/api", "https://b.example/api/", "https://a.example/api", "https://c.example/api"],
            "https://c.example/api");

        order.Should().Equal("https://c.example/api", "https://a.example/api", "https://b.example/api");
        ColituEndpointList.Order(["https://a.example/api", "https://b.example/api"], "https://gone.example/api")
            .Should().Equal("https://a.example/api", "https://b.example/api");
    }

    [Fact]
    public void Rebase_ReplacesOnlyTheKnownBasePrefix()
    {
        string[] known = ["https://api.colitu.com/api/v1", "https://mirror.example.test/capi/v1"];

        ColituEndpointList.Rebase(new Uri("https://api.colitu.com/api/v1/devices/x?a=1"), known, "https://mirror.example.test/capi/v1")
            .ToString().Should().Be("https://mirror.example.test/capi/v1/devices/x?a=1");
        ColituEndpointList.Rebase(new Uri("https://other.example/x"), known, "https://mirror.example.test/capi/v1")
            .ToString().Should().Be("https://other.example/x");
    }

    [Fact]
    public void ShouldFailover_NetworkErrorsOnly()
    {
        var refused = new HttpRequestException(HttpRequestError.ConnectionError, "refused", new SocketException((int)SocketError.ConnectionRefused));
        var dns = new HttpRequestException(HttpRequestError.NameResolutionError, "dns");
        var tls = new HttpRequestException(HttpRequestError.SecureConnectionError, "tls");
        var closed = new HttpRequestException(HttpRequestError.ResponseEnded, "closed before a response");
        var timeout = new TaskCanceledException("timeout", new TimeoutException());

        foreach (var ex in new Exception[] { refused, dns, tls })
        {
            ColituEndpointList.ShouldFailover(ex, isGet: true).Should().BeTrue();
            ColituEndpointList.ShouldFailover(ex, isGet: false).Should().BeTrue();
        }
        // After the body may have been sent, a non-GET request is not repeated.
        ColituEndpointList.ShouldFailover(closed, isGet: true).Should().BeTrue();
        ColituEndpointList.ShouldFailover(closed, isGet: false).Should().BeFalse();
        ColituEndpointList.ShouldFailover(timeout, isGet: true).Should().BeTrue();
        ColituEndpointList.ShouldFailover(timeout, isGet: false).Should().BeFalse();
        ColituEndpointList.ShouldFailover(new InvalidOperationException(), isGet: true).Should().BeFalse();
    }

    [Fact]
    public async Task Failover_NetworkError_GoesToTheNextBase_AndReportsTheWorkingOne()
    {
        var tried = new List<string>();
        string? worked = null;

        using var response = await ColituEndpointList.SendWithFailoverAsync(
            ["https://a.example", "https://b.example", "https://c.example"],
            baseUrl =>
            {
                tried.Add(baseUrl);
                return baseUrl == "https://a.example"
                    ? throw new HttpRequestException(HttpRequestError.ConnectionError, "refused")
                    : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            },
            ex => ColituEndpointList.ShouldFailover(ex, isGet: true),
            baseUrl => worked = baseUrl,
            CancellationToken.None);

        tried.Should().Equal("https://a.example", "https://b.example");
        worked.Should().Be("https://b.example");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Failover_Http500_IsAnAnswer_NoFailover()
    {
        var tried = new List<string>();
        string? worked = null;

        using var response = await ColituEndpointList.SendWithFailoverAsync(
            ["https://a.example", "https://b.example"],
            baseUrl =>
            {
                tried.Add(baseUrl);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            },
            ex => ColituEndpointList.ShouldFailover(ex, isGet: true),
            baseUrl => worked = baseUrl,
            CancellationToken.None);

        tried.Should().Equal("https://a.example");
        worked.Should().Be("https://a.example");
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task Failover_AllBasesDown_TriesEachOnce_AndThrowsTheLastError()
    {
        var tried = new List<string>();

        var act = () => ColituEndpointList.SendWithFailoverAsync(
            ["https://a.example", "https://b.example"],
            baseUrl =>
            {
                tried.Add(baseUrl);
                return Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.NameResolutionError, baseUrl));
            },
            ex => ColituEndpointList.ShouldFailover(ex, isGet: true),
            null,
            CancellationToken.None);

        (await act.Should().ThrowAsync<HttpRequestException>()).WithMessage("https://b.example");
        tried.Should().Equal("https://a.example", "https://b.example");
    }

    [Fact]
    public async Task Refresh_StopsAtTheFirstAcceptedFile_AndIgnoresBadOnes()
    {
        var list = NewList();
        var asked = new List<string>();

        var accepted = await list.RefreshAsync(CancellationToken.None, (url, _) =>
        {
            asked.Add(url);
            return Task.FromResult<string?>(url.Contains("colitu.com") ? Fixture("tampered.signed.json") : Fixture("valid.signed.json"));
        });

        accepted.Should().BeTrue();
        asked.Should().Equal(list.ListUrls());
        list.StoredVersion.Should().Be(ValidVersion);

        // Network errors are silent.
        var other = NewList();
        (await other.RefreshAsync(CancellationToken.None, (_, _) => throw new HttpRequestException("down"))).Should().BeFalse();
        other.StoredVersion.Should().BeNull();
    }

    [Fact]
    public void PinnedUrls_CoverEveryBaseAndListHost()
    {
        var hosts = NewList().PinnedUrls().Select(url => new Uri(url).Host).ToHashSet();

        hosts.Should().Contain(["api.colitu.com", "mirror.example.test", "colitu.com"]);
    }
}
