using System.Globalization;
using Microsoft.AspNetCore.Components;

namespace ChessSchool.Arena.Components;

/// <summary>
/// База для интерактивных компонентов, которые ре-рендерятся из фоновых источников (таймеры,
/// Orleans-нотификации). Такие колбэки выполняются на потоках с дефолтной культурой потока (en),
/// а не с культурой запроса (её ставит RequestLocalization при инициализации компонента). Из-за
/// этого <see cref="Loc"/> на фоновом рендере показывал бы английский, и локализация «прыгала» ru↔en.
/// Захватываем культуру при инициализации и восстанавливаем её на каждом фоновом ре-рендере.
/// </summary>
public abstract class LocalizedComponentBase : ComponentBase
{
    private CultureInfo _culture = CultureInfo.CurrentCulture;
    private CultureInfo _uiCulture = CultureInfo.CurrentUICulture;

    protected override void OnInitialized()
    {
        // Здесь культура ещё корректна (унаследована от запроса/контура) — запоминаем её.
        _culture = CultureInfo.CurrentCulture;
        _uiCulture = CultureInfo.CurrentUICulture;
    }

    /// <summary>
    /// Ре-рендер из фонового колбэка с восстановлением культуры запроса на потоке рендера.
    /// Использовать вместо <c>InvokeAsync(StateHasChanged)</c> в таймерах/нотификациях.
    /// </summary>
    protected Task InvokeStateHasChangedLocalizedAsync() => InvokeAsync(() =>
    {
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
        StateHasChanged();
    });
}
