using System.Diagnostics;

namespace ChessSchool.Arena.Services;

/// <summary>Шахматный движок: возвращает лучший ход в UCI-нотации (например, "e2e4", "e7e8q").</summary>
public interface IChessEngine
{
    Task<string?> GetBestMoveAsync(string fen, int skillLevel, int moveTimeMs, CancellationToken ct = default);
}

/// <summary>
/// Серверная обёртка над Stockfish по протоколу UCI. Один процесс, запросы сериализуются семафором.
/// Если бинарь недоступен — помечает движок недоступным и возвращает null (вызывающий ходит случайно).
/// Путь к движку: конфиг Engine:Path (по умолчанию "stockfish" в PATH).
/// </summary>
public sealed class StockfishEngine(IConfiguration config, ILogger<StockfishEngine> log) : IChessEngine, IAsyncDisposable
{
    private readonly string _path = config["Engine:Path"] ?? "stockfish";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _proc;
    private bool _unavailable;

    public async Task<string?> GetBestMoveAsync(string fen, int skillLevel, int moveTimeMs, CancellationToken ct = default)
    {
        if (_unavailable) return null;
        await _gate.WaitAsync(ct);
        try
        {
            if (!await EnsureStartedAsync(ct)) return null;
            var p = _proc!;

            await SendAsync(p, $"setoption name Skill Level value {Math.Clamp(skillLevel, 0, 20)}");
            await SendAsync(p, $"position fen {fen}");
            await SendAsync(p, $"go movetime {moveTimeMs}");

            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync(ct)) != null)
            {
                if (!line.StartsWith("bestmove", StringComparison.Ordinal)) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var move = parts.Length > 1 ? parts[1] : null;
                return move is null or "(none)" ? null : move;
            }
            return null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Сбой Stockfish — переключаюсь на случайные ходы ботов.");
            _unavailable = true;
            return null;
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> EnsureStartedAsync(CancellationToken ct)
    {
        if (_proc is { HasExited: false }) return true;

        // Пробуем сконфигурированный путь и типичные расположения (PATH под Aspire может отличаться).
        string[] candidates =
        [
            _path,
            "/opt/homebrew/bin/stockfish",
            "/usr/local/bin/stockfish",
            "/usr/games/stockfish",
            "/usr/bin/stockfish"
        ];

        foreach (var path in candidates.Distinct())
        {
            try
            {
                var proc = Process.Start(new ProcessStartInfo(path)
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                });
                if (proc is null) continue;

                _proc = proc;
                await SendAsync(proc, "uci");
                await WaitForAsync(proc, "uciok", ct);
                await SendAsync(proc, "isready");
                await WaitForAsync(proc, "readyok", ct);
                log.LogInformation("Stockfish запущен ({Path}).", path);
                return true;
            }
            catch
            {
                // пробуем следующий путь
            }
        }

        log.LogWarning("Stockfish не найден — боты будут ходить случайно. Установите: brew install stockfish.");
        _unavailable = true;
        return false;
    }

    private static async Task SendAsync(Process p, string command)
    {
        await p.StandardInput.WriteLineAsync(command);
        await p.StandardInput.FlushAsync();
    }

    private static async Task WaitForAsync(Process p, string token, CancellationToken ct)
    {
        string? line;
        while ((line = await p.StandardOutput.ReadLineAsync(ct)) != null)
            if (line.StartsWith(token, StringComparison.Ordinal)) return;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_proc is { HasExited: false })
            {
                await SendAsync(_proc, "quit");
                if (!_proc.WaitForExit(1000)) _proc.Kill(entireProcessTree: true);
            }
        }
        catch { /* best-effort */ }
        _proc?.Dispose();
        _gate.Dispose();
    }
}
