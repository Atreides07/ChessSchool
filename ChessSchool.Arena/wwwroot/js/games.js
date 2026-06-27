// Ленивая подгрузка досок на странице «Все игры»: когда сентинел появляется в зоне
// видимости при скролле, «кликаем» скрытую кнопку «Показать ещё» (её обрабатывает Blazor).
(function () {
    function setup() {
        var sentinel = document.getElementById('games-sentinel');
        var more = document.getElementById('games-more');
        if (sentinel && sentinel._io) { sentinel._io.disconnect(); sentinel._io = null; }
        if (!sentinel || !more) return;

        var io = new IntersectionObserver(function (entries) {
            entries.forEach(function (e) { if (e.isIntersecting) more.click(); });
        }, { rootMargin: '400px 0px' });
        io.observe(sentinel);
        sentinel._io = io;
    }

    // Старт при появлении сентинела в DOM: enhanced-навигация Blazor не исполняет вставленный <script>
    // и не всегда шлёт enhancedload — поэтому ловим вставку узла. Скрипт глобальный (App.razor).
    function watchForRoot() {
        if (window.__gRootObserver) return;
        var relevant = function (n) {
            return n.nodeType === 1 && (n.matches && n.matches('#games-sentinel') || n.querySelector && n.querySelector('#games-sentinel'));
        };
        window.__gRootObserver = new MutationObserver(function (records) {
            if (!records.some(function (r) { return Array.from(r.addedNodes).some(relevant); })) return;
            if (window.__gBootRaf) return;
            window.__gBootRaf = requestAnimationFrame(function () { window.__gBootRaf = 0; setup(); });
        });
        window.__gRootObserver.observe(document.documentElement, { childList: true, subtree: true });
    }

    window.arenaGamesSetup = setup;
    watchForRoot();
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', setup);
    else setup();
    document.addEventListener('enhancedload', setup);
})();
