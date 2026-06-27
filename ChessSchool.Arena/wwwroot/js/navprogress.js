// Полоса прогресса вверху на время enhanced-навигации Blazor. На медленном канале переход
// (fetch новой страницы) занимает время, а индикатора у Blazor нет → страница выглядит «зависшей».
// Стартуем по клику на внутреннюю ссылку, завершаем по событию enhancedload. Скрипт глобальный
// (App.razor), идемпотентный, переживает навигацию.
(function () {
    if (window.__navProg) return;
    window.__navProg = true;

    var bar = null, active = false, safety = null;

    function ensureBar() {
        if (bar && document.body.contains(bar)) return bar;
        bar = document.createElement('div');
        bar.id = 'nav-progress';
        document.body.appendChild(bar);
        return bar;
    }

    function start() {
        if (active) return;
        active = true;
        var b = ensureBar();
        b.style.transition = 'none';
        b.style.opacity = '1';
        b.style.width = '0';
        void b.offsetWidth; // reflow, чтобы сброс ширины применился до анимации
        b.style.transition = 'width 2.2s cubic-bezier(.1,.6,.3,1), opacity .3s';
        b.style.width = '88%';
        clearTimeout(safety);
        safety = setTimeout(done, 12000); // страховка: не оставлять полосу висеть, если событие не пришло
    }

    function done() {
        clearTimeout(safety);
        if (!active) return;
        active = false;
        var b = ensureBar();
        b.style.width = '100%';
        setTimeout(function () {
            b.style.opacity = '0';
            setTimeout(function () { b.style.transition = 'none'; b.style.width = '0'; }, 300);
        }, 150);
    }

    // Ссылка, которую Blazor обработает enhanced-навигацией (внутренняя, обычный левый клик).
    function isNavLink(a) {
        if (!a || a.target === '_blank' || a.hasAttribute('download')) return false;
        var href = a.getAttribute('href');
        if (!href || href.charAt(0) === '#') return false;
        var url;
        try { url = new URL(a.href, location.href); } catch (e) { return false; }
        if (url.origin !== location.origin) return false;
        if (url.pathname === location.pathname && url.hash) return false; // якорь на той же странице
        return true;
    }

    document.addEventListener('click', function (e) {
        if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
        var a = e.target.closest ? e.target.closest('a[href]') : null;
        if (isNavLink(a)) start();
    }, true);

    document.addEventListener('enhancedload', done); // enhanced-навигация завершилась
    window.addEventListener('pageshow', done);       // полная загрузка/возврат из bfcache
})();
