using ChessSchool.Contracts;
using Orleans.Concurrency;
using Color = ChessSchool.Contracts.PieceColor;

namespace ChessSchool.GameServer.Grains;

/// <summary>Настройки матчмейкинга. Таймаут ожидания соперника — из конфига (Matchmaking:WaitTimeoutSeconds),
/// чтобы менять под нагрузку без пересборки образа. Дефолт — 60 секунд.</summary>
public sealed record MatchmakingOptions(TimeSpan WaitTimeout)
{
    public static readonly MatchmakingOptions Default = new(TimeSpan.FromSeconds(60));
}

/// <summary>
/// Сводит ждущих игроков в пары. [Reentrant] — пока первый игрок «висит» в ожидании,
/// вызов второго игрока исполняется и завершает ожидание первого через TaskCompletionSource.
/// </summary>
[Reentrant]
public sealed class MatchmakingGrain(IGrainFactory grains, MatchmakingOptions options) : Grain, IMatchmakingGrain
{
    private readonly record struct Waiting(MatchRequest Req, TaskCompletionSource<MatchFound> Tcs);

    private readonly Queue<Waiting> _waiting = new();

    public async Task<MatchFound> FindMatchAsync(MatchRequest request)
    {
        // Ищем первого ЖИВОГО ждущего соперника. Протухших (ожидание истекло по таймауту → Tcs отменён)
        // и повторную заявку того же пользователя выкидываем. Снимаем с очереди СРАЗУ (до await ниже):
        // грейн реентрантный, и так параллельный вызов не спарится с тем же ждущим дважды.
        while (_waiting.Count > 0)
        {
            var opponent = _waiting.Dequeue();
            if (opponent.Tcs.Task.IsCompleted || opponent.Req.UserId == request.UserId)
                continue; // мёртвая/протухшая/своя заявка — мимо

            var gameId = Guid.NewGuid().ToString("N");
            var white = opponent.Req;   // ждущий получает белые
            var black = request;        // текущий — чёрные

            var game = grains.GetGrain<IGameGrain>(gameId);
            await game.InitializeAsync(white.UserId, white.DisplayName, black.UserId, black.DisplayName, request.TimeControl);

            // Ждущий мог отвалиться за время инициализации (таймаут/отмена) — тогда TrySetResult вернёт
            // false: не отдаём текущему игроку партию-призрак, ищем следующего соперника.
            if (!opponent.Tcs.TrySetResult(
                    new MatchFound(gameId, Color.White, black.UserId, black.DisplayName, black.Rating, request.TimeControl)))
                continue;

            return new MatchFound(gameId, Color.Black, white.UserId, white.DisplayName, white.Rating, request.TimeControl);
        }

        // Соперника нет — встаём в очередь и ждём (с тайм-аутом). По таймауту ОТМЕНЯЕМ свою заявку,
        // чтобы следующий искатель не спарился с уже ушедшим игроком (Tcs.IsCompleted → вычистится выше).
        var tcs = new TaskCompletionSource<MatchFound>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting.Enqueue(new Waiting(request, tcs));
        try
        {
            return await tcs.Task.WaitAsync(options.WaitTimeout);
        }
        catch (TimeoutException)
        {
            tcs.TrySetCanceled();
            throw;
        }
    }
}
