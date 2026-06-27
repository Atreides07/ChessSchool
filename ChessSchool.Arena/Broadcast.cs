namespace ChessSchool.Arena;

/// <summary>
/// Трансляция шахматного события (страницы /broadcasts и /broadcasts/{slug}, карточки на сайте).
/// Контент управляется из админки (CRUD + скрыть/показать) и хранится в Orleans grain storage,
/// поэтому это изменяемый класс с Orleans-сериализацией (не record). <see cref="Slug"/> — стабильный
/// идентификатор и часть URL. <see cref="ImageUrl"/> — ссылка на фон с официального источника
/// (вставляет редактор; файлы не скачиваются и не вшиваются в репозиторий).
/// </summary>
[GenerateSerializer]
public sealed class Broadcast
{
    [Id(0)] public string Slug { get; set; } = "";
    [Id(1)] public string Name { get; set; } = "";
    [Id(2)] public string Series { get; set; } = "";
    [Id(3)] public string SeriesCls { get; set; } = "cls"; // gct | cls | wom — стиль чипа серии
    [Id(4)] public DateOnly Start { get; set; }
    [Id(5)] public DateOnly End { get; set; }
    [Id(6)] public string City { get; set; } = "";
    [Id(7)] public string Country { get; set; } = "";
    [Id(8)] public string Flag { get; set; } = "";
    [Id(9)] public string Format { get; set; } = "";
    [Id(10)] public string Url { get; set; } = "";
    [Id(11)] public string? ImageUrl { get; set; }
    [Id(12)] public bool Visible { get; set; } = true;

    public Broadcast Clone() => (Broadcast)MemberwiseClone();
}

/// <summary>Персистентное состояние каталога трансляций (единственный грейн, ключ 0).</summary>
[GenerateSerializer]
public sealed class BroadcastsState
{
    [Id(0)] public List<Broadcast> Items { get; set; } = [];
    /// <summary>Каталог уже инициализирован стартовым набором (чтобы сид не перетирал правки админа).</summary>
    [Id(1)] public bool Seeded { get; set; }
}

/// <summary>
/// Стартовый набор трансляций (топ-события сезона). Заливается в каталог один раз при первой
/// активации грейна; дальше контентом управляет админка. Изображения намеренно пустые — их
/// добавляет редактор ссылкой с официального сайта (см. <see cref="Broadcast.ImageUrl"/>).
/// </summary>
public static class BroadcastSeed
{
    public static IReadOnlyList<Broadcast> Initial =>
    [
        new() { Slug = "superunited-rapid-blitz-croatia-2026", Name = "SuperUnited Rapid & Blitz Croatia", Series = "Grand Chess Tour", SeriesCls = "gct", Start = new(2026, 6, 29), End = new(2026, 7, 6), City = "Загреб", Country = "Хорватия", Flag = "🇭🇷", Format = "Рапид и блиц", Url = "https://grandchesstour.org", ImageUrl = "https://grandchesstour.org/wp-content/uploads/2025/02/2025-GCT-Croatia-Rapid-and-Blitz-Day-1-Photo-1-767x434.webp" },
        new() { Slug = "biel-chess-festival-2026", Name = "Biel Chess Festival", Series = "Классика", SeriesCls = "cls", Start = new(2026, 7, 11), End = new(2026, 7, 24), City = "Биль", Country = "Швейцария", Flag = "🇨🇭", Format = "Классика · 2700+", Url = "https://www.bielchessfestival.ch", ImageUrl = "https://en.chessbase.com/portals/all/2026/04/Biel/jcr_content.jpg" },
        new() { Slug = "chennai-grand-masters-2026", Name = "Chennai Grand Masters", Series = "Классика", SeriesCls = "cls", Start = new(2026, 7, 15), End = new(2026, 7, 23), City = "Ченнаи", Country = "Индия", Flag = "🇮🇳", Format = "Классика · круговой", Url = "https://chennaigrandmasters.com", ImageUrl = "https://chennaigrandmasters.com/assets/images/season2026.png" },
        new() { Slug = "saint-louis-rapid-blitz-2026", Name = "Saint Louis Rapid & Blitz", Series = "Grand Chess Tour", SeriesCls = "gct", Start = new(2026, 7, 31), End = new(2026, 8, 7), City = "Сент-Луис", Country = "США", Flag = "🇺🇸", Format = "Рапид и блиц", Url = "https://grandchesstour.org", ImageUrl = "https://grandchesstour.org/wp-content/uploads/2025/03/2025-Saint-Louis-Rapid-Blitz-web-banner-2-767x573.webp" },
        new() { Slug = "sinquefield-cup-2026", Name = "Sinquefield Cup", Series = "Grand Chess Tour", SeriesCls = "gct", Start = new(2026, 8, 8), End = new(2026, 8, 21), City = "Сент-Луис", Country = "США", Flag = "🇺🇸", Format = "Классика · элита", Url = "https://grandchesstour.org", ImageUrl = "https://grandchesstour.org/wp-content/uploads/2025/02/photo-2025-Sinquefield-Cup-Day-1-DSC_1915-767x434.webp" },
        new() { Slug = "cairns-cup-2026", Name = "Cairns Cup", Series = "Женский элит", SeriesCls = "wom", Start = new(2026, 8, 8), End = new(2026, 8, 21), City = "Сент-Луис", Country = "США", Flag = "🇺🇸", Format = "Классика · круговой", Url = "https://saintlouischessclub.org", ImageUrl = "https://saintlouischessclub.org/wp-content/uploads/2025/02/2025-Cairns-Cup-Web-Banner-1400.webp" },
        new() { Slug = "gct-finals-2026", Name = "GCT Finals", Series = "Grand Chess Tour", SeriesCls = "gct", Start = new(2026, 8, 21), End = new(2026, 8, 28), City = "Сент-Луис", Country = "США", Flag = "🇺🇸", Format = "Плей-офф · топ-4", Url = "https://grandchesstour.org", ImageUrl = "https://grandchesstour.org/wp-content/uploads/2024/12/2025-GCT-BGT-1230x640.webp" },
    ];
}
