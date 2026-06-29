using ChessSchool.Arena;
using ChessSchool.Arena.Services;

namespace ChessSchool.Tests;

/// <summary>
/// Разбор ответа источника популярных турниров (lichess /api/broadcast/top) и маппинг в каталог.
/// Чистые функции — без сети.
/// </summary>
public class TournamentDiscoveryTests
{
    // Реалистичный фрагмент ответа /api/broadcast/top: активный, предстоящий и прошедший турниры.
    // Даты — epoch ms: 2026-06-29 и 2026-07-06.
    private const string Sample = """
    {
      "active": [
        {
          "tour": {
            "id": "abc123",
            "name": "SuperUnited Rapid & Blitz Croatia",
            "slug": "superunited-rapid-blitz-croatia",
            "info": { "format": "Рапид и блиц", "location": "Загреб, Хорватия", "website": "https://grandchesstour.org" },
            "dates": [1782691200000, 1783296000000],
            "tier": 5,
            "image": "https://image.lichess1.org/cdn/croatia.jpg",
            "url": "https://lichess.org/broadcast/superunited/abc123"
          }
        }
      ],
      "upcoming": [
        {
          "tour": {
            "id": "def456",
            "name": "Biel Chess Festival",
            "slug": "biel-chess-festival",
            "info": { "tc": "Классика", "location": "Биль" },
            "dates": [1752192000000, 1753315200000],
            "url": "https://lichess.org/broadcast/biel/def456"
          }
        }
      ],
      "past": {
        "currentPageResults": [
          { "tour": { "id": "old", "name": "Old Event", "slug": "old-event", "dates": [1, 2] } }
        ]
      }
    }
    """;

    private static readonly DateOnly Fallback = new(2026, 1, 1);

    [Fact]
    public void Parse_TakesActiveAndUpcoming_IgnoresPast()
    {
        var items = TournamentDiscovery.Parse(Sample, Fallback);

        Assert.Equal(2, items.Count);
        Assert.DoesNotContain(items, s => s.Slug == "old-event"); // прошедшие не предлагаем
        Assert.Equal("superunited-rapid-blitz-croatia", items[0].Slug);
        Assert.True(items[0].Live);   // из секции active
        Assert.False(items[1].Live);  // из секции upcoming
    }

    [Fact]
    public void Parse_MapsDatesLocationFormatUrlImage()
    {
        var active = TournamentDiscovery.Parse(Sample, Fallback)[0];

        Assert.Equal("SuperUnited Rapid & Blitz Croatia", active.Name);
        Assert.Equal(new DateOnly(2026, 6, 29), active.Start);
        Assert.Equal(new DateOnly(2026, 7, 6), active.End);
        Assert.Equal("Загреб, Хорватия", active.Location);
        Assert.Equal("Рапид и блиц", active.Format);
        Assert.Equal("https://grandchesstour.org", active.Url); // info.website приоритетнее url трансляции
        Assert.Equal("https://image.lichess1.org/cdn/croatia.jpg", active.ImageUrl);
    }

    [Fact]
    public void Parse_FallsBackFormatToTc_AndUrlToTourUrl_AndNullImage()
    {
        var upcoming = TournamentDiscovery.Parse(Sample, Fallback)[1];

        Assert.Equal("Классика", upcoming.Format);                              // нет format → берём tc
        Assert.Equal("https://lichess.org/broadcast/biel/def456", upcoming.Url); // нет website → url трансляции
        Assert.Null(upcoming.ImageUrl);                                          // нет image → null
    }

    [Fact]
    public void Parse_UsesFallbackDate_WhenNoDates()
    {
        const string json = """{ "active": [ { "tour": { "name": "No Dates Open", "slug": "no-dates-open" } } ] }""";
        var item = TournamentDiscovery.Parse(json, Fallback).Single();
        Assert.Equal(Fallback, item.Start);
        Assert.Equal(Fallback, item.End);
    }

    [Fact]
    public void Parse_RespectsMaxLimit()
    {
        var items = TournamentDiscovery.Parse(Sample, Fallback, max: 1);
        Assert.Single(items);
        Assert.Equal("superunited-rapid-blitz-croatia", items[0].Slug); // active идёт первым
    }

    [Fact]
    public void Parse_EmptyOrMalformed_ReturnsEmpty()
    {
        Assert.Empty(TournamentDiscovery.Parse("{}", Fallback));
        Assert.Empty(TournamentDiscovery.Parse("[]", Fallback));
    }

    [Theory]
    [InlineData("Загреб, Хорватия", "Загреб", "Хорватия")]
    [InlineData("Wijk aan Zee, Netherlands", "Wijk aan Zee", "Netherlands")]
    [InlineData("Лондон", "Лондон", "")]
    [InlineData("", "", "")]
    public void SplitLocation_SplitsOnLastComma(string input, string city, string country)
    {
        var (c, co) = TournamentDiscovery.SplitLocation(input);
        Assert.Equal(city, c);
        Assert.Equal(country, co);
    }

    [Fact]
    public void ToBroadcast_AddsHidden_WithSplitLocation()
    {
        var s = new TournamentSuggestion("biel-chess-festival", "Biel Chess Festival",
            new DateOnly(2026, 7, 11), new DateOnly(2026, 7, 24), "Биль, Швейцария", "Классика",
            "https://example.org", "https://img/x.jpg", Live: false);

        var b = TournamentDiscovery.ToBroadcast(s);

        Assert.False(b.Visible); // добавляется скрытой — админ доклассифицирует и публикует
        Assert.Equal("biel-chess-festival", b.Slug);
        Assert.Equal("Биль", b.City);
        Assert.Equal("Швейцария", b.Country);
        Assert.Equal("https://img/x.jpg", b.ImageUrl);
        Assert.True(BroadcastFormat.IsValidSlug(b.Slug));
    }

    [Fact]
    public void ToBroadcast_InvalidSlug_IsSlugifiedFromName()
    {
        var s = new TournamentSuggestion("", "Tata Steel Masters 2026",
            new DateOnly(2026, 1, 17), new DateOnly(2026, 2, 1), "", "", "", null, Live: true);

        var b = TournamentDiscovery.ToBroadcast(s);

        Assert.True(BroadcastFormat.IsValidSlug(b.Slug));
        Assert.Equal("tata-steel-masters-2026", b.Slug);
    }
}
