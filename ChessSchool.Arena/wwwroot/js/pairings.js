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
    let ri = 0;                // активный тур (индекс)
    let sel = null;            // выбранный игрок (no) для клик-свапа
    let dragNo = null;         // перетаскиваемый игрок (no)
    // DOM-узлы
    let importEl, editorEl, msgEl, fileEl, dropEl, urlForm, roundsEl, headEl, boardsEl,
        poolEl, poolSearch, validEl, titleEl, metaEl;

    function teardown() { setupGen++; doc = null; byNo = new Map(); ri = 0; sel = null; dragNo = null; }

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
        // Доски тура — по возрастанию номера (верхние доски сверху, как в жеребьёвке).
        doc.rounds.forEach(rd => rd.boards.sort((a, b) => (a.board || 0) - (b.board || 0)));
        ri = 0; sel = null;
        showMsg('', '');
        importEl.hidden = true;
        editorEl.hidden = false;
        renderAll();
    }

    function reset() {
        if (doc && !confirm(L.confirmReset || 'Start over?')) return;
        doc = null; sel = null;
        editorEl.hidden = true;
        importEl.hidden = false;
        if (fileEl) fileEl.value = '';
        const u = document.getElementById('pr-url'); if (u) u.value = '';
        showMsg('', '');
    }

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
        if (occ === no) { clearSel(); return; }
        const old = findSlot(no);
        if (side === 'white') b.whiteNo = no; else b.blackNo = no;
        if (old && !(old.bi === bi && old.side === side)) {
            const ob = r.boards[old.bi];
            if (old.side === 'white') ob.whiteNo = occ; else ob.blackNo = occ; // свап (occ может быть null)
        }
        // Если игрок был из пула (old===null), вытесненный occ просто становится свободным (пул).
        clearSel(); renderAll();
    }

    function unassign(no) {
        const s = findSlot(no);
        if (!s) { clearSel(); return; }
        const b = round().boards[s.bi];
        if (s.side === 'white') b.whiteNo = null; else b.blackNo = null;
        clearSel(); renderAll();
    }

    function flipResult(res) {
        return res === '1-0' ? '0-1' : res === '0-1' ? '1-0'
            : res === '+/-' ? '-/+' : res === '-/+' ? '+/-' : res;
    }

    function swapColors(bi) {
        const b = round().boards[bi];
        const w = b.whiteNo; b.whiteNo = b.blackNo; b.blackNo = w;
        b.result = flipResult(b.result);
        renderAll();
    }

    function removeBoard(bi) { round().boards.splice(bi, 1); clearSel(); renderAll(); }
    function addBoard() { round().boards.push({ board: 0, whiteNo: null, blackNo: null, result: '' }); renderAll(); }
    function setResult(bi, res) { const b = round().boards[bi]; b.result = b.result === res ? '' : res; renderAll(); }

    function selectPlayer(no) { sel = (sel === no) ? null : no; renderAll(); }
    function clearSel() { sel = null; }

    // ---------------- Рендеринг ----------------

    function renderAll() {
        if (!doc) return;
        renderBar();
        renderRounds();
        renderHead();
        renderBoards();
        renderPool();
        renderValidation();
    }

    function renderBar() {
        titleEl.textContent = doc.title || '';
        const np = (doc.players || []).length;
        metaEl.textContent = `${np} ${L.players || 'players'} · ${doc.rounds.length} ${L.roundsWord || 'rounds'}`;
    }

    function renderRounds() {
        roundsEl.innerHTML = doc.rounds.map((rd, i) =>
            `<button class="pr-rtab${i === ri ? ' on' : ''}" data-ri="${i}" role="tab" aria-selected="${i === ri}">${esc(L.round || 'Round')} ${rd.number || i + 1}</button>`
        ).join('');
    }

    function renderHead() {
        const rd = round();
        headEl.textContent = rd.schedule ? `${L.round || 'Round'} ${rd.number || ri + 1} — ${rd.schedule}` : `${L.round || 'Round'} ${rd.number || ri + 1}`;
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
        const rtg = p && p.rating ? `<i class="pr-rtg">${p.rating}</i>` : '';
        const cls = 'pr-chip' + (sel === no ? ' sel' : '') + (dups.has(no) ? ' dup' : '');
        return `<span class="${cls}" draggable="true" data-no="${no}"><b>${no}</b> <span class="pr-nm">${esc(name)}</span> ${rtg}</span>`;
    }

    function slotHtml(no, bi, side, dups) {
        const inner = no != null ? chipHtml(no, dups) : `<span class="pr-empty">+ ${esc(L.empty || 'empty')}</span>`;
        return `<div class="pr-slot pr-${side === 'white' ? 'w' : 'b'}" data-bi="${bi}" data-side="${side}">${inner}</div>`;
    }

    function resultHtml(b, bi) {
        // Бай (одна сторона пуста) — результат не редактируем, показываем ярлык.
        if (b.whiteNo == null || b.blackNo == null) return `<div class="pr-res pr-bye">${esc(L.bye || 'Bye')}</div>`;
        const opt = (res, label) =>
            `<button class="pr-rbtn${b.result === res ? ' on' : ''}" data-res="${res}" data-bi="${bi}">${label}</button>`;
        return `<div class="pr-res" role="group" aria-label="${esc(L.result || 'Result')}">
            ${opt('1-0', '1–0')}${opt('½-½', '½')}${opt('0-1', '0–1')}
        </div>`;
    }

    function renderBoards() {
        const r = round(), dups = dupSet();
        boardsEl.innerHTML = r.boards.map((b, bi) => `
            <div class="pr-bd${(b.whiteNo != null && dups.has(b.whiteNo)) || (b.blackNo != null && dups.has(b.blackNo)) ? ' conflict' : ''}" data-bi="${bi}">
                <span class="pr-bd-n">${bi + 1}</span>
                ${slotHtml(b.whiteNo, bi, 'white', dups)}
                ${resultHtml(b, bi)}
                ${slotHtml(b.blackNo, bi, 'black', dups)}
                <div class="pr-bd-acts">
                    <button class="pr-iconbtn" data-act="swap" data-bi="${bi}" title="${esc(L.swapColors || 'Swap colors')}" aria-label="${esc(L.swapColors || 'Swap colors')}">⇄</button>
                    <button class="pr-iconbtn" data-act="rm" data-bi="${bi}" title="${esc(L.removeBoard || 'Remove board')}" aria-label="${esc(L.removeBoard || 'Remove board')}">✕</button>
                </div>
            </div>`).join('');
    }

    function statusOf(no) {
        const s = findSlot(no);
        if (!s) return 'free';
        const b = round().boards[s.bi];
        return (b.whiteNo != null && b.blackNo != null) ? 'paired' : 'bye';
    }

    function renderPool() {
        const q = (poolSearch && poolSearch.value || '').trim().toLowerCase();
        const players = (doc.players || []).slice().sort((a, b) => a.no - b.no);
        const items = players.filter(p => !q || p.name.toLowerCase().includes(q) || String(p.no) === q);
        const badge = { paired: L.statusPaired || 'paired', bye: L.statusBye || 'bye', free: L.statusFree || 'free' };
        poolEl.innerHTML = items.map(p => {
            const st = statusOf(p.no);
            const rtg = p.rating ? `<i class="pr-rtg">${p.rating}</i>` : '';
            return `<div class="pr-pchip st-${st}${sel === p.no ? ' sel' : ''}" draggable="true" data-no="${p.no}">
                <b>${p.no}</b> <span class="pr-nm">${esc(p.name)}</span> ${rtg}
                <span class="pr-st pr-st-${st}">${esc(badge[st])}</span>
            </div>`;
        }).join('') || `<p class="pr-muted">—</p>`;
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

        if (sel != null) {
            const p = byNo.get(sel);
            validEl.insertAdjacentHTML('afterbegin',
                `<div class="pr-selbar"><b>${esc(p ? p.name : '#' + sel)}</b> ${esc(L.selectHint || '')}
                 <button class="pr-unassign" id="pr-unassign-btn">${esc(L.unassign || 'Remove')}</button></div>`);
        }
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
            <td class="r">${esc((b.whiteNo == null || b.blackNo == null) ? (L.bye || 'Bye') : (b.result || '–'))}</td>
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
        document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && sel != null) { clearSel(); renderAll(); } });
    }

    function bindEditor() {
        // Туры.
        roundsEl.onclick = (e) => { const t = e.target.closest('[data-ri]'); if (t) { ri = +t.dataset.ri; sel = null; renderAll(); } };

        // Доски: выбор/свап игроков, результаты, действия.
        boardsEl.onclick = (e) => {
            const act = e.target.closest('[data-act]');
            if (act) { act.dataset.act === 'swap' ? swapColors(+act.dataset.bi) : removeBoard(+act.dataset.bi); return; }
            const rb = e.target.closest('[data-res]');
            if (rb) { setResult(+rb.dataset.bi, rb.dataset.res); return; }
            const slot = e.target.closest('.pr-slot');
            if (!slot) return;
            const bi = +slot.dataset.bi, side = slot.dataset.side;
            const chip = e.target.closest('.pr-chip');
            if (sel == null) { if (chip) selectPlayer(+chip.dataset.no); return; }
            if (chip && +chip.dataset.no === sel) { clearSel(); renderAll(); return; }
            placePlayer(sel, bi, side);
        };
        boardsEl.addEventListener('dragstart', (e) => {
            const chip = e.target.closest('.pr-chip');
            if (chip) { dragNo = +chip.dataset.no; e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', String(dragNo)); }
        });
        boardsEl.addEventListener('dragover', (e) => { if (e.target.closest('.pr-slot')) e.preventDefault(); });
        boardsEl.addEventListener('drop', (e) => {
            const slot = e.target.closest('.pr-slot'); if (!slot) return;
            e.preventDefault();
            const no = dragNo != null ? dragNo : parseInt(e.dataTransfer.getData('text/plain'), 10);
            if (!isNaN(no)) placePlayer(no, +slot.dataset.bi, slot.dataset.side);
            dragNo = null;
        });

        // Пул: выбор игрока; клик по пустому месту/дроп — снять с тура.
        poolEl.onclick = (e) => {
            const chip = e.target.closest('.pr-pchip');
            if (chip) { selectPlayer(+chip.dataset.no); return; }
            if (sel != null) unassign(sel);
        };
        poolEl.addEventListener('dragstart', (e) => {
            const chip = e.target.closest('.pr-pchip');
            if (chip) { dragNo = +chip.dataset.no; e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', String(dragNo)); }
        });
        poolEl.addEventListener('dragover', (e) => e.preventDefault());
        poolEl.addEventListener('drop', (e) => {
            e.preventDefault();
            const no = dragNo != null ? dragNo : parseInt(e.dataTransfer.getData('text/plain'), 10);
            if (!isNaN(no)) unassign(no);
            dragNo = null;
        });
        if (poolSearch) poolSearch.oninput = renderPool;

        // Снять выбранного с тура (кнопка в селект-баре валидации).
        validEl.onclick = (e) => { if (e.target.closest('#pr-unassign-btn') && sel != null) unassign(sel); };

        // Тулбар.
        document.getElementById('pr-addboard').onclick = addBoard;
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
        headEl = document.getElementById('pr-round-head');
        boardsEl = document.getElementById('pr-boards');
        poolEl = document.getElementById('pr-pool');
        poolSearch = document.getElementById('pr-pool-search');
        validEl = document.getElementById('pr-valid');
        titleEl = document.getElementById('pr-title');
        metaEl = document.getElementById('pr-meta');
        if (!importEl || !editorEl || !boardsEl) return;

        // Чистое состояние при каждом заходе (новый импорт).
        editorEl.hidden = true; importEl.hidden = false; showMsg('', '');

        bindImport();
        bindEditor();
        bindDocOnce();
    }

    // Старт по появлению #pr-root (enhanced-навигация не исполняет вставленный <script>, см. App.razor).
    function watch() {
        if (window.__prObserver) return;
        const relevant = (n) => n.nodeType === 1 && (n.matches?.('#pr-root') || n.querySelector?.('#pr-root'));
        window.__prObserver = new MutationObserver((records) => {
            if (!records.some(r => Array.from(r.addedNodes).some(relevant))) return;
            if (window.__prRaf) return;
            window.__prRaf = requestAnimationFrame(() => { window.__prRaf = 0; setup(); });
        });
        window.__prObserver.observe(document.documentElement, { childList: true, subtree: true });
    }

    watch();
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', setup);
    else setup();
    document.addEventListener('enhancedload', setup);
})();
