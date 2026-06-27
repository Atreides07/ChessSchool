// Тонкий клиент страницы турнира: подключается к /arenahub (SignalR), рендерит шапку/таблицу/доски/
// свою партию и анимирует часы локально. Снимает «состояние на зрителя» с веб-сервера (нет Blazor-circuit
// и серверных ре-рендеров по таймеру). Идемпотентен и переживает enhanced-navigation (как schedule.js).
(function () {
    const PIECE = (color, type) => `_content/ChessSchool.Design/pieces/${color}${type.toUpperCase()}.svg`;
    const pieceImg = (color, type, cls) => `<img class="${cls || 'cp'}" draggable="false" alt="" src="${PIECE(color, type)}">`;
    const esc = (s) => String(s ?? '').replace(/[&<>]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]));
    const FILES = ['a', 'b', 'c', 'd', 'e', 'f', 'g', 'h'];

    // --- состояние клиента ---
    let conn = null, ChessLib = null, signalR = null, chess = null;
    let state = null, stateAt = 0, currentId = null, clockTimer = null;
    let authed = false, loginUrl = '/signin', L = {}, isEn = false;
    let sel = null, pendingPromo = null, myColor = 'w', flip = false, lastPersonalFetch = 0;

    async function ensureLibs() {
        if (!ChessLib) ChessLib = (await import('https://esm.sh/chess.js@1')).Chess;
        if (!signalR) signalR = await import('https://esm.sh/@microsoft/signalr@8');
    }

    function teardown() {
        if (clockTimer) { clearInterval(clockTimer); clockTimer = null; }
        if (conn) { try { conn.stop(); } catch (e) { } conn = null; }
        currentId = null; state = null; chess = null; sel = null; pendingPromo = null;
    }

    async function setup() {
        const root = document.getElementById('t-root');
        if (!root) { teardown(); return; }            // ушли со страницы турнира → закрыть соединение
        const id = root.dataset.id;
        if (conn && currentId === id) return;          // уже инициализировано для этого турнира
        teardown();
        currentId = id;
        authed = root.dataset.authed === '1';
        loginUrl = root.dataset.loginurl || '/signin';
        try { const cfg = JSON.parse(document.getElementById('t-loc').textContent); L = cfg.l; isEn = cfg.isEn; }
        catch (e) { L = {}; }

        await ensureLibs();
        chess = new ChessLib();

        conn = new signalR.HubConnectionBuilder().withUrl('/arenahub').withAutomaticReconnect().build();
        conn.on('ArenaState', s => onShared(s));
        conn.onreconnected(async () => { try { applyState(await conn.invoke('GetState', id)); } catch (e) { } });

        try {
            await conn.start();
            applyState(await conn.invoke('JoinTournament', id));
        } catch (e) {
            const m = document.getElementById('t-main');
            if (m) m.innerHTML = `<p class="text-muted">${esc(L.loading || '…')}</p>`;
            return;
        }
        clockTimer = setInterval(tickClocks, 250);
    }

    // Общий пуш (без своей партии): зрителю — применяем как есть; участнику — берём свежее персональное
    // состояние (троттлинг), а общие части (доски/таблица/часы) обновляем сразу, не дожидаясь round-trip.
    function onShared(s) {
        if (state && state.joined) {
            const { myGame, joined, myScore } = state;
            state = s; state.myGame = myGame; state.joined = joined; state.myScore = myScore;
            stateAt = Date.now(); render();
            if (Date.now() - lastPersonalFetch > 700) {
                lastPersonalFetch = Date.now();
                conn.invoke('GetState', currentId).then(applyState).catch(() => { });
            }
        } else {
            applyState(s);
        }
    }

    function applyState(s) {
        if (!s) return;
        state = s; stateAt = Date.now();
        if (s.name) document.title = s.name + ' — ChessArena';
        syncMyGame();
        render();
    }

    function syncMyGame() {
        const g = state && state.myGame;
        if (g && g.fen) {
            myColor = g.myColor === 1 ? 'b' : 'w';
            flip = myColor === 'b';
            try { chess.load(g.fen); } catch (e) { }
        }
    }

    // ----------------------- рендер -----------------------
    function render() {
        const main = document.getElementById('t-main');
        if (!main || !state) return;
        const crumb = document.getElementById('t-crumb');
        if (crumb) crumb.textContent = state.name;
        const running = state.status === 1;
        main.innerHTML =
            heroHtml() +
            ((running && state.joined) ? myGameHtml() : '') +
            boardsHtml() +
            standingsHtml();
        wireBoardHandlers();
        wireActionHandlers();
        tickClocks();
    }

    function heroHtml() {
        const st = state.status;
        const badge = st === 1 ? `<span class="t-live"><span class="t-dot"></span>Live</span>`
            : `<span class="t-live past">${esc(st === 2 ? L.finished : L.soon)}</span>`;
        const started = new Date(state.startedAt);
        const meta =
            `<span>${fmtDateTime(started)}</span><span class="sep">•</span>` +
            `<span>${Math.floor(state.durationSeconds / 60)} ${esc(L.minutes)}</span><span class="sep">•</span>` +
            `<span>${esc(L.control)} ${Math.floor(state.timeControl.initialSeconds / 60)} + ${state.timeControl.incrementSeconds}</span>`;
        let side = '';
        if (st === 1) side += `<div class="t-countdown">${esc(L.endin)} <strong class="js-countdown" data-left="${state.secondsLeft}">${hms(state.secondsLeft)}</strong></div>`;
        else if (st === 0) side += `<div class="t-countdown">${esc(L.start)} <strong>${fmtShort(started)}</strong></div>`;

        const top = state.standings.slice(0, 3).map(s => `<span class="t-ava">${avatar(s.name)}</span>`).join('');
        const cnt = state.standings.length;
        side += `<div class="t-parts"><span class="t-ava-row">${top}</span><span>${cnt} ${esc(participants(cnt))}</span></div>`;
        side += actionHtml();

        return `<div class="t-hero card">
            <div class="t-thumb ${st === 1 ? '' : 'dim'}">${badge}</div>
            <div class="t-hero-mid"><div class="t-meta">${meta}</div><h1 class="t-title">${esc(state.name)}</h1></div>
            <div class="t-hero-side">${side}</div>
        </div>`;
    }

    function actionHtml() {
        const st = state.status;
        if (!authed && st !== 2)
            return `<a class="btn btn-join" href="${esc(loginUrl)}">${esc(st === 0 ? L.loginreg : L.login)}</a>`;
        if (st === 1 && authed)
            return state.joined ? `<span class="badge badge-success t-badge">${esc(L.injoined)}</span>`
                : `<button class="btn btn-join" id="t-act">${esc(L.join)}</button>`;
        if (st === 0 && authed)
            return state.joined ? `<span class="badge badge-success t-badge">${esc(L.registered)}</span>`
                : `<button class="btn btn-join" id="t-act">${esc(L.register)}</button>`;
        return '';
    }

    function myGameHtml() {
        const g = state.myGame;
        if (!g) {
            return `<div class="card waiting">
                <div class="search-anim" aria-hidden="true"><span class="search-ring"></span>
                    <span class="search-piece">${pieceImg('b', 'n', 'cp')}</span></div>
                <div class="search-text">${esc(L.search)}<span class="dots"><i></i><i></i><i></i></span></div>
                <div class="text-muted mt-1">${esc(L.score)} <strong>${state.myScore}</strong></div>
            </div>`;
        }
        const fin = g.status === 2;
        const wActive = g.turn === 0 && g.status === 1, bActive = g.turn === 1 && g.status === 1;
        const gstatus = fin
            ? `<strong>${esc(resultText(g.result))}</strong> <span class="text-muted">${esc(L.waitnext)}</span>`
            : `<span class="text-secondary">${esc(g.turn === (g.myColor) ? L.yourmove : L.oppmove)}</span>
               <span class="ds-spacer"></span>
               ${g.myBerserkAvailable ? `<button class="btn btn-warn btn-sm" id="t-berserk" title="${esc(L.berserktip)}">⚡ Berserk</button>` : ''}
               <button class="btn btn-outline-danger btn-sm" id="t-resign">${esc(L.resign)}</button>`;
        return `<div class="card my-game">
            <div class="players">
                <span>${g.blackBerserk ? '⚡ ' : ''}${esc(g.blackName)} <span class="clock js-clock ${bActive ? 'active' : ''}" data-ms="${g.blackMs}" data-active="${bActive ? 1 : 0}">${mmss(g.blackMs)}</span></span>
                <span>${g.whiteBerserk ? '⚡ ' : ''}${esc(g.whiteName)} <span class="clock js-clock ${wActive ? 'active' : ''}" data-ms="${g.whiteMs}" data-active="${wActive ? 1 : 0}">${mmss(g.whiteMs)}</span></span>
            </div>
            <div class="board-wrap"><div class="board" id="t-board"></div><div class="promo" id="t-promo" hidden></div></div>
            <div class="gstatus">${gstatus}</div>
        </div>`;
    }

    function boardsHtml() {
        const mineId = state.myGame ? state.myGame.gameId : null;
        const list = state.boards.filter(b => b.gameId !== mineId).slice(0, 4);
        if (list.length === 0) return '';
        const cards = list.map(b => {
            const wActive = b.turn === 0 && b.status === 1, bActive = b.turn === 1 && b.status === 1;
            const res = b.status === 2 ? `<div class="bresult"><span class="rcell w">${gres(b.result, true)}</span><span class="rdash">–</span><span class="rcell b">${gres(b.result, false)}</span></div>` : '';
            return `<div class="bcard ${b.status === 2 ? 'done' : ''}">
                <div class="bplayer"><span class="bava">${avatar(b.blackName)}</span><span class="bname">${esc(b.blackName)}</span><span class="bscore">${b.blackScore}</span></div>
                <div class="bclock js-clock ${bActive ? 'active' : ''}" data-ms="${b.blackMs}" data-active="${bActive ? 1 : 0}">${mmss(b.blackMs)}</div>
                <div class="bboard">${miniBoard(b.fen, b.lastFrom, b.lastTo, b.checkSquare)}${res}</div>
                <div class="bclock js-clock ${wActive ? 'active' : ''}" data-ms="${b.whiteMs}" data-active="${wActive ? 1 : 0}">${mmss(b.whiteMs)}</div>
                <div class="bplayer"><span class="bava">${avatar(b.whiteName)}</span><span class="bname">${esc(b.whiteName)}</span><span class="bscore">${b.whiteScore}</span></div>
            </div>`;
        }).join('');
        return `<div class="sec-head"><h2 class="sec-title">${esc(L.games)}</h2>
            <a class="all-games" href="/t/${esc(currentId)}/games">${esc(L.allgames)}</a></div>
            <div class="boards-grid">${cards}</div>`;
    }

    function standingsHtml() {
        const r = state.standings;
        if (r.length === 0) return '';
        const podium = r.slice(0, 3).map(p =>
            `<div class="pod ${podClass(p.rank)}"><div class="pod-cup">${trophy(p.rank)}</div>
                <div class="pod-name">${esc(p.name)}</div>
                <div class="pod-stat">${p.games} ${esc(L.podgames)} • ${p.wins} ${esc(L.podwins)} • ${p.score} ${esc(L.podpts)}</div></div>`).join('');
        const list = r.map(s => {
            const chips = s.results.length ? `<div class="st-chips">${s.results.map(x => `<span class="chip ${chipClass(x)}">${x}</span>`).join('')}</div>` : '';
            return `<div class="st-card"><div class="st-head">
                <span class="st-ava">${avatar(s.name)}</span>
                <span class="st-name">${esc(s.name)} ${s.onFire ? `<span class="fire">🔥 ${s.streak}</span>` : ''}</span>
                <span class="st-place">${s.rank} ${esc(L.place)}</span>
                <span class="st-score">${s.score}</span></div>${chips}</div>`;
        }).join('');
        return `${podium ? `<div class="podium">${podium}</div>` : ''}<div class="st-list">${list}</div>`;
    }

    // ----------------------- интерактивная доска (своя партия) -----------------------
    function wireBoardHandlers() {
        const boardEl = document.getElementById('t-board');
        if (!boardEl || !state.myGame) return;
        buildBoard(boardEl);
        renderBoard();
    }

    const cells = {}, coordHtml = {};
    function buildBoard(boardEl) {
        boardEl.innerHTML = '';
        for (const k in cells) delete cells[k];
        const ranks = flip ? [1, 2, 3, 4, 5, 6, 7, 8] : [8, 7, 6, 5, 4, 3, 2, 1];
        const cols = flip ? [...FILES].reverse() : FILES;
        for (const rr of ranks) for (const f of cols) {
            const sq = f + rr, dark = (FILES.indexOf(f) + rr) % 2 === 0;
            const el = document.createElement('button');
            el.className = 'sq ' + (dark ? 'dark' : 'light');
            el.onclick = () => onCellClick(sq);
            let cd = ''; const tone = dark ? 'on-dark' : 'on-light';
            if (f === cols[0]) cd += `<span class="coord rank ${tone}">${rr}</span>`;
            if (rr === ranks[ranks.length - 1]) cd += `<span class="coord file ${tone}">${f}</span>`;
            coordHtml[sq] = cd; cells[sq] = el; boardEl.appendChild(el);
        }
    }

    function renderBoard() {
        const g = state.myGame; if (!g) return;
        for (const f of FILES) for (let r = 1; r <= 8; r++) {
            const sq = f + r, el = cells[sq]; if (!el) continue;
            const p = chess.get(sq);
            el.innerHTML = coordHtml[sq] + (p ? pieceImg(p.color, p.type, 'piece-img') : '');
            el.classList.remove('hl', 'chk', 'sel');
        }
        if (g.lastFrom && cells[g.lastFrom]) cells[g.lastFrom].classList.add('hl');
        if (g.lastTo && cells[g.lastTo]) cells[g.lastTo].classList.add('hl');
        if (g.checkSquare && cells[g.checkSquare]) cells[g.checkSquare].classList.add('chk');
    }

    function onCellClick(sq) {
        const g = state.myGame;
        if (!g || g.status !== 1 || pendingPromo) return;
        if (chess.turn() !== myColor) return;
        const p = chess.get(sq);
        if (sel === null) { if (p && p.color === myColor) { sel = sq; cells[sq].classList.add('sel'); } return; }
        const from = sel; cells[from] && cells[from].classList.remove('sel'); sel = null;
        if (from === sq) return;
        const mover = chess.get(from);
        if (mover && mover.type === 'p' && (sq[1] === '8' || sq[1] === '1')) { askPromotion(from, sq); return; }
        commitMove(from, sq, null);
    }

    function commitMove(from, to, promotion) {
        let mv = null;
        try { mv = chess.move({ from, to, promotion: promotion || undefined }); } catch (e) { mv = null; }
        if (!mv) return;
        renderBoard(); // мгновенно показываем свой ход (оптимистично)
        conn.invoke('Move', currentId, from, to, mv.promotion || null).then(applyState).catch(() => { });
    }

    function askPromotion(from, to) {
        pendingPromo = { from, to };
        const color = chess.get(from).color;
        const promoEl = document.getElementById('t-promo');
        promoEl.innerHTML = ['q', 'r', 'b', 'n'].map(t => `<button class="promo-btn" data-p="${t}">${pieceImg(color, t)}</button>`).join('');
        promoEl.querySelectorAll('button').forEach(b => b.onclick = () => {
            const t = b.dataset.p, mv = pendingPromo; pendingPromo = null; promoEl.hidden = true;
            commitMove(mv.from, mv.to, t);
        });
        promoEl.hidden = false;
    }

    function wireActionHandlers() {
        const act = document.getElementById('t-act');
        if (act) act.onclick = () => { act.disabled = true; conn.invoke('Register', currentId).then(applyState).catch(() => act.disabled = false); };
        const berserk = document.getElementById('t-berserk');
        if (berserk) berserk.onclick = () => conn.invoke('Berserk', currentId).then(applyState).catch(() => { });
        const resign = document.getElementById('t-resign');
        if (resign) resign.onclick = () => conn.invoke('Resign', currentId).then(applyState).catch(() => { });
    }

    // ----------------------- мини-доска трансляции (без интерактива) -----------------------
    function miniBoard(fen, lastFrom, lastTo, checkSquare) {
        const grid = parseFen(fen);
        let html = '<div class="board-wrap"><div class="board mini">';
        for (let row = 0; row < 8; row++) for (let col = 0; col < 8; col++) {
            const sq = FILES[col] + (8 - row), dark = (row + col) % 2 === 1;
            const ch = grid[row][col];
            let cls = 'sq ' + (dark ? 'dark' : 'light');
            if (sq === lastFrom || sq === lastTo) cls += ' hl';
            if (sq === checkSquare) cls += ' chk';
            const piece = ch ? pieceImg(ch === ch.toLowerCase() ? 'b' : 'w', ch.toLowerCase(), 'piece-img') : '';
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

    // ----------------------- часы (локальная анимация между апдейтами) -----------------------
    function tickClocks() {
        if (!state) return;
        const elapsed = Date.now() - stateAt;
        document.querySelectorAll('.js-clock').forEach(el => {
            const base = +el.dataset.ms, active = el.dataset.active === '1';
            el.textContent = mmss(base - (active ? elapsed : 0));
        });
        const cd = document.querySelector('.js-countdown');
        if (cd) cd.textContent = hms(Math.max(0, (+cd.dataset.left) - Math.floor(elapsed / 1000)));
    }

    // ----------------------- утилиты -----------------------
    const pad = n => String(n).padStart(2, '0');
    function mmss(ms) { const t = Math.max(0, Math.floor(ms / 1000)); return pad(Math.floor(t / 60)) + ':' + pad(t % 60); }
    function hms(sec) { sec = Math.max(0, sec); return pad(Math.floor(sec / 3600)) + ':' + pad(Math.floor(sec / 60) % 60) + ':' + pad(sec % 60); }
    function fmtDateTime(d) { return `${pad(d.getDate())}.${pad(d.getMonth() + 1)}.${d.getFullYear()}, ${pad(d.getHours())}:${pad(d.getMinutes())}`; }
    function fmtShort(d) { return `${pad(d.getDate())}.${pad(d.getMonth() + 1)}, ${pad(d.getHours())}:${pad(d.getMinutes())}`; }
    function resultText(r) { return r === 1 ? L.reswhite : r === 2 ? L.resblack : r === 3 ? L.resdraw : '—'; }
    function gres(r, white) { return r === 3 ? '½' : r === 1 ? (white ? '1' : '0') : r === 2 ? (white ? '0' : '1') : '—'; }
    function trophy(rank) { return rank === 1 ? '🥇' : rank === 2 ? '🥈' : rank === 3 ? '🥉' : String(rank); }
    function podClass(rank) { return rank === 1 ? 'gold' : rank === 2 ? 'silver' : rank === 3 ? 'bronze' : ''; }
    function chipClass(x) { return x >= 4 ? 'c-bonus' : x === 2 ? 'c-win' : x === 1 ? 'c-draw' : 'c-loss'; }
    const AVATARS = ['👶', '🧑', '🧔', '🧓', '👩', '🧒', '👨', '👧'];
    function avatar(name) { let h = 0; for (const ch of String(name)) h = (h * 31 + ch.charCodeAt(0)) & 0x7fffffff; return AVATARS[h % AVATARS.length]; }
    function participants(n) {
        if (isEn) return n === 1 ? (L.participant_one || 'participant') : (L.participant_many || 'participants');
        const m10 = n % 10, m100 = n % 100;
        if (m10 === 1 && m100 !== 11) return L.participant_one;
        if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return L.participant_few;
        return L.participant_many;
    }

    window.arenaTournamentSetup = setup;
    document.addEventListener('DOMContentLoaded', setup);
    document.addEventListener('enhancedload', setup);
})();
