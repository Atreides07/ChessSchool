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

    // hours > 0 — показать столько часов (9ч = всё окно расписания целиком); hours <= 0 —
    // вписать всё окно (защитный запас). Колонка никогда не уже fit-all: иначе сетка (фикс. 9ч
    // данных) окажется уже вьюпорта и справа появится пустота — так «ломался» зум шире данных (12ч на 9ч).
    function applyZoom(grid, area, hours) {
        currentZoom = hours;
        const cols = parseInt(getComputedStyle(grid).getPropertyValue('--cols'), 10) || 18;
        const avail = area.clientWidth - gutterWidth(grid);
        const fitAll = avail / cols; // ширина колонки, при которой всё окно ровно влезает без пустот
        let colw = hours > 0 ? avail / (hours * 2) : fitAll;
        colw = Math.max(fitAll, colw);             // не уже, чем нужно для заполнения вьюпорта
        colw = Math.max(34, Math.min(360, colw));  // в разумных пределах
        grid.style.setProperty('--colw', colw + 'px');
        positionNow(grid);
    }

    function scrollToNow(grid, area, center, smooth = true) {
        const start = parseInt(grid.getAttribute('data-start'), 10);
        const now = Date.now() / 1000;
        if (isNaN(start)) return;
        const x = gutterWidth(grid) + ((now - start) / HALF) * colWidth(grid);
        const target = center ? x - area.clientWidth / 2 : x - gutterWidth(grid) - 20;
        area.scrollTo({ left: Math.max(0, target), behavior: smooth ? 'smooth' : 'auto' });
    }

    // Пересчёт ширины колонок, линии времени и прокрутки к «сейчас» (когда таймлайн виден).
    // smooth=false — для первой отрисовки/возврата (без анимации, чтобы не было «прыжка»).
    // Если область ещё не получила ширину (layout не готов) — повторяем на следующем кадре,
    // иначе now-линия спозиционируется по нулевой геометрии и останется скрытой.
    function refresh(grid, area, smooth = true, attempt = 0) {
        if (area.clientWidth === 0 && attempt < 20) {
            requestAnimationFrame(() => refresh(grid, area, smooth, attempt + 1));
            return;
        }
        applyZoom(grid, area, currentZoom);
        positionNow(grid);
        scrollToNow(grid, area, true, smooth);
    }

    // Однократная привязка интерактива к конкретному узлу сетки. Флаг живёт на самом узле:
    // если enhanced-навигация заменит сетку — флаг исчезнет вместе с ней и обработчики
    // навесятся заново; если узел сохранён (морфинг) — повторно не навешиваем.
    function bindHandlers(grid, area) {
        if (grid.dataset.tlBound === '1') return;
        grid.dataset.tlBound = '1';

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
            if (document.getElementById('sv-tl')?.checked) requestAnimationFrame(() => refresh(grid, area, false));
        }));

        // Пересчёт ширины колонок при ресайзе окна.
        window.removeEventListener('resize', window.__tlResize || (() => { }));
        window.__tlResize = () => applyZoom(grid, area, currentZoom);
        window.addEventListener('resize', window.__tlResize);
    }

    function init() {
        const grid = document.querySelector('[data-tl-grid]');
        const area = document.querySelector('[data-tl-scrollarea]');
        if (!grid || !area) return;

        bindHandlers(grid, area);

        // Позиционирование линии времени и скролл к «сейчас» делаем ВСЕГДА (а не один раз):
        // при возврате из турнира enhanced-навигация морфит сетку, оставляя старый флаг,
        // но разметка now-линии вставляется заново и без повторного refresh остаётся скрытой.
        currentZoom = 4;
        refresh(grid, area, false);
    }

    // Линия времени тикает раз в 5с (только сдвиг now-линии у готовой сетки).
    function startWatchdog() {
        clearInterval(window.__tlTimer);
        window.__tlTimer = setInterval(() => {
            const g = document.querySelector('[data-tl-grid]');
            if (g) positionNow(g);
        }, 5000);
    }

    // Мгновенная инициализация при появлении сетки в DOM (Blazor enhanced-навигация заново
    // вставляет разметку, но не перезапускает скрипты и не всегда шлёт enhancedload —
    // поэтому ждём именно появление узла, а не 5-секундный тик). Реагируем ТОЛЬКО на повторную
    // вставку сетки/now-линии (а не на любую мутацию — иначе сдвиг линии раз в 5с зациклит boot).
    function watchForGrid() {
        if (window.__tlObserver) return;
        const isRelevant = (node) => node.nodeType === 1 &&
            (node.matches?.('[data-tl-grid],[data-tl-nowline]') ||
                node.querySelector?.('[data-tl-grid],[data-tl-nowline]'));
        window.__tlObserver = new MutationObserver((records) => {
            const reinserted = records.some(r => Array.from(r.addedNodes).some(isRelevant));
            if (!reinserted) return;
            if (window.__tlBootRaf) return; // дебаунс: один boot на пачку мутаций
            window.__tlBootRaf = requestAnimationFrame(() => { window.__tlBootRaf = 0; boot(); });
        });
        window.__tlObserver.observe(document.documentElement, { childList: true, subtree: true });
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

    watchForGrid(); // ловим появление сетки сразу, без задержки

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', boot);
    } else {
        boot();
    }
    // Blazor enhanced navigation: повторная инициализация после обновления DOM.
    document.addEventListener('enhancedload', boot);
    // Возврат по кнопке «Назад» (в т.ч. из bfcache).
    window.addEventListener('pageshow', boot);
})();
