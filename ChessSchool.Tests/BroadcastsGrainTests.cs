using ChessSchool.Arena;
using ChessSchool.Arena.Grains;
using Orleans.TestingHost;

namespace ChessSchool.Tests;

/// <summary>Каталог трансляций: сид, CRUD, скрытие и переживание деактивации грейна.</summary>
public class BroadcastsGrainTests
{
    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder) =>
            siloBuilder.AddMemoryGrainStorage("arena"); // хранилище каталога трансляций
    }

    private static async Task<TestCluster> StartAsync()
    {
        var cluster = new TestClusterBuilder().AddSiloBuilderConfigurator<SiloConfigurator>().Build();
        await cluster.DeployAsync();
        return cluster;
    }

    private static Broadcast Sample(string slug) => new()
    {
        Slug = slug,
        Name = "Test Broadcast",
        Series = "Классика",
        SeriesCls = "cls",
        Start = new DateOnly(2026, 9, 1),
        End = new DateOnly(2026, 9, 7),
        City = "Берлин",
        Country = "Германия",
        Flag = "🇩🇪",
        Format = "Классика",
        Url = "https://example.org",
        ImageUrl = "https://example.org/img.jpg",
        Visible = true,
    };

    [Fact]
    public async Task FirstActivation_SeedsInitialCatalog()
    {
        var cluster = await StartAsync();
        try
        {
            var grain = cluster.GrainFactory.GetGrain<IBroadcastsGrain>(0);
            var all = await grain.GetAllAsync();

            Assert.Equal(BroadcastSeed.Initial.Count, all.Count);
            Assert.All(all, b => Assert.True(b.Visible));
            Assert.All(all, b => Assert.False(string.IsNullOrWhiteSpace(b.ImageUrl))); // у стартовых трансляций заполнен фон
            Assert.Contains(all, b => b.Slug == "sinquefield-cup-2026");
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    [Fact]
    public async Task Upsert_AddsNew_AndUpdatesExisting()
    {
        var cluster = await StartAsync();
        try
        {
            var grain = cluster.GrainFactory.GetGrain<IBroadcastsGrain>(0);

            await grain.UpsertAsync(Sample("new-event"));
            var added = await grain.GetAsync("new-event");
            Assert.NotNull(added);
            Assert.Equal("Test Broadcast", added!.Name);

            var edit = Sample("new-event");
            edit.Name = "Renamed";
            await grain.UpsertAsync(edit);

            var all = await grain.GetAllAsync();
            Assert.Single(all, b => b.Slug == "new-event"); // не задвоилось
            Assert.Equal("Renamed", (await grain.GetAsync("new-event"))!.Name);
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    [Fact]
    public async Task SetVisible_TogglesFlag()
    {
        var cluster = await StartAsync();
        try
        {
            var grain = cluster.GrainFactory.GetGrain<IBroadcastsGrain>(0);
            await grain.UpsertAsync(Sample("vis-event"));

            Assert.True(await grain.SetVisibleAsync("vis-event", false));
            Assert.False((await grain.GetAsync("vis-event"))!.Visible);

            Assert.True(await grain.SetVisibleAsync("vis-event", true));
            Assert.True((await grain.GetAsync("vis-event"))!.Visible);

            Assert.False(await grain.SetVisibleAsync("missing", false)); // нет записи
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    [Fact]
    public async Task Delete_RemovesItem()
    {
        var cluster = await StartAsync();
        try
        {
            var grain = cluster.GrainFactory.GetGrain<IBroadcastsGrain>(0);
            await grain.UpsertAsync(Sample("del-event"));

            Assert.True(await grain.DeleteAsync("del-event"));
            Assert.Null(await grain.GetAsync("del-event"));
            Assert.False(await grain.DeleteAsync("del-event")); // повторно — нечего удалять
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    [Fact]
    public async Task EditsSurviveDeactivation_AndNoReseed()
    {
        var cluster = await StartAsync();
        try
        {
            var grain = cluster.GrainFactory.GetGrain<IBroadcastsGrain>(0);
            // Удаляем весь сид и добавляем одну свою запись.
            foreach (var b in await grain.GetAllAsync()) await grain.DeleteAsync(b.Slug);
            await grain.UpsertAsync(Sample("only-one"));

            // Принудительная деактивация грейна по простою.
            await cluster.GrainFactory.GetGrain<IManagementGrain>(0).ForceActivationCollection(TimeSpan.Zero);

            // Реактивация: состояние читается из хранилища; сид не должен повториться (флаг Seeded сохранён).
            var after = await cluster.GrainFactory.GetGrain<IBroadcastsGrain>(0).GetAllAsync();
            Assert.Single(after);
            Assert.Equal("only-one", after[0].Slug);
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    [Fact]
    public void BackfillImages_FillsOnlyEmpties_BySlug_WithoutTouchingEdits()
    {
        // Каталог «как после v1»: у сид-записи пустой фон, у админской — свой (не сид), есть запись вне сида.
        var seedSlug = BroadcastSeed.Initial.First(s => !string.IsNullOrWhiteSpace(s.ImageUrl)).Slug;
        var custom = "https://my.cdn/edited.jpg";
        var items = new List<Broadcast>
        {
            new() { Slug = seedSlug, ImageUrl = null },                 // должен заполниться из сида
            new() { Slug = "custom-event", ImageUrl = custom },         // правка админа — не трогаем
            new() { Slug = "unknown-slug", ImageUrl = null },           // нет в сиде — остаётся пустым
        };

        var filled = BroadcastSeed.BackfillImages(items);

        Assert.Equal(1, filled);
        Assert.False(string.IsNullOrWhiteSpace(items[0].ImageUrl)); // заполнено из сида
        Assert.Equal(custom, items[1].ImageUrl);                    // не перетёрто
        Assert.Null(items[2].ImageUrl);                             // вне сида — без изменений

        // Идемпотентность: повторный вызов ничего не заполняет.
        Assert.Equal(0, BroadcastSeed.BackfillImages(items));
    }

    [Theory]
    [InlineData("Sinquefield Cup 2026", "sinquefield-cup-2026")]
    [InlineData("  Biel — Festival!! ", "biel-festival")]
    [InlineData("GCT/Finals", "gct-finals")]
    public void Slugify_ProducesUrlSafeSlug(string input, string expected) =>
        Assert.Equal(expected, BroadcastFormat.Slugify(input));

    [Theory]
    [InlineData("valid-slug-1", true)]
    [InlineData("UPPER", false)]
    [InlineData("with space", false)]
    [InlineData("-leading", false)]
    [InlineData("", false)]
    public void IsValidSlug_Validates(string input, bool expected) =>
        Assert.Equal(expected, BroadcastFormat.IsValidSlug(input));
}
