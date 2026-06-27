namespace ChessSchool.Arena;

/// <summary>
/// Бренд-турнир — кураторское именованное событие со стабильным slug (URL /t/{slug}), в отличие от
/// регулярных эфемерных турниров расписания. Управляется из админки; индексируется и выводится в
/// «Главных турнирах» (лента/таймлайн/список). Грейн турнира конфигурируется из этих полей
/// (см. ConfigureBrandAsync). Изменяемый класс с Orleans-сериализацией (хранится в grain storage).
/// </summary>
[GenerateSerializer]
public sealed class BrandTournament
{
    [Id(0)] public string Slug { get; set; } = "";
    [Id(1)] public string Name { get; set; } = "";
    [Id(2)] public string Description { get; set; } = "";
    [Id(3)] public string? ImageUrl { get; set; }
    [Id(4)] public int InitialSeconds { get; set; } = 180; // контроль времени (база)
    [Id(5)] public int IncrementSeconds { get; set; }
    [Id(6)] public DateTimeOffset StartsAt { get; set; }
    [Id(7)] public int DurationSeconds { get; set; } = 3600;
    [Id(8)] public bool Visible { get; set; } = true;

    public BrandTournament Clone() => (BrandTournament)MemberwiseClone();
}

/// <summary>Персистентное состояние каталога бренд-турниров (единственный грейн, ключ 0).</summary>
[GenerateSerializer]
public sealed class BrandTournamentsState
{
    [Id(0)] public List<BrandTournament> Items { get; set; } = [];
}

/// <summary>Бренд-турнир + его живая сводка из грейна (для ленты/таймлайна/списка на Home).</summary>
public sealed record BrandTournamentView(BrandTournament Brand, ChessSchool.Contracts.TournamentSummaryDto Summary);
