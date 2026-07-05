using ChessSchool.Arena.Grains;
using ChessSchool.Arena.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.TestingHost;

namespace ChessSchool.Tests;

/// <summary>
/// Настройки числа ботов по типам регулярных игр (для админки): дефолт, сохранение, валидация типа,
/// зажим диапазона. Хранилище «arena» — как у остальных грейнов арены.
/// </summary>
public class BotSettingsGrainTests
{
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
                s.AddSingleton<ArenaTelemetry>();
                s.AddSingleton(TimeProvider.System);
            });
        }
    }

    [Fact]
    public async Task Defaults_SetSave_Validate_Clamp()
    {
        var cluster = new TestClusterBuilder().AddSiloBuilderConfigurator<SiloConfigurator>().Build();
        await cluster.DeployAsync();
        try
        {
            var g = cluster.GrainFactory.GetGrain<IBotSettingsGrain>(0);

            // По умолчанию — для каждого типа расписания дефолтное число.
            var all = await g.GetAllAsync();
            foreach (var spec in ArenaSchedule.Series)
            {
                Assert.True(all.ContainsKey(spec.Type));
                Assert.Equal(BotSettingsGrain.DefaultCount, all[spec.Type]);
            }
            Assert.Equal(BotSettingsGrain.DefaultCount, await g.GetCountAsync("Blitz"));

            // Сохранение и чтение.
            await g.SetCountAsync("Blitz", 3);
            Assert.Equal(3, await g.GetCountAsync("Blitz"));

            // Зажим диапазона [0..50].
            await g.SetCountAsync("Bullet", 999);
            Assert.Equal(50, await g.GetCountAsync("Bullet"));
            await g.SetCountAsync("Rapid", -7);
            Assert.Equal(0, await g.GetCountAsync("Rapid"));

            // Тип вне расписания: чтение → 0, запись → ошибка.
            Assert.Equal(0, await g.GetCountAsync("Nonsense"));
            await Assert.ThrowsAsync<ArgumentException>(() => g.SetCountAsync("Nonsense", 5));
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    [Fact]
    public async Task SettingPersists_AcrossReactivation()
    {
        var cluster = new TestClusterBuilder().AddSiloBuilderConfigurator<SiloConfigurator>().Build();
        await cluster.DeployAsync();
        try
        {
            await cluster.GrainFactory.GetGrain<IBotSettingsGrain>(0).SetCountAsync("Rapid", 4);
            await cluster.GrainFactory.GetGrain<IManagementGrain>(0).ForceActivationCollection(TimeSpan.Zero);

            Assert.Equal(4, await cluster.GrainFactory.GetGrain<IBotSettingsGrain>(0).GetCountAsync("Rapid"));
        }
        finally { await cluster.StopAllSilosAsync(); }
    }
}
