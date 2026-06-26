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

    window.arenaGamesSetup = setup;
    document.addEventListener('DOMContentLoaded', setup);
    document.addEventListener('enhancedload', setup);
})();
