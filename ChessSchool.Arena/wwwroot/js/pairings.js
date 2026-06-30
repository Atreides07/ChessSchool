// Тонкий клиент редактора жеребьёвки (/pairings). Импорт из chess-results (.xlsx через /api/pairings/parse
// или ссылка через /api/pairings/fetch — сетевой разбор на сервере, грабля #12), затем вся правкa пар — в
// браузере (состояние не на сервере). Подключён ГЛОБАЛЬНО в App.razor и сам инициализируется по появлению
// #pr-root (enhanced-навигация не исполняет вставленный <script> — грабля #13). Идемпотентен.
(function () {
    const esc = (s) => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

    // --- состояние (одна страница за раз) ---
    let L = {}, setupGen = 0;
    let doc = null;            // { title, sourceUrl, players:[{no,name,rating}], rounds:[{number,schedule,boards:[{whiteNo,blackNo,result}]}] }
    let byNo = new Map();      // no -> player
    let pts = new Map();       // no -> {pts,games,wins,draws,losses,white,black} (по всем турам)
    let ri = 0;                // активный тур (индекс)
    let curRoundIdx = 0;       // «текущий» тур (первый без результатов) — для метки и автооткрытия
    let view = 'pairs';        // 'pairs' | 'standings'
    let sel = null;            // выбранный игрок (no) для клик-свапа
    let dragNo = null;         // перетаскиваемый игрок (no)
    let dragBoardBi = null;    // перетаскиваемая доска (индекс) — смена порядка пар
    let pickBi = null, pickSide = null; // открытый поповер быстрого выбора (для какого слота)
    let focusBi = null;        // вернуть фокус на эту доску после перерисовки (ввод результата с клавиатуры)
    let undoStack = [], redoStack = [];
    let toastTimer = null;
    // DOM-узлы
    let importEl, editorEl, msgEl, fileEl, dropEl, urlForm, roundsEl, headEl, boardsEl,
        poolEl, poolSearch, validEl, titleEl, metaEl, standingsEl, selbarEl, toastEl,
        undoBtn, redoBtn, addBoardEl, hintEl, roundHeadEl, pickEl;

    function teardown() {
        setupGen++; doc = null; byNo = new Map(); pts = new Map(); ri = 0; curRoundIdx = 0;
        view = 'pairs'; sel = null; dragNo = null; dragBoardBi = null; undoStack = []; redoStack = [];
        pickBi = null; pickSide = null; focusBi = null;
        if (toastTimer) { clearTimeout(toastTimer); toastTimer = null; }
        if (pickEl) { pickEl.hidden = true; pickEl.classList.remove('show'); }
    }

    // ---------------- Импорт ----------------

    function showMsg(text, kind) {
        if (!msgEl) return;
        if (!text) { msgEl.hidden = true; return; }
        msgEl.hidden = false;
        msgEl.textContent = text;
        msgEl.className = 'pr-msg' + (kind ? ' pr-msg-' + kind : '');
    }

    async function importFile(file) {
        if (!file) return;
        if (!/\.xlsx$/i.test(file.name)) { showMsg(file.name + ' — нужен .xlsx', 'err'); return; }
        const gen = setupGen;
        showMsg(L.loading || 'Loading…', 'info');
        const fd = new FormData();
        fd.append('file', file);
        try {
            const r = await fetch('/api/pairings/parse', { method: 'POST', body: fd, credentials: 'include' });
            const data = await r.json().catch(() => null);
            if (gen !== setupGen) return;
            if (!r.ok) { showMsg((data && data.error) || 'Не удалось разобрать файл.', 'err'); return; }
            loadDoc(data);
        } catch { if (gen === setupGen) showMsg('Сеть недоступна. Попробуйте ещё раз.', 'err'); }
    }

    async function importUrl(url) {
        url = (url || '').trim();
        if (!url) { showMsg('Вставьте ссылку на турнир chess-results.', 'err'); return; }
        const gen = setupGen;
        showMsg(L.loading || 'Loading…', 'info');
        try {
            const r = await fetch('/api/pairings/fetch', {
                method: 'POST', credentials: 'include',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ url }),
            });
            const data = await r.json().catch(() => null);
            if (gen !== setupGen) return;
            if (!r.ok) { showMsg((data && data.error) || 'Не удалось подтянуть жеребьёвку.', 'err'); return; }
            loadDoc(data);
        } catch { if (gen === setupGen) showMsg('Сеть недоступна. Попробуйте ещё раз.', 'err'); }
    }

    function loadDoc(data) {
        if (!data || !Array.isArray(data.rounds) || data.rounds.length === 0) {
            showMsg('В источнике не найдено туров с парами.', 'err');
            return;
        }
        doc = data;
        byNo = new Map((doc.players || []).map(p => [p.no, p]));
        doc.rounds.forEach(rd => rd.boards.sort((a, b) => (a.board || 0) - (b.board || 0)));
        undoStack = []; redoStack = []; sel = null; view = 'pairs';
        curRoundIdx = currentRoundIndex();
        ri = curRoundIdx;                        // открываем текущий тур, а не первый
        showMsg('', '');
        importEl.hidden = true;
        editorEl.hidden = false;
        renderAll();
        toast((L.loaded || 'Loaded: {0} players, {1} rounds')
            .replace('{0}', (doc.players || []).length).replace('{1}', doc.rounds.length));
    }

    // Текущий тур = первый, где ещё нет ни одного результата (его и пейрят); иначе последний.
    function currentRoundIndex() {
        for (let i = 0; i < doc.rounds.length; i++) {
            const b = doc.rounds[i].boards;
            if (b.length && !b.some(x => x.whiteNo != null && x.blackNo != null && x.result)) return i;
        }
        return doc.rounds.length - 1;
    }

    function reset() {
        if (doc && !confirm(L.confirmReset || 'Start over?')) return;
        teardown();
        editorEl.hidden = true;
        importEl.hidden = false;
        if (fileEl) fileEl.value = '';
        const u = document.getElementById('pr-url'); if (u) u.value = '';
        showMsg('', ''); renderSelbar(); hideToast();
    }

    // ---------------- Очки (по всем турам) ----------------

    function blank() { return { pts: 0, games: 0, wins: 0, draws: 0, losses: 0, white: 0, black: 0 }; }

    function computePoints() {
        const m = new Map();
        const get = (no) => { if (!m.has(no)) m.set(no, blank()); return m.get(no); };
        for (const rd of doc.rounds) for (const b of rd.boards) {
            const w = b.whiteNo, bl = b.blackNo, res = b.result;
            if (w != null && bl != null) {
                const W = get(w), B = get(bl); W.white++; B.black++;
                if (res === '1-0' || res === '+/-') { W.pts += 1; W.games++; W.wins++; B.games++; B.losses++; }
                else if (res === '0-1' || res === '-/+') { B.pts += 1; B.games++; B.wins++; W.games++; W.losses++; }
                else if (res === '½-½') { W.pts += .5; B.pts += .5; W.games++; B.games++; W.draws++; B.draws++; }
            } else if (w != null) { get(w).pts += 1; }       // бай белых
            else if (bl != null) { get(bl).pts += 1; }       // бай чёрных
        }
        return m;
    }

    function fmtPts(p) {
        const i = Math.floor(p), half = p - i >= 0.5;
        return half ? (i ? i + '½' : '½') : String(i);
    }

    // ---------------- Undo / Redo ----------------

    function snapshot() { return JSON.stringify(doc.rounds); }
    function pushUndo() { undoStack.push(snapshot()); if (undoStack.length > 80) undoStack.shift(); redoStack = []; }
    function undo() { if (!undoStack.length) return; redoStack.push(snapshot()); doc.rounds = JSON.parse(undoStack.pop()); sel = null; renderAll(); }
    function redo() { if (!redoStack.length) return; undoStack.push(snapshot()); doc.rounds = JSON.parse(redoStack.pop()); sel = null; renderAll(); }

    // ---------------- Операции над моделью ----------------

    function round() { return doc.rounds[ri]; }

    function findSlot(no) {
        const r = round();
        for (let bi = 0; bi < r.boards.length; bi++) {
            if (r.boards[bi].whiteNo === no) return { bi, side: 'white' };
            if (r.boards[bi].blackNo === no) return { bi, side: 'black' };
        }
        return null;
    }

    function placePlayer(no, bi, side) {
        const r = round(), b = r.boards[bi];
        const occ = side === 'white' ? b.whiteNo : b.blackNo;
        if (occ === no) { clearSel(); renderAll(); return; }
        pushUndo();
        const old = findSlot(no);
        if (side === 'white') b.whiteNo = no; else b.blackNo = no;
        if (old && !(old.bi === bi && old.side === side)) {
            const ob = r.boards[old.bi];
            if (old.side === 'white') ob.whiteNo = occ; else ob.blackNo = occ; // свап (occ может быть null)
        }
        clearSel(); renderAll();
    }

    function unassign(no) {
        const s = findSlot(no);
        if (!s) { clearSel(); renderAll(); return; }
        pushUndo();
        const b = round().boards[s.bi];
        if (s.side === 'white') b.whiteNo = null; else b.blackNo = null;
        clearSel(); renderAll();
    }

    // Бай: игрок садится на отдельную доску один (типичный случай — из пула, нечётный игрок).
    function byePlayer(no) {
        pushUndo();
        const s = findSlot(no);
        if (s) { const b = round().boards[s.bi]; if (s.side === 'white') b.whiteNo = null; else b.blackNo = null; }
        round().boards.push({ board: 0, whiteNo: no, blackNo: null, result: '' });
        clearSel(); renderAll();
        toast(L.byeMade || 'Bye assigned');
    }

    function detach(no) {
        const s = findSlot(no);
        if (s) { const b = round().boards[s.bi]; if (s.side === 'white') b.whiteNo = null; else b.blackNo = null; }
    }

    // Создать новую пару из двух свободных игроков (перетаскивание/тап одного на другого). Перетащенный — белые.
    function createPair(white, black) {
        if (white === black) { clearSel(); renderAll(); return; }
        pushUndo();
        detach(white); detach(black);
        round().boards.push({ board: 0, whiteNo: white, blackNo: black, result: '' });
        clearSel(); renderAll();
        toast(L.pairMade || 'Pair created');
    }

    // Сменить порядок доски: вставить перетащенную ПЕРЕД целевой.
    function moveBoard(from, to) {
        if (from === to) return;
        pushUndo();
        const arr = round().boards;
        const item = arr.splice(from, 1)[0];
        arr.splice(from < to ? to - 1 : to, 0, item);
        clearSel(); renderAll();
    }

    // Упорядочить доски по силе пары (очки↓, рейтинг↓); баи/пустые — вниз. Как «правильный» порядок досок.
    function sortBoards() {
        pushUndo();
        const sv = (no) => { const s = pts.get(no); return s ? s.pts : 0; };
        const rv = (no) => { const p = byNo.get(no); return p && p.rating ? p.rating : 0; };
        round().boards.sort((a, b) => {
            const ea = (a.whiteNo == null || a.blackNo == null) ? 1 : 0;
            const eb = (b.whiteNo == null || b.blackNo == null) ? 1 : 0;
            return ea - eb
                || Math.max(sv(b.whiteNo), sv(b.blackNo)) - Math.max(sv(a.whiteNo), sv(a.blackNo))
                || Math.max(rv(b.whiteNo), rv(b.blackNo)) - Math.max(rv(a.whiteNo), rv(a.blackNo));
        });
        renderAll();
    }

    // Свободные игроки текущего тура, отсортированные как для пейринга: очки↓ → рейтинг↓ → стартовый №.
    function freePlayers() {
        return (doc.players || []).filter(p => statusOf(p.no) === 'free')
            .map(p => ({ p, score: (pts.get(p.no)?.pts) || 0 }))
            .sort((a, b) => b.score - a.score || (b.p.rating || 0) - (a.p.rating || 0) || a.p.no - b.p.no)
            .map(x => x.p);
    }

    // Досадить остаток: спарить всех свободных соседними по силе (1-2, 3-4, …); нечётному — бай.
    function autoPairRemaining() {
        const free = freePlayers();
        if (!free.length) { toast(L.noFree || 'No free players'); return; }
        pushUndo();
        const arr = round().boards;
        let i = 0;
        for (; i + 1 < free.length; i += 2) arr.push({ board: 0, whiteNo: free[i].no, blackNo: free[i + 1].no, result: '' });
        if (i < free.length) arr.push({ board: 0, whiteNo: free[i].no, blackNo: null, result: '' }); // нечётный → бай
        clearSel(); renderAll();
        toast(L.autoPaired || 'Free players paired');
    }

    function flipResult(res) {
        return res === '1-0' ? '0-1' : res === '0-1' ? '1-0'
            : res === '+/-' ? '-/+' : res === '-/+' ? '+/-' : res;
    }

    function swapColors(bi) {
        pushUndo();
        const b = round().boards[bi];
        const w = b.whiteNo; b.whiteNo = b.blackNo; b.blackNo = w;
        b.result = flipResult(b.result);
        renderAll();
    }

    function removeBoard(bi) {
        pushUndo();
        round().boards.splice(bi, 1);
        clearSel(); renderAll();
        toast(L.boardRemoved || 'Board removed', L.undo || 'Undo', undo);
    }

    function addBoard() { pushUndo(); round().boards.push({ board: 0, whiteNo: null, blackNo: null, result: '' }); renderAll(); }
    function setResult(bi, res) { pushUndo(); const b = round().boards[bi]; b.result = b.result === res ? '' : res; renderAll(); }
    // Форфейт-цикл: нет → победа белых (+/-) → победа чёрных (-/+) → нет.
    function cycleForfeit(bi) { pushUndo(); const b = round().boards[bi]; b.result = b.result === '+/-' ? '-/+' : b.result === '-/+' ? '' : '+/-'; renderAll(); }

    function selectPlayer(no) { sel = (sel === no) ? null : no; renderAll(); }
    function clearSel() { sel = null; }

    // ---------------- Тосты ----------------

    function toast(text, actionLabel, actionFn) {
        if (!toastEl) return;
        toastEl.innerHTML = `<span>${esc(text)}</span>` + (actionLabel ? `<button class="pr-toast-act">${esc(actionLabel)}</button>` : '');
        toastEl.hidden = false; toastEl.classList.add('show');
        if (actionFn) toastEl.querySelector('.pr-toast-act').onclick = () => { actionFn(); hideToast(); };
        clearTimeout(toastTimer);
        toastTimer = setTimeout(hideToast, actionLabel ? 6000 : 3000);
    }
    function hideToast() { if (toastEl) { toastEl.classList.remove('show'); toastEl.hidden = true; } }

    // ---------------- Рендеринг ----------------

    function renderAll() {
        if (!doc) return;
        pts = computePoints();
        renderBar();
        renderRounds();
        renderHead();
        renderView();
        renderPool();
        renderValidation();
        renderSelbar();
        editorEl.classList.toggle('pr-selecting', sel != null);
        if (undoBtn) undoBtn.disabled = !undoStack.length;
        if (redoBtn) redoBtn.disabled = !redoStack.length;
    }

    function renderBar() {
        titleEl.textContent = doc.title || '';
        const np = (doc.players || []).length;
        metaEl.textContent = `${np} ${L.players || 'players'} · ${doc.rounds.length} ${L.roundsWord || 'rounds'}`;
    }

    function renderRounds() {
        roundsEl.innerHTML = doc.rounds.map((rd, i) =>
            `<button class="pr-rtab${i === ri ? ' on' : ''}" data-ri="${i}" role="tab" aria-selected="${i === ri}">${esc(L.round || 'Round')} ${rd.number || i + 1}${i === curRoundIdx ? ` · ${esc(L.current || 'current')}` : ''}</button>`
        ).join('');
    }

    function renderHead() {
        const rd = round();
        headEl.textContent = rd.schedule
            ? `${L.round || 'Round'} ${rd.number || ri + 1} — ${rd.schedule}`
            : `${L.round || 'Round'} ${rd.number || ri + 1}`;
    }

    function renderView() {
        const standings = view === 'standings';
        standingsEl.hidden = !standings;
        boardsEl.hidden = standings;
        const tools = addBoardEl ? addBoardEl.parentElement : null;
        if (tools) tools.hidden = standings;
        if (hintEl) hintEl.hidden = standings;
        if (roundHeadEl) roundHeadEl.hidden = standings;
        roundsEl.hidden = standings;
        document.getElementById('pr-view-pairs')?.classList.toggle('on', !standings);
        document.getElementById('pr-view-standings')?.classList.toggle('on', standings);
        if (standings) renderStandings(); else renderBoards();
    }

    function dupSet() {
        const seen = new Map(), dups = new Set();
        for (const b of round().boards)
            for (const no of [b.whiteNo, b.blackNo])
                if (no != null) { if (seen.has(no)) dups.add(no); else seen.set(no, 1); }
        return dups;
    }

    function chipHtml(no, dups) {
        const p = byNo.get(no);
        const name = p ? p.name : '#' + no;
        const s = pts.get(no);
        const rtg = p && p.rating ? `<i class="pr-rtg">${p.rating}</i>` : '';
        const ppill = (s && (s.games > 0 || s.pts > 0)) ? `<span class="pr-pts" title="${esc(L.stPts || 'Pts')}">${fmtPts(s.pts)}</span>` : '';
        const cls = 'pr-chip' + (sel === no ? ' sel lifted' : '') + (dups.has(no) ? ' dup' : '');
        return `<span class="${cls}" draggable="true" data-no="${no}"><b>${no}</b> <span class="pr-nm">${esc(name)}</span> ${rtg}${ppill}</span>`;
    }

    function slotHtml(no, bi, side, dups) {
        const dot = `<span class="pr-dot pr-dot-${side === 'white' ? 'w' : 'b'}" aria-hidden="true"></span>`;
        const inner = no != null ? chipHtml(no, dups) : `<span class="pr-empty">+ ${esc(L.empty || 'empty')}</span>`;
        return side === 'white'
            ? `<div class="pr-slot pr-w" data-bi="${bi}" data-side="white">${dot}${inner}</div>`
            : `<div class="pr-slot pr-b" data-bi="${bi}" data-side="black">${inner}${dot}</div>`;
    }

    function resDisp(res) {
        return res === '1-0' ? '1–0' : res === '0-1' ? '0–1' : res === '½-½' ? '½'
            : res === '+/-' ? '+ −' : res === '-/+' ? '− +' : '';
    }

    function resultHtml(b, bi) {
        if (b.whiteNo == null || b.blackNo == null) return `<div class="pr-res"><span class="pr-bye">${esc(L.bye || 'Bye')}</span></div>`;
        const ff = b.result === '+/-' || b.result === '-/+';
        const opt = (res, label) => `<button class="pr-rbtn${b.result === res ? ' on' : ''}" data-res="${res}" data-bi="${bi}">${label}</button>`;
        return `<div class="pr-res" role="group" aria-label="${esc(L.result || 'Result')}">
            ${opt('1-0', '1–0')}${opt('½-½', '½')}${opt('0-1', '0–1')}
            <button class="pr-rbtn pr-ff${ff ? ' on' : ''}" data-ff="${bi}" title="${esc(L.forfeit || 'Forfeit')}" aria-label="${esc(L.forfeit || 'Forfeit')}">⚑</button>
            ${ff ? `<span class="pr-ff-lbl">${resDisp(b.result)}</span>` : ''}
        </div>`;
    }

    function renderBoards() {
        const r = round(), dups = dupSet();
        boardsEl.innerHTML = r.boards.map((b, bi) => `
            <div class="pr-bd${(b.whiteNo != null && dups.has(b.whiteNo)) || (b.blackNo != null && dups.has(b.blackNo)) ? ' conflict' : ''}" data-bi="${bi}" tabindex="0">
                <span class="pr-bd-n" draggable="true" title="${esc(L.reorder || '')}">${bi + 1}</span>
                ${slotHtml(b.whiteNo, bi, 'white', dups)}
                ${resultHtml(b, bi)}
                ${slotHtml(b.blackNo, bi, 'black', dups)}
                <div class="pr-bd-acts">
                    <button class="pr-iconbtn" data-act="swap" data-bi="${bi}" title="${esc(L.swapColors || 'Swap colors')}" aria-label="${esc(L.swapColors || 'Swap colors')}">⇄</button>
                    <button class="pr-iconbtn" data-act="rm" data-bi="${bi}" title="${esc(L.removeBoard || 'Remove board')}" aria-label="${esc(L.removeBoard || 'Remove board')}">✕</button>
                </div>
            </div>`).join('') || `<p class="pr-muted">—</p>`;
        // Вернуть фокус на доску после ввода результата с клавиатуры (поток «1 → следующая доска»).
        if (focusBi != null) { const el = boardsEl.querySelector(`.pr-bd[data-bi="${Math.min(focusBi, r.boards.length - 1)}"]`); focusBi = null; if (el) el.focus(); }
    }

    function renderStandings() {
        const arr = [...pts.entries()].map(([no, s]) => ({ no, ...s, p: byNo.get(no) }))
            .sort((a, b) => b.pts - a.pts || b.wins - a.wins || (b.p?.rating || 0) - (a.p?.rating || 0)
                || (a.p?.name || '').localeCompare(b.p?.name || ''));
        if (!arr.length) { standingsEl.innerHTML = `<p class="pr-muted">${esc(L.stEmpty || '')}</p>`; return; }
        const rows = arr.map((s, i) => `<tr>
            <td class="pr-st-place">${i + 1}</td>
            <td class="pr-st-name"><b>${s.no}</b> <span>${esc(s.p ? s.p.name : '#' + s.no)}</span>${s.p && s.p.rating ? ` <i>${s.p.rating}</i>` : ''}</td>
            <td class="pr-st-pts">${fmtPts(s.pts)}</td>
            <td>${s.games}</td><td>${s.wins}</td><td>${s.draws}</td><td>${s.losses}</td>
            <td class="pr-st-col">${s.white}/${s.black}</td>
        </tr>`).join('');
        standingsEl.innerHTML = `<div class="pr-st-wrap"><table class="pr-st-tbl"><thead><tr>
            <th>#</th><th class="pr-st-name">${esc(L.stPlayer || 'Player')}</th><th>${esc(L.stPts || 'Pts')}</th>
            <th>${esc(L.stGames || 'G')}</th><th>${esc(L.stWins || 'W')}</th><th>${esc(L.stDraws || 'D')}</th>
            <th>${esc(L.stLosses || 'L')}</th><th>${esc(L.stColors || 'W/B')}</th>
        </tr></thead><tbody>${rows}</tbody></table></div>`;
    }

    function markDrop(bd) { clearDropMark(); bd.classList.add('drop-before'); }
    function clearDropMark() { boardsEl.querySelectorAll('.pr-bd.drop-before').forEach(b => b.classList.remove('drop-before')); }

    function statusOf(no) {
        const s = findSlot(no);
        if (!s) return 'free';
        const b = round().boards[s.bi];
        return (b.whiteNo != null && b.blackNo != null) ? 'paired' : 'bye';
    }

    function renderPool() {
        const q = (poolSearch && poolSearch.value || '').trim().toLowerCase();
        // Пул — рабочий список: свободные вверху (их надо посадить), затем баи, затем в паре. Внутри группы —
        // по очкам↓, рейтингу↓, стартовому №↑ (пейринг идёт по очкам; без результатов это = «по рейтингу»).
        const rank = { free: 0, bye: 1, paired: 2 };
        let list = (doc.players || []).map(p => {
            const st = statusOf(p.no), s = pts.get(p.no);
            return { p, st, score: s ? s.pts : 0 };
        });
        if (q) list = list.filter(x => x.p.name.toLowerCase().includes(q) || String(x.p.no) === q);
        list.sort((a, b) => rank[a.st] - rank[b.st] || b.score - a.score
            || (b.p.rating || 0) - (a.p.rating || 0) || a.p.no - b.p.no);

        const badge = { paired: L.statusPaired || 'paired', bye: L.statusBye || 'bye', free: L.statusFree || 'free' };
        const grp = { free: L.poolFree || 'Free', bye: L.poolBye || 'Byes', paired: L.poolPaired || 'Paired' };
        const counts = { free: 0, bye: 0, paired: 0 };
        for (const x of list) counts[x.st]++;

        let html = '', lastGrp = null;
        for (const x of list) {
            // Заголовки групп — только без поиска (при поиске нужен плоский список совпадений).
            if (!q && x.st !== lastGrp) { lastGrp = x.st; html += `<div class="pr-pool-grp">${esc(grp[x.st])} <span>${counts[x.st]}</span></div>`; }
            const p = x.p, rtg = p.rating ? `<i class="pr-rtg">${p.rating}</i>` : '';
            html += `<div class="pr-pchip st-${x.st}${sel === p.no ? ' sel' : ''}" draggable="true" data-no="${p.no}">
                <b>${p.no}</b> <span class="pr-nm">${esc(p.name)}</span> ${rtg}
                <span class="pr-st pr-st-${x.st}">${esc(badge[x.st])}</span>
            </div>`;
        }
        poolEl.innerHTML = html || `<p class="pr-muted">—</p>`;
    }

    function renderValidation() {
        const r = round(), issues = [];
        const dups = dupSet();
        if (dups.size) {
            const names = [...dups].map(no => (byNo.get(no)?.name) || '#' + no).join(', ');
            issues.push({ kind: 'err', text: `${L.dupConflict || 'Duplicate'}: ${names}` });
        }
        const assigned = new Set();
        let byeCount = 0;
        for (const b of r.boards) {
            for (const no of [b.whiteNo, b.blackNo]) if (no != null) assigned.add(no);
            if ((b.whiteNo == null) !== (b.blackNo == null)) byeCount++;
        }
        const unpaired = (doc.players || []).filter(p => !assigned.has(p.no));
        const total = (doc.players || []).length;
        if (total % 2 === 1 && byeCount === 0)
            issues.push({ kind: 'warn', text: L.byeWarn || 'Odd number of players — one bye expected.' });
        if (byeCount > 1)
            issues.push({ kind: 'warn', text: `${L.byeExtra || 'Multiple byes'} (${byeCount}).` });
        if (unpaired.length)
            issues.push({ kind: 'warn', text: (L.unpaired || 'Unpaired: {0}').replace('{0}', unpaired.map(p => p.name).join(', ')) });

        validEl.innerHTML = issues.length
            ? issues.map(i => `<div class="pr-issue pr-issue-${i.kind}">${esc(i.text)}</div>`).join('')
            : `<div class="pr-issue pr-issue-ok">${esc(L.okValid || 'Looks valid ✓')}</div>`;
    }

    // Плавающая панель выбранного игрока (видна при выборе; удобна на телефоне).
    function renderSelbar() {
        if (!selbarEl) return;
        if (sel == null) { selbarEl.hidden = true; selbarEl.classList.remove('show'); return; }
        const p = byNo.get(sel);
        selbarEl.innerHTML =
            `<span class="pr-selbar-nm">${esc(p ? p.name : '#' + sel)}</span>
             <span class="pr-selbar-hint">${esc(L.selectHint || '')}</span>
             <span class="pr-selbar-acts">
               <button data-sb="bye">${esc(L.giveBye || 'Bye')}</button>
               <button data-sb="unassign">${esc(L.unassign || 'Remove')}</button>
               <button data-sb="cancel">${esc(L.cancel || 'Cancel')}</button>
             </span>`;
        selbarEl.hidden = false; selbarEl.classList.add('show');
    }

    // ---------------- Быстрый выбор свободного на пустое место ----------------

    function openPick(bi, side, anchorEl) {
        if (!pickEl) return;
        pickBi = bi; pickSide = side;
        pickEl.innerHTML = `<input class="pr-pick-search" type="search" placeholder="${esc(L.pickSearch || '')}" aria-label="${esc(L.pickSearch || '')}"><div class="pr-pick-list"></div>`;
        renderPickList('');
        const r = anchorEl.getBoundingClientRect();
        const w = 260;
        pickEl.style.left = Math.max(8, Math.min(r.left, window.innerWidth - w - 8)) + 'px';
        pickEl.style.top = Math.min(r.bottom + 4, window.innerHeight - 24) + 'px';
        pickEl.hidden = false; pickEl.classList.add('show');
        pickEl.querySelector('.pr-pick-search').oninput = (e) => renderPickList(e.target.value);
        pickEl.querySelector('.pr-pick-search').focus();
    }

    function renderPickList(q) {
        const query = (q || '').trim().toLowerCase();
        const free = freePlayers().filter(p => !query || p.name.toLowerCase().includes(query) || String(p.no) === query);
        const list = pickEl.querySelector('.pr-pick-list');
        list.innerHTML = free.map(p =>
            `<button class="pr-pick-item" data-no="${p.no}"><b>${p.no}</b> <span class="pr-nm">${esc(p.name)}</span>${p.rating ? ` <i class="pr-rtg">${p.rating}</i>` : ''}</button>`
        ).join('') || `<p class="pr-muted pr-pick-empty">${esc(L.noFree || 'No free players')}</p>`;
    }

    function closePick() { if (pickEl) { pickEl.hidden = true; pickEl.classList.remove('show'); } pickBi = null; pickSide = null; }

    // ---------------- Ввод результата с клавиатуры ----------------

    function focusRow(idx) {
        const n = round().boards.length; if (!n) return;
        const i = Math.max(0, Math.min(idx, n - 1));
        boardsEl.querySelector(`.pr-bd[data-bi="${i}"]`)?.focus();
    }

    function boardKeydown(e) {
        const bd = e.target.closest('.pr-bd'); if (!bd) return;
        const bi = +bd.dataset.bi, b = round().boards[bi];
        if (e.key === 'ArrowDown') { e.preventDefault(); focusRow(bi + 1); return; }
        if (e.key === 'ArrowUp') { e.preventDefault(); focusRow(bi - 1); return; }
        const both = b.whiteNo != null && b.blackNo != null;
        if (!both) return; // на бай-доске результат не вводим
        if (e.key === 'f' || e.key === 'F' || e.key === 'а' || e.key === 'А') { e.preventDefault(); focusBi = bi; cycleForfeit(bi); return; }
        let res = null;
        if (e.key === '1') res = '1-0';
        else if (e.key === '0') res = '0-1';
        else if (e.key === '=' || e.key === '5') res = '½-½';
        else if (e.key === 'Backspace' || e.key === 'Delete') res = '';
        else return;
        e.preventDefault();
        pushUndo();
        b.result = res;                              // прямое присвоение (не toggle): «1» всегда = победа белых
        focusBi = (res === '' ? bi : bi + 1);        // после ввода — к следующей доске (быстрый поток вниз)
        renderAll();
    }

    // ---------------- Экспорт ----------------

    function slugify(s) { return (s || 'pairings').toLowerCase().replace(/[^a-z0-9а-я]+/gi, '-').replace(/^-+|-+$/g, '').slice(0, 60) || 'pairings'; }

    function download(name, text, mime) {
        const blob = new Blob(['﻿' + text], { type: mime || 'text/plain;charset=utf-8' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url; a.download = name; document.body.appendChild(a); a.click(); a.remove();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    }

    function nm(no) { const p = no != null && byNo.get(no); return p ? p.name : (no != null ? '#' + no : ''); }
    function rt(no) { const p = no != null && byNo.get(no); return p && p.rating ? p.rating : ''; }

    function exportCsv() {
        const q = (s) => `"${String(s ?? '').replace(/"/g, '""')}"`;
        const rows = [['Round', 'Board', 'WhiteNo', 'White', 'WhiteRating', 'Result', 'BlackNo', 'Black', 'BlackRating']];
        doc.rounds.forEach(rd => rd.boards.forEach((b, i) =>
            rows.push([rd.number, i + 1, b.whiteNo ?? '', nm(b.whiteNo), rt(b.whiteNo), b.result || '', b.blackNo ?? '', nm(b.blackNo), rt(b.blackNo)])));
        download(slugify(doc.title) + '.csv', rows.map(r => r.map(q).join(',')).join('\r\n'), 'text/csv;charset=utf-8');
    }

    function pgnResult(res) {
        return res === '1-0' || res === '+/-' ? '1-0' : res === '0-1' || res === '-/+' ? '0-1' : res === '½-½' ? '1/2-1/2' : '*';
    }

    function exportPgn() {
        const out = [];
        doc.rounds.forEach(rd => rd.boards.forEach((b, i) => {
            if (b.whiteNo == null && b.blackNo == null) return;
            out.push(
                `[Event "${(doc.title || 'Tournament').replace(/"/g, "'")}"]`,
                `[Round "${rd.number || ''}"]`,
                `[Board "${i + 1}"]`,
                `[White "${nm(b.whiteNo).replace(/"/g, "'") || '?'}"]`,
                `[Black "${nm(b.blackNo).replace(/"/g, "'") || '?'}"]`,
                `[Result "${pgnResult(b.result)}"]`,
                '', pgnResult(b.result), '');
        }));
        download(slugify(doc.title) + '.pgn', out.join('\n'), 'application/x-chess-pgn');
    }

    function printRound() {
        const rd = round();
        const rows = rd.boards.map((b, i) => `<tr>
            <td class="n">${i + 1}</td>
            <td class="w">${esc(nm(b.whiteNo) || '—')}${rt(b.whiteNo) ? ` <span>(${rt(b.whiteNo)})</span>` : ''}</td>
            <td class="r">${esc((b.whiteNo == null || b.blackNo == null) ? (L.bye || 'Bye') : (resDisp(b.result) || '–'))}</td>
            <td class="b">${esc(nm(b.blackNo) || '—')}${rt(b.blackNo) ? ` <span>(${rt(b.blackNo)})</span>` : ''}</td>
        </tr>`).join('');
        let area = document.getElementById('pr-print-area');
        if (!area) { area = document.createElement('div'); area.id = 'pr-print-area'; document.body.appendChild(area); }
        area.innerHTML = `<h1>${esc(doc.title || (L.printTitle || 'Pairings'))}</h1>
            <h2>${esc(L.round || 'Round')} ${rd.number || ri + 1}${rd.schedule ? ' — ' + esc(rd.schedule) : ''}</h2>
            <table class="pr-print-tbl"><thead><tr>
              <th>#</th><th>${esc(L.white || 'White')}</th><th>${esc(L.result || 'Result')}</th><th>${esc(L.black || 'Black')}</th>
            </tr></thead><tbody>${rows}</tbody></table>`;
        window.print();
    }

    // ---------------- Привязка событий ----------------

    function bindDocOnce() {
        if (window.__prBound) return;
        window.__prBound = true;
        document.addEventListener('keydown', (e) => {
            if (e.key === 'Escape') {
                if (pickEl && !pickEl.hidden) { closePick(); return; }
                if (sel != null) { clearSel(); renderAll(); return; }
            }
            if (!doc || !editorEl || editorEl.hidden) return;
            const t = e.target;
            if (t && /^(input|textarea|select)$/i.test(t.tagName)) return;
            const mod = e.ctrlKey || e.metaKey;
            if (mod && (e.key === 'z' || e.key === 'Z')) { e.preventDefault(); e.shiftKey ? redo() : undo(); }
            else if (mod && (e.key === 'y' || e.key === 'Y')) { e.preventDefault(); redo(); }
        });
        // Закрыть поповер быстрого выбора по клику вне его (но не по пустому слоту, который его открывает).
        document.addEventListener('pointerdown', (e) => {
            if (pickEl && !pickEl.hidden && !e.target.closest('#pr-pick') && !e.target.closest('.pr-empty')) closePick();
        });
    }

    function bindEditor() {
        roundsEl.onclick = (e) => { const t = e.target.closest('[data-ri]'); if (t) { ri = +t.dataset.ri; sel = null; renderAll(); } };

        document.getElementById('pr-view-pairs').onclick = () => { if (view !== 'pairs') { view = 'pairs'; sel = null; renderAll(); } };
        document.getElementById('pr-view-standings').onclick = () => { if (view !== 'standings') { view = 'standings'; sel = null; renderAll(); } };

        boardsEl.onclick = (e) => {
            const act = e.target.closest('[data-act]');
            if (act) { act.dataset.act === 'swap' ? swapColors(+act.dataset.bi) : removeBoard(+act.dataset.bi); return; }
            const ff = e.target.closest('[data-ff]');
            if (ff) { cycleForfeit(+ff.dataset.ff); return; }
            const rb = e.target.closest('[data-res]');
            if (rb) { setResult(+rb.dataset.bi, rb.dataset.res); return; }
            const slot = e.target.closest('.pr-slot');
            if (!slot) return;
            const bi = +slot.dataset.bi, side = slot.dataset.side;
            const chip = e.target.closest('.pr-chip');
            if (sel == null) {
                if (chip) selectPlayer(+chip.dataset.no);
                else if (slot.querySelector('.pr-empty')) openPick(bi, side, slot); // пустое место → быстрый выбор
                return;
            }
            if (chip && +chip.dataset.no === sel) { clearSel(); renderAll(); return; }
            placePlayer(sel, bi, side);
        };
        boardsEl.addEventListener('keydown', boardKeydown);
        boardsEl.addEventListener('dragstart', (e) => {
            const grip = e.target.closest('.pr-bd-n');     // тащим номер → меняем порядок доски
            if (grip) { dragBoardBi = +grip.closest('.pr-bd').dataset.bi; e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', 'board'); return; }
            const chip = e.target.closest('.pr-chip');     // тащим фишку → двигаем игрока
            if (chip) { dragNo = +chip.dataset.no; e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', String(dragNo)); }
        });
        boardsEl.addEventListener('dragover', (e) => {
            if (dragBoardBi != null) { const bd = e.target.closest('.pr-bd'); if (bd) { e.preventDefault(); markDrop(bd); } return; }
            if (e.target.closest('.pr-slot')) e.preventDefault();
        });
        boardsEl.addEventListener('drop', (e) => {
            if (dragBoardBi != null) {
                const bd = e.target.closest('.pr-bd'); if (bd) { e.preventDefault(); moveBoard(dragBoardBi, +bd.dataset.bi); }
                dragBoardBi = null; clearDropMark(); return;
            }
            const slot = e.target.closest('.pr-slot'); if (!slot) return;
            e.preventDefault();
            const no = dragNo != null ? dragNo : parseInt(e.dataTransfer.getData('text/plain'), 10);
            if (!isNaN(no)) placePlayer(no, +slot.dataset.bi, slot.dataset.side);
            dragNo = null;
        });
        boardsEl.addEventListener('dragend', () => { dragBoardBi = null; dragNo = null; clearDropMark(); });

        poolEl.onclick = (e) => {
            const chip = e.target.closest('.pr-pchip');
            if (chip) {
                const no = +chip.dataset.no;
                // Выбран свободный + кликнули по другому свободному → новая пара (удобно и на тач).
                if (sel != null && sel !== no && statusOf(sel) === 'free' && statusOf(no) === 'free') { createPair(sel, no); return; }
                selectPlayer(no); return;
            }
            if (sel != null) unassign(sel); // клик по пустому месту пула — снять выбранного
        };
        poolEl.addEventListener('dragstart', (e) => {
            const chip = e.target.closest('.pr-pchip');
            if (chip) { dragNo = +chip.dataset.no; e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', String(dragNo)); }
        });
        poolEl.addEventListener('dragover', (e) => e.preventDefault());
        poolEl.addEventListener('drop', (e) => {
            e.preventDefault();
            const no = dragNo != null ? dragNo : parseInt(e.dataTransfer.getData('text/plain'), 10);
            dragNo = null;
            if (isNaN(no)) return;
            // Дроп на другого СВОБОДНОГО → создать пару; иначе — снять с тура.
            const target = e.target.closest('.pr-pchip');
            if (target) {
                const tno = +target.dataset.no;
                if (tno !== no && statusOf(no) === 'free' && statusOf(tno) === 'free') { createPair(no, tno); return; }
            }
            unassign(no);
        });
        if (poolSearch) poolSearch.oninput = renderPool;

        selbarEl.onclick = (e) => {
            const b = e.target.closest('[data-sb]'); if (!b || sel == null) return;
            const a = b.dataset.sb;
            if (a === 'bye') byePlayer(sel);
            else if (a === 'unassign') unassign(sel);
            else { clearSel(); renderAll(); }
        };

        // Поповер быстрого выбора: клик по игроку → посадить на запомненный слот.
        pickEl.onclick = (e) => {
            const it = e.target.closest('.pr-pick-item'); if (!it) return;
            const no = +it.dataset.no, bi = pickBi, side = pickSide;
            closePick();
            if (bi != null) placePlayer(no, bi, side);
        };

        undoBtn.onclick = undo;
        redoBtn.onclick = redo;
        addBoardEl.onclick = addBoard;
        document.getElementById('pr-pairrest').onclick = autoPairRemaining;
        document.getElementById('pr-sortboards').onclick = sortBoards;
        document.getElementById('pr-print').onclick = printRound;
        document.getElementById('pr-pgn').onclick = exportPgn;
        document.getElementById('pr-csv').onclick = exportCsv;
        document.getElementById('pr-reset').onclick = reset;
    }

    function bindImport() {
        fileEl.onchange = () => { if (fileEl.files && fileEl.files[0]) importFile(fileEl.files[0]); };
        dropEl.addEventListener('dragover', (e) => { e.preventDefault(); dropEl.classList.add('over'); });
        dropEl.addEventListener('dragleave', () => dropEl.classList.remove('over'));
        dropEl.addEventListener('drop', (e) => {
            e.preventDefault(); dropEl.classList.remove('over');
            const f = e.dataTransfer.files && e.dataTransfer.files[0];
            if (f) importFile(f);
        });
        urlForm.onsubmit = (e) => { e.preventDefault(); importUrl(document.getElementById('pr-url').value); };
    }

    // ---------------- Инициализация ----------------

    function setup() {
        const root = document.getElementById('pr-root');
        if (!root) return; // не страница жеребьёвки — no-op
        teardown();

        try { L = JSON.parse(document.getElementById('pr-loc')?.textContent || '{}'); } catch { L = {}; }

        importEl = document.getElementById('pr-import');
        editorEl = document.getElementById('pr-editor');
        msgEl = document.getElementById('pr-msg');
        fileEl = document.getElementById('pr-file');
        dropEl = document.getElementById('pr-drop');
        urlForm = document.getElementById('pr-url-form');
        roundsEl = document.getElementById('pr-rounds');
        headEl = roundHeadEl = document.getElementById('pr-round-head');
        boardsEl = document.getElementById('pr-boards');
        standingsEl = document.getElementById('pr-standings');
        poolEl = document.getElementById('pr-pool');
        poolSearch = document.getElementById('pr-pool-search');
        validEl = document.getElementById('pr-valid');
        titleEl = document.getElementById('pr-title');
        metaEl = document.getElementById('pr-meta');
        selbarEl = document.getElementById('pr-selbar');
        toastEl = document.getElementById('pr-toast');
        pickEl = document.getElementById('pr-pick');
        undoBtn = document.getElementById('pr-undo');
        redoBtn = document.getElementById('pr-redo');
        addBoardEl = document.getElementById('pr-addboard');
        hintEl = document.querySelector('#pr-editor .pr-hint');
        if (!importEl || !editorEl || !boardsEl || !standingsEl || !selbarEl) return;

        editorEl.hidden = true; importEl.hidden = false; showMsg('', '');
        if (selbarEl) { selbarEl.hidden = true; selbarEl.classList.remove('show'); }
        hideToast();

        bindImport();
        bindEditor();
        bindDocOnce();
    }

    // Инициализация ровно один раз на появившийся #pr-root. Флаг на самом узле ловит и enhanced-навигацию с
    // морфингом узла (грабля #13), и не самозапускается на перерисовке досок (#pr-root тот же узел).
    function tryInit() {
        const root = document.getElementById('pr-root');
        if (!root || root.dataset.prReady === '1') return;
        root.dataset.prReady = '1';
        setup();
    }

    function watch() {
        if (window.__prObserver) return;
        window.__prObserver = new MutationObserver(() => {
            if (window.__prRaf) return;
            window.__prRaf = requestAnimationFrame(() => { window.__prRaf = 0; tryInit(); });
        });
        window.__prObserver.observe(document.documentElement, { childList: true, subtree: true });
    }

    watch();
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', tryInit);
    else tryInit();
    document.addEventListener('enhancedload', tryInit);
})();
