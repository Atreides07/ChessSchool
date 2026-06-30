// Тонкий клиент экрана трансляции: сетка всех досок турнира + оверлей одной доски с навигацией по ходам.
// Данные тянет с minimal-API /api/broadcasts/{slug}/games[/n] (разбор PGN — на сервере) и рисует доски из
// FEN без внешних библиотек. Подключён ГЛОБАЛЬНО в App.razor и сам инициализируется по появлению #bd-root:
// Blazor enhanced-navigation НЕ исполняет вставленный в страницу <script>, поэтому инлайновый скрипт «висел»
// на «Загружаем доски…» (как t-root у tournament.js). Идемпотентен и переживает навигацию.
(function () {
    const FILES = ['a', 'b', 'c', 'd', 'e', 'f', 'g', 'h'];
    const POLL_MS = 12000;
    const esc = (s) => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
    const pieceImg = (ch) => {
        const color = ch === ch.toUpperCase() ? 'w' : 'b';
        return `<img class="cp" draggable="false" alt="" src="_content/ChessSchool.Design/pieces/${color}${ch.toUpperCase()}.svg">`;
    };
    function parseFen(fen) {
        const map = {};
        const rows = (fen || '').split(' ')[0].split('/');
        for (let r = 0; r < 8; r++) {
            let file = 0;
            for (const ch of rows[r] || '') {
                if (ch >= '1' && ch <= '8') file += +ch;
                else { map[FILES[file] + (8 - r)] = ch; file++; }
            }
        }
        return map;
    }

    // --- состояние клиента (одна страница трансляции за раз) ---
    let slug = '', L = {}, setupGen = 0;
    let gridEl = null, overlayEl = null, boardEl = null, movesEl = null, counterEl = null, resultEl = null;
    let pollTimer = null, progressTimer = null;
    const cells = {}, coordHtml = {};
    let openId = null, fens = [], froms = [], tos = [], sans = [], total = 0, ply = 0;

    function teardown() {
        setupGen++;
        if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
        if (progressTimer) { clearInterval(progressTimer); progressTimer = null; }
        openId = null; ply = 0; total = 0;
    }

    // --- Индикатор загрузки в процентах (трикл: opaque-ожидание серверного разбора фида) ---
    function startProgress() {
        gridEl.innerHTML =
            '<div class="bd-loading"><div class="bd-progress"><div class="bd-bar"></div></div>' +
            `<span class="bd-pct">${esc(L.loading || 'Loading…')} 0%</span></div>`;
        const bar = gridEl.querySelector('.bd-bar'), pct = gridEl.querySelector('.bd-pct');
        let p = 8;
        progressTimer = setInterval(() => {
            p += Math.max(0.6, (92 - p) * 0.12); // замедляющийся прирост, потолок ~92% до прихода данных
            if (p > 92) p = 92;
            if (bar) bar.style.width = p + '%';
            if (pct) pct.textContent = `${esc(L.loading || 'Loading…')} ${Math.round(p)}%`;
        }, 200);
    }
    function stopProgress() { if (progressTimer) { clearInterval(progressTimer); progressTimer = null; } }

    // --- Сетка досок ---
    function miniBoard(fen, from, to) {
        const pos = parseFen(fen);
        let html = '';
        for (let r = 8; r >= 1; r--) for (let fi = 0; fi < 8; fi++) {
            const f = FILES[fi], sq = f + r, dark = (fi + r) % 2 === 1; // a1 тёмная (light справа-снизу)
            const hl = (sq === from || sq === to) ? ' hl' : '';
            const ch = pos[sq];
            html += `<div class="bd-msq ${dark ? 'd' : 'l'}${hl}">${ch ? pieceImg(ch) : ''}</div>`;
        }
        return `<div class="bd-mini">${html}</div>`;
    }
    function nameLine(name, elo) {
        return `<span class="bd-pl-name">${esc(name)}</span>${elo ? `<span class="bd-pl-elo">${esc(elo)}</span>` : ''}`;
    }
    function gameCard(b) {
        const tag = b.finished
            ? `<span class="bd-tag done">${esc(b.result)}</span>`
            : `<span class="bd-tag live">● ${esc(L.live)}</span>`;
        return `<button class="bd-card" data-board="${b.board}">
            <div class="bd-card-top"><span class="bd-bn">#${b.board}</span>${tag}</div>
            ${miniBoard(b.fen, b.lastFrom, b.lastTo)}
            <div class="bd-card-pl">${nameLine(b.black, b.blackElo)}</div>
            <div class="bd-card-pl">${nameLine(b.white, b.whiteElo)}</div>
        </button>`;
    }

    async function loadGrid() {
        const gen = setupGen;
        let data;
        try {
            const r = await fetch(`/api/broadcasts/${encodeURIComponent(slug)}/games`, { credentials: 'include' });
            if (gen !== setupGen || !gridEl) return; // ушли со страницы во время запроса
            stopProgress();
            if (r.status === 404) { gridEl.innerHTML = `<p class="bd-muted">${esc(L.empty)}</p>`; return; }
            if (!r.ok) { gridEl.innerHTML = `<p class="bd-muted">${esc(L.error)}</p>`; return; }
            data = await r.json();
        } catch { if (gen === setupGen && gridEl) { stopProgress(); gridEl.innerHTML = `<p class="bd-muted">${esc(L.error)}</p>`; } return; }
        if (gen !== setupGen || !gridEl) return;

        const boards = data.boards || [];
        if (!boards.length) { gridEl.innerHTML = `<p class="bd-muted">${esc(L.empty)}</p>`; return; }
        gridEl.innerHTML = boards.map(gameCard).join('');
        gridEl.querySelectorAll('[data-board]').forEach(b => b.onclick = () => openBoard(+b.dataset.board));
    }

    // --- Оверлей одной доски с навигацией по ходам ---
    function buildBoard() {
        boardEl.innerHTML = '';
        for (const k in cells) delete cells[k];
        for (let r = 8; r >= 1; r--) for (let fi = 0; fi < 8; fi++) {
            const f = FILES[fi], sq = f + r, dark = (fi + r) % 2 === 1, tone = dark ? 'on-dark' : 'on-light'; // a1 тёмная
            const el = document.createElement('div');
            el.className = 'sq ' + (dark ? 'dark' : 'light');
            let cd = '';
            if (fi === 0) cd += `<span class="coord rank ${tone}">${r}</span>`;
            if (r === 1) cd += `<span class="coord file ${tone}">${f}</span>`;
            coordHtml[sq] = cd;
            boardEl.appendChild(el);
            cells[sq] = el;
        }
    }
    function renderBoard() {
        const pos = parseFen(fens[ply]);
        for (const f of FILES) for (let r = 1; r <= 8; r++) {
            const sq = f + r, el = cells[sq], ch = pos[sq];
            if (!el) continue;
            el.innerHTML = coordHtml[sq] + (ch ? pieceImg(ch) : '');
            el.classList.remove('hl');
        }
        if (froms[ply] && cells[froms[ply]]) cells[froms[ply]].classList.add('hl');
        if (tos[ply] && cells[tos[ply]]) cells[tos[ply]].classList.add('hl');
        counterEl.textContent = `${ply} / ${total}`;
        movesEl.querySelectorAll('.bd-cell').forEach(c => c.classList.toggle('on', +c.dataset.ply === ply));
        const onCell = movesEl.querySelector('.bd-cell.on');
        // Скроллим ИМЕННО панель ходов (а не страницу/модал — scrollIntoView('nearest') это делал
        // ненадёжно: на «живом» конце список оставался в начале и казался законченным). Центрируем
        // текущий ход в видимой области, чтобы последние ходы были видны сразу и было понятно, что есть прокрутка.
        // На следующем кадре: при первом открытии оверлея геометрия ещё не разложена (rect нулевые),
        // поэтому считаем и скроллим после раскладки. На ручной навигации лишний кадр незаметен.
        if (onCell) requestAnimationFrame(() => {
            const c = movesEl.querySelector('.bd-cell.on'); if (!c) return;
            const cr = c.getBoundingClientRect(), mr = movesEl.getBoundingClientRect();
            movesEl.scrollTop += (cr.top - mr.top) - mr.height / 2 + cr.height / 2;
        });
    }
    function renderMoves() {
        let html = '';
        for (let n = 1; n <= Math.ceil(total / 2); n++) {
            const w = 2 * n - 1, bl = 2 * n;
            html += `<span class="bd-rownum">${n}.</span>`;
            html += `<button class="bd-cell" data-ply="${w}">${esc(sans[w] || '')}</button>`;
            html += bl <= total ? `<button class="bd-cell" data-ply="${bl}">${esc(sans[bl] || '')}</button>` : '<span></span>';
        }
        movesEl.innerHTML = html;
        movesEl.querySelectorAll('.bd-cell').forEach(c => c.onclick = () => { ply = +c.dataset.ply; renderBoard(); });
    }
    function setGame(d) {
        const plies = d.plies || [];
        const atEnd = ply === total; // следим за «живым» концом, чтобы подтягивать новые ходы
        fens = [d.startFen, ...plies.map(p => p.fen)];
        froms = [null, ...plies.map(p => p.from)];
        tos = [null, ...plies.map(p => p.to)];
        sans = [null, ...plies.map(p => p.san)];
        const wasTotal = total;
        total = plies.length;
        if (openId !== d.board || ply > total || (atEnd && total !== wasTotal)) ply = total;
        document.getElementById('bd-top').innerHTML = nameLine(d.black, d.blackElo);
        document.getElementById('bd-bottom').innerHTML = nameLine(d.white, d.whiteElo);
        const ongoing = !(d.result === '1-0' || d.result === '0-1' || d.result === '1/2-1/2');
        resultEl.textContent = ongoing ? L.ongoing : d.result;
        resultEl.className = 'bd-result ' + (ongoing ? 'live' : 'done');
        renderMoves();
        renderBoard();
    }
    async function fetchBoard(n) {
        const r = await fetch(`/api/broadcasts/${encodeURIComponent(slug)}/games/${n}`, { credentials: 'include' });
        return r.ok ? await r.json() : null;
    }
    async function openBoard(n) {
        const d = await fetchBoard(n);
        if (!d || !overlayEl) return;
        openId = n; ply = -1; total = -1; // setGame переустановит на конец при первой загрузке
        setGame(d);
        overlayEl.hidden = false;
        document.body.style.overflow = 'hidden';
    }
    function closeOverlay() {
        if (overlayEl) overlayEl.hidden = true;
        openId = null;
        document.body.style.overflow = '';
    }

    // Навигация клавиатурой вешается на document один раз и читает текущее состояние модуля.
    function bindKeysOnce() {
        if (window.__bdKeys) return;
        window.__bdKeys = true;
        document.addEventListener('keydown', (e) => {
            if (!overlayEl || overlayEl.hidden) return;
            if (e.key === 'Escape') closeOverlay();
            else if (e.key === 'ArrowLeft') { if (ply > 0) { ply--; renderBoard(); } }
            else if (e.key === 'ArrowRight') { if (ply < total) { ply++; renderBoard(); } }
        });
    }

    function setup() {
        const root = document.getElementById('bd-root');
        if (!root) return; // не страница трансляции — no-op
        teardown();
        const gen = setupGen;

        slug = root.getAttribute('data-slug') || '';
        try { L = JSON.parse(document.getElementById('bd-loc')?.textContent || '{}'); } catch { L = {}; }

        gridEl = document.getElementById('bd-boards');
        overlayEl = document.getElementById('bd-overlay');
        boardEl = document.getElementById('bd-board');
        movesEl = document.getElementById('bd-moves');
        counterEl = document.getElementById('bd-counter');
        resultEl = document.getElementById('bd-result');
        if (!gridEl || !overlayEl || !boardEl) return;

        buildBoard();
        document.getElementById('bd-close').onclick = closeOverlay;
        overlayEl.onclick = (e) => { if (e.target === overlayEl) closeOverlay(); };
        document.getElementById('bd-first').onclick = () => { ply = 0; renderBoard(); };
        document.getElementById('bd-prev').onclick = () => { if (ply > 0) { ply--; renderBoard(); } };
        document.getElementById('bd-next').onclick = () => { if (ply < total) { ply++; renderBoard(); } };
        document.getElementById('bd-last').onclick = () => { ply = total; renderBoard(); };
        bindKeysOnce();

        startProgress();
        loadGrid();
        pollTimer = setInterval(async () => {
            if (gen !== setupGen) return;
            await loadGrid();
            if (overlayEl && !overlayEl.hidden && openId !== null) { const d = await fetchBoard(openId); if (d) setGame(d); }
        }, POLL_MS);
    }

    // Старт при появлении #bd-root в DOM (enhanced-навигация не исполняет вставленный <script> и не всегда
    // шлёт enhancedload — наблюдатель ловит вставку узла; скрипт глобальный, поэтому наблюдатель жив всегда).
    function watch() {
        if (window.__bdObserver) return;
        const relevant = (n) => n.nodeType === 1 && (n.matches?.('#bd-root') || n.querySelector?.('#bd-root'));
        window.__bdObserver = new MutationObserver((records) => {
            if (!records.some(r => Array.from(r.addedNodes).some(relevant))) return;
            if (window.__bdRaf) return;
            window.__bdRaf = requestAnimationFrame(() => { window.__bdRaf = 0; setup(); });
        });
        window.__bdObserver.observe(document.documentElement, { childList: true, subtree: true });
    }

    watch();
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', setup);
    else setup();
    document.addEventListener('enhancedload', setup);
})();
