namespace ChessSchool.Arena.Services;

/// <summary>
/// Рантайм-переключатели силоса арены, зависящие от окружения (наличия Redis).
/// <paramref name="RemindersEnabled"/> — настроен ли распределённый reminder-сервис (Redis): только тогда
/// грейн турнира регистрирует reminder-«воскрешение» (без сервиса вызов reminder-API бросил бы исключение).
/// </summary>
public sealed record ArenaRuntimeOptions(bool RemindersEnabled);
