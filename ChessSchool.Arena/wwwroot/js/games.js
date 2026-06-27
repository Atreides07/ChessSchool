// Тонкий клиент страницы «Все игры»: подключается к /arenahub (SignalR), получает ПОЛНЫЙ список
// досок турнира и рендерит их из FEN (read-only), с ленивой подгрузкой и живыми обновлениями.
// Снимает «состояние на зрителя» с веб-сервера (нет Blazor-circuit и серверного ре-рендера сетки —
// для популярного турнира это тысячи circuit'ов против лёгких WS-зрителей). Доски только смотрят,
// поэтому chess.js не нужен — FEN парсится напрямую. Идемпотентен, переживает enhanced-навигацию.
(function () {
    const PIECE = (color, type) => `_content/ChessSchool.Design/pieces/${color}${type.toUpperCase()}.svg`;
    const pieceImg = (color, type) => `<img class="piece-img" draggable="false" alt="" src="${PIECE(color, type)}">`;
    const esc = (s) => String(s ?? '').replace(/[&<>]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]));
    const FILES = ['a', 'b', 'c', 'd', 'e', 'f', 'g', 'h'];
    const PAGE = 12;

    // --- состояние клиента ---
    let conn = null, signalR = null;
    let boards = [], boardsAt = 0, visible = PAGE, currentId = null, clockTimer = null, io = null;
    let connecting = false, setupGen = 0;       // защита от повторного входа (дубли соединений/таймеров)
    let lastFetch = 0, fetchTimer = null;       // троттлинг перезабора всех досок
    let L = {}, signalrUrl = '/lib/signalr.js'; // переопределяется fingerprinted-URL из #ag-root

    async function ensureLibs() {
        // Локальная сборка (vendored в wwwroot/lib), не внешний CDN — быстрый старт на медленном канале.
        if (!signalR) signalR = await import(signalrUrl);
    }

    function teardown() {
        setupGen++;                                       // инвалидируем любой setup «в полёте»
        connecting = false;
        if (clockTimer) { clearInterval(clockTimer); clockTimer = null; }
        if (fetchTimer) { clearTimeout(fetchTimer); fetchTimer = null; }
        if (io) { io.disconnect(); io = null; }
        if (conn) { try { conn.stop(); } catch (e) { } conn = null; }
        currentId = null; boards = []; visible = PAGE;
    }

    async function setup() {
        const root = document.getElementById('ag-root');
        if (!root) { teardown(); return; }                // ушли со страницы → закрыть соединение
        const id = root.dataset.id;
        if (currentId === id && (conn || connecting)) return;
        teardown();
        const gen = ++setupGen;                           // токен этого запуска
        currentId = id; connecting = true; visible = PAGE;
        // @Assets даёт ОТНОСИТЕЛЬНЫЙ путь — для import() резолвим в абсолютный относительно базы документа.
        if (root.dataset.signalr) signalrUrl = new URL(root.dataset.signalr, document.baseURI).href;
        try { L = JSON.parse(document.getElementById('ag-loc').textContent); } catch (e) { L = {}; }

        try {
            await ensureLibs();
            if (gen !== setupGen) return;                 // нас вытеснил teardown/новый setup
            const c = new signalR.HubConnectionBuilder().withUrl('/arenahub').withAutomaticReconnect().build();
            // Общий пуш = «что-то изменилось» → перезабираем полный список (троттлинг). Пуш несёт лишь
            // 4 доски (для шапки), поэтому /games берёт все через GetAllBoards, а не из тела пуша.
            c.on('ArenaState', () => { if (gen === setupGen) scheduleFetch(); });
            c.onreconnected(async () => { try { setBoards(await c.invoke('GetAllBoards', id)); } catch (e) { } });

            await c.start();
            if (gen !== setupGen) { try { c.stop(); } catch (e) { } return; } // ушли/перезапустились
            conn = c;
            setBoards(await c.invoke('JoinAllGames', id));
            clockTimer = setInterval(tickClocks, 250);
        } catch (e) {
            const r = document.getElementById('ag-root');
            if (r) r.innerHTML = `<p class="text-muted">${esc(L.loading || '…')}</p>`;
        } finally {
            if (gen === setupGen) connecting = false;
        }
    }

    // Перезабор полного списка досок не на каждый частый пуш, а не чаще раза в 700 мс
    // (BuildBoards на грейне — серьёзная работа; коалесцируем).
    function scheduleFetch() {
        if (fetchTimer) return;
        const wait = Math.max(0, 700 - (Date.now() - lastFetch));
        fetchTimer = setTimeout(async () => {
            fetchTimer = null; lastFetch = Date.now();
            if (!conn) return;
            try { setBoards(await conn.invoke('GetAllBoards', currentId)); } catch (e) { }
        }, wait);
    }

    function setBoards(list) {
        if (!list) return;
        boards = list; boardsAt = Date.now();
        scheduleRender();
    }

    // Коалесцируем перерисовки в один кадр: частые обновления не вызывают многократный пересбор сетки.
    let renderQueued = false;
    function scheduleRender() {
        if (renderQueued) return;
        renderQueued = true;
        requestAnimationFrame(() => { renderQueued = false; render(); });
    }

    // ----------------------- рендер -----------------------
    function render() {
        const root = document.getElementById('ag-root');
        if (!root) return;
        const count = document.getElementById('ag-count');
        if (count) count.textContent = `${boards.length} ${esc(L.onboard || '')}`;

        if (boards.length === 0) {
            root.innerHTML = `<div class="ag-empty">${esc(L.empty || '')} <a href="/t/${esc(currentId)}">${esc(L.back || '')}</a></div>`;
            return;
        }
        const shown = Math.min(visible, boards.length);
        const cards = boards.slice(0, shown).map(cardHtml).join('');
        const more = visible < boards.length
            ? `<button id="ag-more" class="ag-more">${esc(L.more || '')} (${boards.length - visible})</button><div id="ag-sentinel" class="ag-sentinel"></div>`
            : '';
        root.innerHTML = `<div class="boards-grid">${cards}</div>${more}`;
        wireMore();
        tickClocks();
    }

    function cardHtml(b) {
        const wActive = b.turn === 0 && b.status === 1, bActive = b.turn === 1 && b.status === 1;
        const res = b.status === 2
            ? `<div class="bresult"><span class="rcell w">${gres(b.result, true)}</span><span class="rdash">–</span><span class="rcell b">${gres(b.result, false)}</span></div>`
            : '';
        return `<div class="bcard ${b.status === 2 ? 'done' : ''}">
            <div class="bplayer"><span class="bava">${avatar(b.blackName)}</span><span class="bname">${esc(b.blackName)}</span><span class="bscore">${b.blackScore}</span></div>
            <div class="bclock js-clock ${bActive ? 'active' : ''}" data-ms="${b.blackMs}" data-active="${bActive ? 1 : 0}">${mmss(b.blackMs)}</div>
            <div class="bboard">${miniBoard(b.fen, b.lastFrom, b.lastTo, b.checkSquare)}${res}</div>
            <div class="bclock js-clock ${wActive ? 'active' : ''}" data-ms="${b.whiteMs}" data-active="${wActive ? 1 : 0}">${mmss(b.whiteMs)}</div>
            <div class="bplayer"><span class="bava">${avatar(b.whiteName)}</span><span class="bname">${esc(b.whiteName)}</span><span class="bscore">${b.whiteScore}</span></div>
        </div>`;
    }

    // «Показать ещё» + сентинел: при появлении сентинела в зоне видимости открываем следующую страницу.
    function wireMore() {
        if (io) { io.disconnect(); io = null; }
        const more = document.getElementById('ag-more');
        if (!more) return;
        more.onclick = () => { visible = Math.min(visible + PAGE, boards.length); scheduleRender(); };
        const sentinel = document.getElementById('ag-sentinel');
        if (sentinel) {
            io = new IntersectionObserver(es => { es.forEach(e => { if (e.isIntersecting) more.click(); }); }, { rootMargin: '400px 0px' });
            io.observe(sentinel);
        }
    }

    // ----------------------- мини-доска (read-only, из FEN) -----------------------
    function miniBoard(fen, lastFrom, lastTo, checkSquare) {
        const grid = parseFen(fen);
        let html = '<div class="board-wrap"><div class="board">';
        for (let row = 0; row < 8; row++) for (let col = 0; col < 8; col++) {
            const sq = FILES[col] + (8 - row), dark = (row + col) % 2 === 1;
            const ch = grid[row][col];
            let cls = 'sq ' + (dark ? 'dark' : 'light');
            if (sq === lastFrom || sq === lastTo) cls += ' hl';
            if (sq === checkSquare) cls += ' chk';
            const piece = ch ? pieceImg(ch === ch.toLowerCase() ? 'b' : 'w', ch.toLowerCase()) : '';
            html += `<div class="${cls}">${piece}</div>`;
        }
        return html + '</div></div>';
    }

    function parseFen(fen) {
        const g = Array.from({ length: 8 }, () => Array(8).fill(''));
        const rows = (fen || '').split(' ')[0].split('/');
        for (let r = 0; r < 8 && r < rows.length; r++) {
            let c = 0;
            for (const ch of rows[r]) {
                if (/\d/.test(ch)) c += +ch;
                else if (c < 8) g[r][c++] = ch;
            }
        }
        return g;
    }

    // ----------------------- часы (локальная анимация между обновлениями) -----------------------
    function tickClocks() {
        if (!boards.length) return;
        const elapsed = Date.now() - boardsAt;
        document.querySelectorAll('.js-clock').forEach(el => {
            const base = +el.dataset.ms, active = el.dataset.active === '1';
            el.textContent = mmss(base - (active ? elapsed : 0));
        });
    }

    // ----------------------- утилиты -----------------------
    const pad = n => String(n).padStart(2, '0');
    function mmss(ms) { const t = Math.max(0, Math.floor(ms / 1000)); return pad(Math.floor(t / 60)) + ':' + pad(t % 60); }
    function gres(r, white) { return r === 3 ? '½' : r === 1 ? (white ? '1' : '0') : r === 2 ? (white ? '0' : '1') : '—'; }
    const AVATARS = ['👶', '🧑', '🧔', '🧓', '👩', '🧒', '👨', '👧'];
    function avatar(name) { let h = 0; for (const ch of String(name)) h = (h * 31 + ch.charCodeAt(0)) & 0x7fffffff; return AVATARS[h % AVATARS.length]; }

    // Мгновенный старт при появлении #ag-root в DOM: enhanced-навигация Blazor НЕ исполняет вставленный
    // <script> и не всегда шлёт enhancedload — ловим вставку узла. Скрипт глобальный (App.razor).
    function watchForRoot() {
        if (window.__gRootObserver) return;
        const relevant = (n) => n.nodeType === 1 && (n.matches?.('#ag-root') || n.querySelector?.('#ag-root'));
        window.__gRootObserver = new MutationObserver(records => {
            if (!records.some(r => Array.from(r.addedNodes).some(relevant))) return;
            if (window.__gBootRaf) return;                 // дебаунс: один setup на пачку мутаций
            window.__gBootRaf = requestAnimationFrame(() => { window.__gBootRaf = 0; setup(); });
        });
        window.__gRootObserver.observe(document.documentElement, { childList: true, subtree: true });
    }

    watchForRoot();                                        // переживает enhanced-навигацию
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', setup);
    else setup();                                          // прямой заход: DOM уже готов
    document.addEventListener('enhancedload', setup);      // запасной путь
})();
