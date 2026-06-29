namespace ChessSchool.Arena.Services;

/// <summary>Кандидат-человек в пуле подбора: ключ и момент начала ожидания (для грейс-периода до бота).</summary>
public readonly record struct SeekingHuman(string Key, DateTimeOffset? WaitingSince);

/// <summary>
/// План подбора пар: какие существующие игроки сводятся в партии (<see cref="Pairs"/>) и каким людям нужно
/// подключить СВЕЖЕГО бота (<see cref="HumansNeedingNewBot"/>) — грейн его создаст и спарит. Чистый результат
/// без побочных эффектов: создание партий/ботов выполняет грейн.
/// </summary>
public readonly record struct ArenaPairingPlan(
    IReadOnlyList<(string A, string B)> Pairs,
    IReadOnlyList<string> HumansNeedingNewBot);

/// <summary>
/// Алгоритм подбора пар арены (как на lichess), вынесенный из грейна как чистая функция. Приоритеты:
/// 1) человек+человек мгновенно; 2) ждущий человек после грейс-периода получает бота (свободного, иначе
/// свежего, если боты включены); 3) свободные боты играют между собой (живость арены).
/// </summary>
public static class ArenaPairing
{
    /// <param name="idleHumans">ищущие люди, УЖЕ упорядоченные (грейн сортирует по очкам).</param>
    /// <param name="idleBots">свободные боты (ключи).</param>
    /// <param name="waitForBotSeconds">сколько человек ждёт человека, прежде чем подключить бота.</param>
    /// <param name="botsEnabled">разрешено ли создавать новых ботов (таргет &gt; 0).</param>
    public static ArenaPairingPlan Plan(
        IReadOnlyList<SeekingHuman> idleHumans,
        IReadOnlyList<string> idleBots,
        DateTimeOffset now,
        int waitForBotSeconds,
        bool botsEnabled)
    {
        var pairs = new List<(string, string)>();
        var newBotHumans = new List<string>();

        // 1) Человек с человеком — мгновенно (приоритет живым соперникам).
        int hi = 0;
        while (hi + 1 < idleHumans.Count)
        {
            pairs.Add((idleHumans[hi].Key, idleHumans[hi + 1].Key));
            hi += 2;
        }

        // 2) Оставшийся человек ждёт; после грейс-периода — бот (свободный, иначе свежий, если включены).
        int bi = 0;
        for (; hi < idleHumans.Count; hi++)
        {
            var h = idleHumans[hi];
            if (h.WaitingSince is not { } since || (now - since).TotalSeconds < waitForBotSeconds)
                continue; // ещё ищем — оставляем «Ищем соперника…»
            if (bi < idleBots.Count) pairs.Add((h.Key, idleBots[bi++]));
            else if (botsEnabled) newBotHumans.Add(h.Key);
            // иначе боты отключены и свободных нет — человек продолжает ждать
        }

        // 3) Свободные боты играют между собой, не занимая место будущего соперника.
        for (; bi + 1 < idleBots.Count; bi += 2)
            pairs.Add((idleBots[bi], idleBots[bi + 1]));

        return new ArenaPairingPlan(pairs, newBotHumans);
    }
}
