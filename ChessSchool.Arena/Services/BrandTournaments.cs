namespace ChessSchool.Arena.Services;

/// <summary>Минимум для ссылки на бренд-турнир в sitemap (slug = часть URL /t/{slug}).</summary>
public sealed record BrandTournamentRef(string Slug);

/// <summary>
/// Решает, является ли турнир «брендовым» — кураторским индексируемым событием со стабильным slug,
/// в отличие от регулярных эфемерных турниров расписания (id по времени), которые не индексируются.
///
/// Точка расширения: сейчас брендов нет (<see cref="NoBrandTournaments"/> → все /t/{id} остаются
/// noindex, sitemap без турниров — поведение как сегодня). Когда заведём бренд-турниры, реализацию
/// подменяет каталог (грейн + admin-CRUD), и бренд-страницы начинают индексироваться/попадать в
/// sitemap БЕЗ правок страницы турнира и эндпоинта sitemap. Решение — по наличию в каталоге, а не по
/// формату id (надёжно).
/// </summary>
public interface IBrandTournaments
{
    /// <summary>Бренд-турнир (индексируемый)? Регулярные турниры расписания → false.</summary>
    Task<bool> IsBrandAsync(string id);

    /// <summary>Видимые бренд-турниры для sitemap. Регулярные сюда не попадают никогда.</summary>
    Task<IReadOnlyList<BrandTournamentRef>> ListIndexableAsync();
}

/// <summary>Бренд-турниров пока нет: всё /t/{id} остаётся noindex, sitemap без турниров.</summary>
public sealed class NoBrandTournaments : IBrandTournaments
{
    public Task<bool> IsBrandAsync(string id) => Task.FromResult(false);

    public Task<IReadOnlyList<BrandTournamentRef>> ListIndexableAsync() =>
        Task.FromResult<IReadOnlyList<BrandTournamentRef>>([]);
}
