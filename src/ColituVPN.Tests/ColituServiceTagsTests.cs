using System.Text.Json;
using AwesomeAssertions;
using v2rayN.Services;
using Xunit;

namespace v2rayN.Tests;

/// <summary>Service tags of a server: only youtube_adfree is used by the desktop list; unknown keys are ignored.</summary>
public class ColituServiceTagsTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private static ColituVpnServer Map(string json) =>
        ColituApiClient.MapServer(JsonSerializer.Deserialize<ColituServerDto>(json, Json)!);

    [Fact]
    public void Services_AreParsed_NormalisedAndDeduplicated()
    {
        var server = Map("""{"id":"a","country":"AL","status":"online","services":["ChatGPT"," youtube_adfree ","youtube_adfree","","mystery_service"]}""");

        server.Services.Should().Equal("chatgpt", "youtube_adfree", "mystery_service");
        server.HasAdFreeYoutube.Should().BeTrue();
    }

    [Fact]
    public void Server_WithoutServices_OrWithOtherKeys_HasNoAdFreeYoutube()
    {
        Map("""{"id":"a","country":"DE","status":"online"}""").Services.Should().BeEmpty();
        Map("""{"id":"b","country":"DE","status":"online","services":["netflix","youtube_premium"]}""").HasAdFreeYoutube.Should().BeFalse();
    }

    [Fact]
    public void AdFreeYoutube_DoesNotAddACategory()
    {
        var server = Map("""{"id":"a","country":"AL","status":"online","categories":["privacy"],"services":["youtube_adfree"]}""");

        server.Categories.Should().Equal("privacy");
    }

    [Fact]
    public void AdFreeYoutube_HasTextInEveryLanguage()
    {
        Loc.ValuesOf("service.youtube_adfree").Should().Equal("YouTube без рекламы", "Reklamsız YouTube", "Ad-free YouTube");
    }
}
