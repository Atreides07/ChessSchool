using ChessSchool.Arena.Grains;
using ChessSchool.Contracts;

namespace ChessSchool.Arena.Services;

/// <summary>
/// Детерминированно «проигрывает» завершённый демо-турнир (сид от id): пейринг по очкам, исходы взвешены
/// силой игроков, начисление — строго по <see cref="ArenaScoring"/>. Чистая функция (без Orleans/времени) →
/// тестируется отдельно. Вынесено из ArenaTournamentGrain (design-review #2): демо-фикстура — отдельная
/// ответственность, не смешанная с боевой оркестрацией турнира.
/// </summary>
public static class ArenaFinishedSimulator
{
    // Состав завершённых турниров: имя + «сила» (влияет на вероятность победы) + признак бота.
    private static readonly (string Name, double Strength, bool Bot)[] Roster =
    [
        ("ArenaHost_0", 1.35, false), ("Zugzwang_42", 1.30, false), ("Leela_Zero", 1.20, true),
        ("French_Winawer", 1.10, false), ("DeepBlue_v2", 1.05, true), ("Morphy_Machine", 1.00, false),
        ("Stockfish_15", 0.95, true), ("Komodo_X", 0.90, true), ("Tal_Tactics", 0.85, false),
        ("Rook_Rampage", 0.80, false), ("Endgame_Esra", 0.75, false), ("Fritz_9", 0.70, true),
    ];

    /// <summary>Финальная таблица завершённого турнира (8..12 игроков), детерминированная по <paramref name="id"/>.</summary>
    public static List<PersistedPlayer> Build(string id, TimeControl tc, int durationSeconds)
    {
        int seed = 17;
        foreach (var ch in id) seed = unchecked(seed * 31 + ch) & 0x7fffffff;
        var rng = new Random(seed);

        int count = 8 + rng.Next(0, 5); // 8..12 участников
        var roster = Roster.OrderBy(_ => rng.Next()).Take(count).ToList();
        var players = new Dictionary<string, PersistedPlayer>();
        var strength = new Dictionary<string, double>();
        foreach (var (name, str, bot) in roster)
        {
            players[name] = new PersistedPlayer { Key = name, Name = name, IsBot = bot };
            strength[name] = str * (0.85 + rng.NextDouble() * 0.3); // лёгкий разброс формы
        }

        // Число туров оцениваем по длительности и средней партии данного контроля.
        int avgGameSec = Math.Max(45, tc.InitialSeconds + tc.IncrementSeconds * 20);
        int rounds = Math.Clamp(durationSeconds / Math.Max(30, avgGameSec / 4), 8, 22);

        for (int r = 0; r < rounds; r++)
        {
            // Пейринг по очкам (как на lichess), внутри равных очков — случайно.
            var order = players.Values.OrderByDescending(p => p.Score).ThenBy(_ => rng.Next()).ToList();
            for (int i = 0; i + 1 < order.Count; i += 2)
            {
                var a = order[i];
                var b = order[i + 1];
                double pa = strength[a.Name], pb = strength[b.Name];
                if (rng.NextDouble() < 0.18) { Award(a, 0.5); Award(b, 0.5); } // ничья
                else if (rng.NextDouble() * (pa + pb) < pa) { Award(a, 1.0); Award(b, 0.0); }
                else { Award(a, 0.0); Award(b, 1.0); }
            }
        }
        return players.Values.ToList();
    }

    // Начисление строго как в грейне: очки/серия через ArenaScoring, плюс счётчики и история результатов.
    private static void Award(PersistedPlayer p, double outcome)
    {
        var before = p.Score;
        (p.Score, p.Streak) = ArenaScoring.Apply(p.Score, p.Streak, outcome);
        p.Games++;
        if (outcome == 1.0) p.Wins++;
        p.Results.Add(p.Score - before); // 0 — поражение, 1/2 — ничья, 2/4 — победа (×2 на огне)
    }
}
