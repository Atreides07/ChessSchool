using ChessSchool.Contracts;
using Orleans.Concurrency;
using Color = ChessSchool.Contracts.PieceColor;

namespace ChessSchool.GameServer.Grains;

/// <summary>
/// Сводит ждущих игроков в пары. [Reentrant] — пока первый игрок «висит» в ожидании,
/// вызов второго игрока исполняется и завершает ожидание первого через TaskCompletionSource.
/// </summary>
[Reentrant]
public sealed class MatchmakingGrain(IGrainFactory grains) : Grain, IMatchmakingGrain
{
    private readonly record struct Waiting(MatchRequest Req, TaskCompletionSource<MatchFound> Tcs);

    private readonly Queue<Waiting> _waiting = new();

    public async Task<MatchFound> FindMatchAsync(MatchRequest request)
    {
        // Убираем из очереди отменённых/протухших и повторные заявки того же пользователя.
        while (_waiting.Count > 0 &&
               (_waiting.Peek().Tcs.Task.IsCompleted || _waiting.Peek().Req.UserId == request.UserId))
        {
            _waiting.Dequeue();
        }

        if (_waiting.Count > 0)
        {
            var opponent = _waiting.Dequeue();
            var gameId = Guid.NewGuid().ToString("N");

            // Ждущий получает белые, текущий — чёрные.
            var white = opponent.Req;
            var black = request;

            var game = grains.GetGrain<IGameGrain>(gameId);
            await game.InitializeAsync(white.UserId, white.DisplayName, black.UserId, black.DisplayName, request.TimeControl);

            opponent.Tcs.TrySetResult(
                new MatchFound(gameId, Color.White, black.UserId, black.DisplayName, black.Rating, request.TimeControl));

            return new MatchFound(gameId, Color.Black, white.UserId, white.DisplayName, white.Rating, request.TimeControl);
        }

        // Соперника нет — встаём в очередь и ждём (с тайм-аутом).
        var tcs = new TaskCompletionSource<MatchFound>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting.Enqueue(new Waiting(request, tcs));
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }
}
