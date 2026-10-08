using AwesomeAssertions;
using v2rayN.Services;
using Xunit;

namespace v2rayN.Tests;

/// <summary>Locations list: servers grouped by country, a lone server stays a plain row.</summary>
public class ColituServerGroupsTests
{
    private static readonly StringComparer Comparer = StringComparer.Create(System.Globalization.CultureInfo.InvariantCulture, true);

    private static string? Name(string? code) => (code ?? "").ToUpperInvariant() switch
    {
        "DE" => "Germany",
        "FI" => "Finland",
        "GB" or "UK" => "United Kingdom",
        _ => null
    };

    private static ColituVpnServer Server(string id, string code, string city, string? display = null) =>
        new() { Id = id, CountryCode = code, City = city, Name = display ?? city };

    private static readonly ColituVpnServer[] Servers =
    [
        Server("de-fra", "DE", "Frankfurt"),
        Server("fi-hel", "FI", "Helsinki"),
        Server("de-ber", "DE", "Berlin"),
        Server("uk-lon", "UK", "London"),
        Server("gb-man", "GB", "Manchester"),
    ];

    [Fact]
    public void Build_GroupsByCountry_SortedByCountryThenCity()
    {
        var groups = ColituServerGroups.Build(Servers, Name, Comparer, flat: false);

        groups.Select(group => group.Name).Should().Equal("Finland", "Germany", "United Kingdom");
        groups[1].Servers.Select(server => server.City).Should().Equal("Berlin", "Frankfurt");
        // UK and GB are one country.
        groups[2].Servers.Select(server => server.Id).Should().Equal("uk-lon", "gb-man");
    }

    [Fact]
    public void Build_CountryWithOneServer_StaysASingleRow()
    {
        var groups = ColituServerGroups.Build(Servers, Name, Comparer, flat: false);

        groups.Single(group => group.Name == "Finland").IsSingle.Should().BeTrue();
        groups.Single(group => group.Name == "Germany").IsSingle.Should().BeFalse();
    }

    [Fact]
    public void Build_Flat_KeepsEveryServerAlone()
    {
        var groups = ColituServerGroups.Build(Servers, Name, Comparer, flat: true);

        groups.Should().HaveCount(5).And.OnlyContain(group => group.IsSingle);
        groups.Select(group => group.Servers[0].City).Should().Equal("Helsinki", "Berlin", "Frankfurt", "London", "Manchester");
    }

    [Fact]
    public void Build_WithoutCountryCode_DoesNotMergeUnrelatedServers()
    {
        var groups = ColituServerGroups.Build([Server("a", "", "One"), Server("b", "", "Two")], Name, Comparer, flat: false);

        groups.Should().HaveCount(2).And.OnlyContain(group => group.IsSingle);
    }

    [Fact]
    public void BestPing_IsTheLowestMeasuredOne()
    {
        var pings = new Dictionary<string, int> { ["de-fra"] = 48, ["de-ber"] = 0 };
        var servers = Servers.Where(server => server.CountryCode == "DE");

        ColituServerGroups.BestPing(servers, server => pings.TryGetValue(server.Id!, out var ms) ? ms : null).Should().Be(48);
        ColituServerGroups.BestPing(servers, _ => null).Should().BeNull();
    }
}
