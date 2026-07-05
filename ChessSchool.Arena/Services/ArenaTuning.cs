namespace ChessSchool.Arena.Services;

/// <summary>
/// Тюнинг-параметры арена-турнира в одном месте (design-review #5): раньше — разрозненные const/магические
/// числа в грейне. Дефолты подобраны под lichess-подобный опыт; при необходимости выносятся в IOptions/конфиг
/// без изменения грейна.
/// </summary>
public static class ArenaTuning
{
    /// <summary>Сколько секунд человек ждёт соперника-человека, прежде чем к нему подключат бота.</summary>
    public const int WaitForBotSeconds = 10;

    /// <summary>Хвост показа завершённой партии БЕЗ живого участника (зрители успевают увидеть финал), сек.</summary>
    public const int FinishedLingerSeconds = 6;

    /// <summary>Период тика турнира (ведёт часы/пейринг), мс.</summary>
    public const int TimerCadenceMs = 500;

    /// <summary>Период reminder-«воскрешения» грейна на другой ноде (минимум Orleans), мин.</summary>
    public const int ReminderMinutes = 1;

    /// <summary>Держим грейн активным, пока идёт турнир, мин.</summary>
    public const int KeepAliveMinutes = 10;

    /// <summary>Диапазон бюджета времени на ход движка-бота, мс.</summary>
    public const int EngineBudgetMinMs = 100;
    public const int EngineBudgetMaxMs = 450;
}
