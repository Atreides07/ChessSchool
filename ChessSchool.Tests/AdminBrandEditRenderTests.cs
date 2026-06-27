using Bunit;
using ChessSchool.Arena;
using ChessSchool.Arena.Components.Pages.Admin;
using ChessSchool.Arena.Grains;
using ChessSchool.Arena.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.TestingHost;

namespace ChessSchool.Tests;

/// <summary>
/// Воспроизводит сценарий из бага: на странице создания бренд-турнира админ вводит русское
/// название и вставляет длинный URL фонового изображения (idchess _next/image с query-строкой).
/// Тест рендерит реальный компонент против реального грейна (TestCluster) — если путь рендера
/// или сохранения бросает исключение, тест упадёт со стектрейсом.
/// </summary>
public class AdminBrandEditRenderTests : BunitContext
{
    private const string ProblemUrl =
        "https://media.idchess.com/_next/image?url=https%3A%2F%2Fs3.idsport.tech%2Fidsport-banner%2Fbanners%2F0c7ab2de-db31-43e3-bbac-d29ac0f5580b%2F1775139365_f5503966.webp&w=640&q=75";

    private sealed class FakeEngine : IChessEngine
    {
        public Task<string?> GetBestMoveAsync(string fen, int skillLevel, int moveTimeMs, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryGrainStorage("arena");
            siloBuilder.UseInMemoryReminderService();
            siloBuilder.ConfigureServices(s =>
            {
                s.AddSingleton<ArenaNotifier>();
                s.AddSingleton<IChessEngine, FakeEngine>();
                s.AddSingleton(new ArenaRuntimeOptions(RemindersEnabled: false));
                s.AddSingleton<IAnalytics, NoopAnalytics>();
            });
        }
    }

    [Fact]
    public async Task EnterRussianName_AndIdchessImageUrl_RendersAndSaves()
    {
        var cluster = new TestClusterBuilder().AddSiloBuilderConfigurator<SiloConfigurator>().Build();
        await cluster.DeployAsync();
        try
        {
            Services.AddLogging();
            Services.AddHttpClient();
            Services.AddSingleton<IGrainFactory>(cluster.GrainFactory);
            Services.AddSingleton<BrandTournamentCatalog>();
            Services.AddSingleton<IImageStorage, NullImageStorage>();
            Services.AddSingleton<IImageIngestor, ImageIngestor>();

            var cut = Render<AdminBrandTournamentEdit>(p => p.Add(c => c.Slug, (string?)null));

            var inputs = cut.FindAll("input.adm-in");
            // Порядок: [Name, Slug, Start(date), initial, increment, duration, image].
            inputs[0].Input("Шахматный турнир Бристоль"); // русское название → автогенерация slug
            cut.FindAll("input.adm-in")[6].Input(ProblemUrl); // длинный URL фона — раньше ронял контур

            // Превью должно отрисовать картинку с этим URL, без исключения при рендере.
            Assert.Contains("media.idchess.com", cut.Markup);

            // Сохранение: путь grain должен пройти без исключения и сохранить URL как есть.
            await cut.Find("form").SubmitAsync();

            var grain = cluster.GrainFactory.GetGrain<IBrandTournamentsGrain>(0);
            var all = await grain.GetAllAsync();
            Assert.Single(all);
            Assert.Equal(ProblemUrl, all[0].ImageUrl);
            Assert.StartsWith("shakhmatnyy-turnir-bristol-", all[0].Slug); // транслит + дата
        }
        finally { await cluster.StopAllSilosAsync(); }
    }
}
