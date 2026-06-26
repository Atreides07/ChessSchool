// Прогрессивное улучшение таймлайна расписания: now-линия по реальному времени,
// зум 1ч/4ч/12ч и скролл «сейчас»/стрелками. Страница — статический SSR, поэтому
// логика времени и интерактив живут здесь (а не в Blazor).
(function () {
    const HALF = 1800; // секунд в полу-часовой колонке
    let currentZoom = 4;

    function gutterWidth(grid) {
        const g = grid.querySelector('.tl-gutter');
        return g ? g.offsetWidth : 132;
    }

    function colWidth(grid) {
        const v = getComputedStyle(grid).getPropertyValue('--colw');
        const n = parseFloat(v);
        return isNaN(n) ? 92 : n;
    }

    function positionNow(grid) {
        const line = grid.querySelector('[data-tl-nowline]');
        const label = grid.querySelector('[data-tl-nowlabel]');
        if (!line) return;

        const start = parseInt(grid.getAttribute('data-start'), 10);
        const cols = parseInt(getComputedStyle(grid).getPropertyValue('--cols'), 10) || 24;
        const now = Date.now() / 1000;
        const end = start + cols * HALF;

        if (isNaN(start) || now < start || now > end) { line.style.display = 'none'; return; }

        const left = gutterWidth(grid) + ((now - start) / HALF) * colWidth(grid);
        line.style.left = left + 'px';
        line.style.display = 'block';
        if (label) {
            const d = new Date();
            label.textContent = String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0');
        }
    }

    function applyZoom(grid, area, hours) {
        currentZoom = hours;
        const avail = area.clientWidth - gutterWidth(grid);
        let colw = avail / (hours * 2);
        colw = Math.max(34, Math.min(360, colw)); // в разумных пределах
        grid.style.setProperty('--colw', colw + 'px');
        positionNow(grid);
    }

    function scrollToNow(grid, area, center) {
        const start = parseInt(grid.getAttribute('data-start'), 10);
        const now = Date.now() / 1000;
        if (isNaN(start)) return;
        const x = gutterWidth(grid) + ((now - start) / HALF) * colWidth(grid);
        const target = center ? x - area.clientWidth / 2 : x - gutterWidth(grid) - 20;
        area.scrollTo({ left: Math.max(0, target), behavior: 'smooth' });
    }

    // Пересчёт ширины колонок, линии времени и прокрутки к «сейчас» (когда таймлайн виден).
    function refresh(grid, area) {
        applyZoom(grid, area, currentZoom);
        positionNow(grid);
        scrollToNow(grid, area, true);
    }

    function init() {
        const grid = document.querySelector('[data-tl-grid]');
        const area = document.querySelector('[data-tl-scrollarea]');
        if (!grid || !area || grid.dataset.tlReady === '1') return;
        grid.dataset.tlReady = '1';

        // Зум-кнопки 1ч/4ч/12ч
        const zoomBtns = Array.from(document.querySelectorAll('[data-tl-zoom]'));
        zoomBtns.forEach(btn => btn.addEventListener('click', () => {
            const h = parseInt(btn.getAttribute('data-tl-zoom'), 10);
            zoomBtns.forEach(b => b.classList.toggle('is-active', b === btn));
            applyZoom(grid, area, h);
            scrollToNow(grid, area, true);
        }));

        // «сейчас» и стрелки
        const nowBtn = document.querySelector('[data-tl-now]');
        if (nowBtn) nowBtn.addEventListener('click', () => scrollToNow(grid, area, true));
        document.querySelectorAll('[data-tl-scroll]').forEach(btn => btn.addEventListener('click', () => {
            const dir = parseInt(btn.getAttribute('data-tl-scroll'), 10);
            area.scrollBy({ left: dir * colWidth(grid) * 2, behavior: 'smooth' }); // на 1 час
        }));

        // Возврат к виду «Таймлайн» (переключатель Список↔Таймлайн): таймлайн был скрыт
        // (нулевая ширина) → пересчитываем колонки и заново ставим линию времени.
        document.querySelectorAll('input[name="schedview"]').forEach(r => r.addEventListener('change', () => {
            if (document.getElementById('sv-tl')?.checked) requestAnimationFrame(() => refresh(grid, area));
        }));

        // Старт: масштаб 4ч, линия времени, прокрутка к «сейчас».
        currentZoom = 4;
        refresh(grid, area);

        // Пересчёт ширины колонок при ресайзе окна.
        window.removeEventListener('resize', window.__tlResize || (() => { }));
        window.__tlResize = () => applyZoom(grid, area, currentZoom);
        window.addEventListener('resize', window.__tlResize);
    }

    // Сторож: переживает SSR/enhanced-навигацию. Если на странице появилась новая сетка
    // (после перехода/возврата) — инициализируем её; иначе просто двигаем линию времени.
    function startWatchdog() {
        clearInterval(window.__tlTimer);
        window.__tlTimer = setInterval(() => {
            const g = document.querySelector('[data-tl-grid]');
            if (!g) return;
            if (g.dataset.tlReady !== '1') init();
            else positionNow(g);
        }, 5000);
    }

    // Запоминаем выбранный вид (Таймлайн/Список) в cookie — сервер восстанавливает его при
    // навигации на турнир и обратно (radio рендерится уже с нужным checked). Здесь же — мгновенно
    // применяем сохранённое значение на случай, если страница пришла без серверного восстановления.
    function restoreView() {
        const tl = document.getElementById('sv-tl');
        const list = document.getElementById('sv-list');
        if (!tl || !list) return;

        if (!list.dataset.persistBound) {
            list.dataset.persistBound = '1';
            const save = () => {
                const v = list.checked ? 'list' : 'tl';
                document.cookie = 'schedview=' + v + ';path=/;max-age=31536000;samesite=lax';
            };
            tl.addEventListener('change', save);
            list.addEventListener('change', save);
        }

        const m = document.cookie.match(/(?:^|;\s*)schedview=(list|tl)/);
        if (m && m[1] === 'list') list.checked = true;
        else if (m && m[1] === 'tl') tl.checked = true;
    }

    function boot() { restoreView(); init(); startWatchdog(); }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', boot);
    } else {
        boot();
    }
    // Blazor enhanced navigation: повторная инициализация после обновления DOM.
    document.addEventListener('enhancedload', boot);
    // Возврат по кнопке «Назад» (в т.ч. из bfcache).
    window.addEventListener('pageshow', restoreView);
})();
