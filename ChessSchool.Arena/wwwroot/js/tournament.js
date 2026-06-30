// Тонкий клиент страницы турнира: подключается к /arenahub (SignalR), рендерит шапку/таблицу/доски/
// свою партию и анимирует часы локально. Снимает «состояние на зрителя» с веб-сервера (нет Blazor-circuit
// и серверных ре-рендеров по таймеру). Идемпотентен и переживает enhanced-navigation (как schedule.js).
(function () {
    const PIECE = (color, type) => `_content/ChessSchool.Design/pieces/${color}${type.toUpperCase()}.svg`;
    const pieceImg = (color, type, cls) => `<img class="${cls || 'cp'}" draggable="false" alt="" src="${PIECE(color, type)}">`;
    const esc = (s) => String(s ?? '').replace(/[&<>]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]));
    // Тег «бот» рядом с никнеймом (локализованная метка из L.bottag).
    const botTag = (isBot) => isBot ? ` <span class="bot-tag">${esc(L.bottag || 'BOT')}</span>` : '';
    const FILES = ['a', 'b', 'c', 'd', 'e', 'f', 'g', 'h'];

    // --- состояние клиента ---
    let conn = null, ChessLib = null, signalR = null, chess = null;
    let state = null, stateAt = 0, currentId = null, clockTimer = null;
    let authed = false, loginUrl = '/signin', L = {}, isEn = false;
    let sel = null, pendingPromo = null, myColor = 'w', flip = false, lastPersonalFetch = 0;
    let premove = null; // отложенный ход на чужом ходу: { from, to, promo } — исполнится, когда наступит наш ход
    let connecting = false, setupGen = 0; // защита от повторного входа в setup (иначе дубли соединений/таймеров)
    let lastHeroSig = null, lastPlayKey = null; // устойчивые секции: не пересобирать шапку/ожидание на каждый пуш
    let lastGameSig = null, drawDeclinedAt = 0;  // своя партия: не пересобирать доску на каждый пуш (иначе теряются клики)
    let boardSig = null; // подпись отрисованной доски (позиция+выбор+предход): не перерисовывать, если ничего не изменилось
    let chessUrl = '/lib/chess.js', signalrUrl = '/lib/signalr.js'; // переопределяются fingerprinted-URL из #t-root

    async function ensureLibs() {
        // Локальные сборки (vendored в wwwroot/lib), а НЕ внешний CDN esm.sh: на медленном интернете
        // обращение к esm.sh (DNS/TLS/латентность + цепочка под-импортов) надолго блокировало старт
        // клиента — кнопки/доски не появлялись, клики «не реагировали». Со своего origin грузится быстро,
        // кэшируется и сжимается вместе со страницей.
        if (!ChessLib) ChessLib = (await import(chessUrl)).Chess;
        if (!signalR) signalR = await import(signalrUrl);
    }

    function teardown() {
        setupGen++;                                   // инвалидируем любой setup «в полёте»
        connecting = false;
        if (clockTimer) { clearInterval(clockTimer); clockTimer = null; }
        if (conn) { try { conn.stop(); } catch (e) { } conn = null; }
        currentId = null; state = null; chess = null; sel = null; pendingPromo = null; premove = null;
        lastHeroSig = null; lastPlayKey = null; lastGameSig = null; boardSig = null; // следующий setup перестроит каркас заново
    }

    async function setup() {
        const root = document.getElementById('t-root');
        if (!root) { teardown(); return; }            // ушли со страницы турнира → закрыть соединение
        const id = root.dataset.id;
        // Уже инициализировано ИЛИ инициализируется для этого турнира — выходим. Проверяем connecting,
        // т.к. conn присваивается только после await (иначе два почти одновременных set() создали бы
        // два соединения и два таймера — старый таймер утекал бы → тормоза).
        if (currentId === id && (conn || connecting)) return;
        teardown();
        const gen = ++setupGen;                       // токен этого запуска
        currentId = id;
        connecting = true;
        authed = root.dataset.authed === '1';
        loginUrl = root.dataset.loginurl || '/signin';
        // @Assets даёт ОТНОСИТЕЛЬНЫЙ путь ("lib/chess.<hash>.js") — для import() нужен валидный URL,
        // поэтому резолвим в абсолютный относительно базы документа (иначе «голый» спецификатор → ошибка).
        if (root.dataset.chess) chessUrl = new URL(root.dataset.chess, document.baseURI).href;
        if (root.dataset.signalr) signalrUrl = new URL(root.dataset.signalr, document.baseURI).href;
        try { const cfg = JSON.parse(document.getElementById('t-loc').textContent); L = cfg.l; isEn = cfg.isEn; }
        catch (e) { L = {}; }

        try {
            await ensureLibs();
            if (gen !== setupGen) return;             // нас вытеснил teardown/новый setup
            chess = new ChessLib();

            const c = new signalR.HubConnectionBuilder().withUrl('/arenahub').withAutomaticReconnect().build();
            c.on('ArenaState', s => { if (gen === setupGen) onShared(s); });
            c.onreconnected(async () => { try { applyState(await c.invoke('GetState', id)); } catch (e) { } });

            await c.start();
            if (gen !== setupGen) { try { c.stop(); } catch (e) { } return; } // ушли/перезапустились за время старта
            conn = c;
            applyState(await c.invoke('JoinTournament', id));
            clockTimer = setInterval(tickClocks, 250);
        } catch (e) {
            const m = document.getElementById('t-main');
            if (m) m.innerHTML = `<p class="text-muted">${esc(L.loading || '…')}</p>`;
        } finally {
            if (gen === setupGen) connecting = false;
        }
    }

    // Общий пуш (без своей партии): зрителю — применяем как есть; участнику — берём свежее персональное
    // состояние (троттлинг), а общие части (доски/таблица/часы) обновляем сразу, не дожидаясь round-trip.
    function onShared(s) {
        if (state && state.joined) {
            const { myGame, joined, myScore, seeking } = state;
            state = s; state.myGame = myGame; state.joined = joined; state.myScore = myScore; state.seeking = seeking;
            stateAt = Date.now(); scheduleRender();
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
        scheduleRender();
    }

    // Коалесцируем перерисовки в один кадр: частые пуши с сервера не вызывают многократный
    // пересбор всех досок за один фрейм (тяжёлый innerHTML). Действия пользователя рисуют сразу.
    let renderQueued = false;
    function scheduleRender() {
        if (renderQueued) return;
        renderQueued = true;
        requestAnimationFrame(() => { renderQueued = false; render(); });
    }

    function syncMyGame() {
        const g = state && state.myGame;
        if (g && g.fen) {
            myColor = g.myColor === 1 ? 'b' : 'w';
            flip = myColor === 'b';
            try { chess.load(g.fen); } catch (e) { }
            maybeRunPremove(g); // если наступил наш ход и есть отложенный предход — исполнить
        }
    }

    // ----------------------- рендер -----------------------
    function render() {
        const main = document.getElementById('t-main');
        if (!main || !state) return;
        // Устойчивый каркас: пересборка всего #t-main на каждый пуш пересоздавала бы DOM-узлы и
        // перезапускала их CSS-анимации (кольцо «ищем соперника», пульс Live-точки) — на ход в любой
        // транслируемой доске прилетает пуш, и анимация дёргалась. Поэтому секции разделены и
        // обновляются независимо: анимированные не трогаем, пока их содержимое не изменилось.
        if (!document.getElementById('t-play')) {
            main.innerHTML = '<div id="t-hero"></div><div id="t-play"></div><div id="t-boards"></div><div id="t-standings"></div>';
            lastHeroSig = null; lastPlayKey = null;
        }
        const crumb = document.getElementById('t-crumb');
        if (crumb) crumb.textContent = state.name;

        // Шапка: пересобираем только при смене значимых данных (счётчик участников/статус/состав топ-3),
        // иначе пульс Live-точки перезапускался бы каждый пуш. Обратный отсчёт идёт отдельно (tickClocks).
        const top3 = state.standings.slice(0, 3).map(s => s.name).join(',');
        const heroSig = `${state.status}|${state.joined}|${authed}|${state.standings.length}|${top3}`;
        if (heroSig !== lastHeroSig) {
            document.getElementById('t-hero').innerHTML = heroHtml();
            lastHeroSig = heroSig;
        }

        renderPlay();
        // Во время своей активной партии не транслируем чужие доски — не отвлекаем от игры (и меньше
        // перерисовок). Завершённую партию/ожидание это не касается — там трансляция снова видна.
        const playingNow = !!(state.myGame && state.myGame.status === 1);
        document.getElementById('t-boards').innerHTML = playingNow ? '' : boardsHtml();
        document.getElementById('t-standings').innerHTML = standingsHtml();

        wireActionHandlers();   // перевесить обработчики на актуальные элементы (join/berserk/resign)
        tickClocks();
    }

    // Секция «своя партия / ожидание соперника». Ключ режима (lastPlayKey) защищает анимацию ожидания:
    // пока игрок ждёт, узел .search-anim НЕ пересоздаётся (обновляем лишь счёт очков) → анимация плавная.
    function renderPlay() {
        const el = document.getElementById('t-play');
        if (!el) return;
        const running = state.status === 1;
        const g = (running && state.joined) ? state.myGame : undefined;

        if (running && state.joined && !g) {
            // Подбор не автоматический: пока игрок не нажал «подобрать соперника» (state.seeking=false) —
            // показываем кнопку; после нажатия — анимацию поиска. Узлы не пересоздаём между пушами
            // (ключ режима), обновляем лишь счёт очков, чтобы анимация/кнопка не дёргались.
            const mode = state.seeking ? 'waiting' : 'seek';
            if (lastPlayKey !== mode) { el.innerHTML = mode === 'waiting' ? waitingHtml() : seekHtml(); lastPlayKey = mode; }
            else { const sc = el.querySelector('.js-myscore'); if (sc) sc.textContent = state.myScore; }
            return;
        }
        if (g) {                                          // идёт своя партия
            // Полную пересборку (innerHTML + buildBoard) делаем ТОЛЬКО при смене партии/ориентации/статуса.
            // На обычный пуш (ход в любой доске турнира, апдейт часов) — лёгкое обновление БЕЗ пересоздания
            // клеток доски: иначе кнопка-клетка уничтожалась между нажатием и кликом и «клик не срабатывал».
            const sig = `${g.gameId}|${g.status}|${g.myColor}`;
            if (lastPlayKey !== 'game' || lastGameSig !== sig) {
                el.innerHTML = myGameHtml();
                lastPlayKey = 'game'; lastGameSig = sig;
                wireBoardHandlers();
            } else {
                updateMyGameCard();                       // лёгкое обновление: фигуры/часы/контролы, без rebuild доски
            }
            return;
        }
        if (lastPlayKey !== null) { el.innerHTML = ''; lastPlayKey = null; } // нечего показывать
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

    // Карточка ожидания соперника (с анимацией поиска). Счёт очков помечен .js-myscore — его можно
    // обновлять, не пересоздавая узел анимации (см. renderPlay).
    function waitingHtml() {
        return `<div class="card waiting">
            <div class="search-anim" aria-hidden="true"><span class="search-ring"></span>
                <span class="search-piece">${pieceImg('b', 'n', 'cp')}</span></div>
            <div class="search-text">${esc(L.search)}<span class="dots"><i></i><i></i><i></i></span></div>
            <div class="text-muted mt-1">${esc(L.score)} <strong class="js-myscore">${state.myScore}</strong></div>
        </div>`;
    }

    // Карточка «подобрать соперника»: до нажатия игрок просто записан и соперника не ищет.
    function seekHtml() {
        return `<div class="card seek-card">
            <button class="btn btn-join" id="t-seek">${esc(L.seek)}</button>
            <div class="text-muted mt-1">${esc(L.score)} <strong class="js-myscore">${state.myScore}</strong></div>
        </div>`;
    }

    function myGameHtml() {
        const g = state.myGame;
        if (!g) return '';
        const fin = g.status === 2;
        const wActive = g.turn === 0 && g.status === 1, bActive = g.turn === 1 && g.status === 1;
        // Соперник — над доской, я — под доской (как на реальной доске; доска уже развёрнута под мой цвет).
        const iAmWhite = g.myColor === 0; // PieceColor.White = 0
        const whiteRow = playerRow(g.whiteName, g.whiteBerserk, g.whiteMs, wActive, g.whiteIsBot, fin && g.whiteMs <= 0);
        const blackRow = playerRow(g.blackName, g.blackBerserk, g.blackMs, bActive, g.blackIsBot, fin && g.blackMs <= 0);

        // Итог — оверлеем поверх финальной доски: исход ОТ ЛИЦА ИГРОКА (победа/поражение/ничья) + причина
        // (мат/время/сдача). Сбоку — влияние на турнир (очки за партию/место/серия) и что делать дальше.
        const o = fin ? myOutcome(g) : '';
        const overlay = fin
            ? `<div class="gs-overlay"><div class="gs-card gs-${o}"><div class="gs-out">${esc(outcomeLabel(o))}</div>` +
              `${reasonLabel(g.endReason) ? `<div class="gs-reason">${esc(reasonLabel(g.endReason))}</div>` : ''}</div></div>`
            : '';
        const side = fin ? endgameSideHtml(g) : '';
        return `<div class="card my-game">
            <div class="players players-top">${iAmWhite ? blackRow : whiteRow}</div>
            <div class="game-row">
                <div class="board-wrap"><div class="board" id="t-board"></div>${overlay}<div class="promo" id="t-promo" hidden></div></div>
                ${side ? `<div class="game-side">${side}</div>` : ''}
            </div>
            <div class="players players-bottom">${iAmWhite ? whiteRow : blackRow}</div>
            <div class="my-controls">${controlsHtml(g)}</div>
        </div>`;
    }

    // Боковая панель итога: влияние партии на турнир (всё считается из моей строки таблицы лидеров —
    // последний элемент results = очки именно за эту партию) и иерархия действий.
    function endgameSideHtml(g) {
        const row = myStandingRow(g);
        const pts = row && row.results && row.results.length ? row.results[row.results.length - 1] : 0;
        const place = row ? row.rank : '—';
        const total = (state.standings || []).length;
        const streak = row ? row.streak : 0;
        return `<div class="gs-meta">
                <div class="gs-m"><span class="gs-ml">${esc(L.lastpts)}</span><span class="gs-mv">+${pts}</span></div>
                <div class="gs-m"><span class="gs-ml">${esc(L.placelbl)}</span><span class="gs-mv">${place}<small>/${total}</small></span></div>
                <div class="gs-m"><span class="gs-ml">${esc(L.streaklbl)}</span><span class="gs-mv">${streak >= 2 ? '🔥 ' : ''}${streak}</span></div>
            </div>
            <button class="btn btn-join" id="t-seek">${esc(L.seek)}</button>
            <div class="gs-actions">
                <a class="btn btn-outline btn-sm" href="/me/games">${esc(L.review)}</a>
                <button class="btn btn-outline btn-sm" id="t-standings-btn">${esc(L.standings)}</button>
            </div>
            <div class="gs-endin">${esc(L.endin)} ${hms(state.secondsLeft)}</div>`;
    }

    function myOutcome(g) {
        if (g.result === 3) return 'draw';
        const iWhite = g.myColor === 0;
        if (g.result === 1) return iWhite ? 'win' : 'loss';
        if (g.result === 2) return iWhite ? 'loss' : 'win';
        return 'draw';
    }
    function outcomeLabel(o) { return o === 'win' ? L.youwin : o === 'loss' ? L.youlose : L.youdraw; }
    function reasonLabel(r) {
        return ({ 1: L.rcheckmate, 2: L.rresign, 3: L.rtimeout, 4: L.rstalemate, 5: L.rdrawagreed, 6: L.rinsufficient, 7: L.rabandoned })[r] || '';
    }
    // «Я» в таблице — по имени с моей стороны доски (бота исключаем).
    function myStandingRow(g) {
        const me = g.myColor === 0 ? g.whiteName : g.blackName;
        return (state.standings || []).find(r => !r.isBot && r.name === me) || null;
    }

    // Управление под доской (ход + берсерк + ничья + сдаться) и баннер входящего предложения ничьи.
    // Вынесено отдельно: обновляется на каждый пуш без пересоздания доски (см. updateMyGameCard).
    function controlsHtml(g) {
        if (g.status === 2) return '';                    // завершено — результат/«подобрать» в game-side
        const turn = `<span class="text-secondary">${esc(g.turn === g.myColor ? L.yourmove : L.oppmove)}</span>`;
        const berserk = g.myBerserkAvailable ? `<button class="btn btn-warn btn-sm" id="t-berserk" title="${esc(L.berserktip)}">⚡ Berserk</button>` : '';
        let draw;
        if (g.drawOfferByMe) draw = `<button class="btn btn-outline btn-sm" disabled>${esc(L.drawoffered)}</button>`;
        else if (Date.now() - drawDeclinedAt < 3000) draw = `<span class="draw-declined">${esc(L.drawdeclined)}</span>`;
        else draw = `<button class="btn btn-outline btn-sm" id="t-draw">${esc(L.drawoffer)}</button>`;
        const main = `<div class="gstatus">${turn}<span class="ds-spacer"></span>${berserk}${draw}` +
            `<button class="btn btn-outline-danger btn-sm" id="t-resign">${esc(L.resign)}</button></div>`;
        const incoming = g.drawOfferFromOpponent
            ? `<div class="draw-incoming"><span>🤝 ${esc(L.drawincoming)}</span><span class="ds-spacer"></span>` +
              `<button class="btn btn-success btn-sm" id="t-draw-accept">${esc(L.drawaccept)}</button>` +
              `<button class="btn btn-outline btn-sm" id="t-draw-decline">${esc(L.drawdecline)}</button></div>`
            : '';
        return main + incoming;
    }

    // Лёгкое обновление карточки своей партии без пересоздания клеток доски (клики не теряются):
    // обновляем ряды игроков (часы/имена), контролы (ход/берсерк/ничья) и фигуры через renderBoard.
    function updateMyGameCard() {
        const g = state.myGame; if (!g) return;
        const iAmWhite = g.myColor === 0;
        const fin = g.status === 2;
        const wActive = g.turn === 0 && g.status === 1, bActive = g.turn === 1 && g.status === 1;
        const whiteRow = playerRow(g.whiteName, g.whiteBerserk, g.whiteMs, wActive, g.whiteIsBot, fin && g.whiteMs <= 0);
        const blackRow = playerRow(g.blackName, g.blackBerserk, g.blackMs, bActive, g.blackIsBot, fin && g.blackMs <= 0);
        const top = document.querySelector('.my-game .players-top');
        const bot = document.querySelector('.my-game .players-bottom');
        if (top) top.innerHTML = iAmWhite ? blackRow : whiteRow;
        if (bot) bot.innerHTML = iAmWhite ? whiteRow : blackRow;
        const ctrl = document.querySelector('.my-game .my-controls');
        if (ctrl) ctrl.innerHTML = controlsHtml(g);
        renderBoard();
    }

    function playerRow(name, berserk, ms, active, isBot, flag) {
        const fl = flag ? ' <span class="clock-flag" title="время вышло">⚑</span>' : '';
        return `<span>${berserk ? '⚡ ' : ''}${esc(name)}${botTag(isBot)} <span class="clock js-clock ${active ? 'active' : ''}" data-ms="${ms}" data-active="${active ? 1 : 0}">${mmss(ms)}</span>${fl}</span>`;
    }

    function boardsHtml() {
        const mineId = state.myGame ? state.myGame.gameId : null;
        const list = state.boards.filter(b => b.gameId !== mineId).slice(0, 4);
        if (list.length === 0) return '';
        const cards = list.map(b => {
            const wActive = b.turn === 0 && b.status === 1, bActive = b.turn === 1 && b.status === 1;
            const res = b.status === 2 ? `<div class="bresult"><span class="rcell w">${gres(b.result, true)}</span><span class="rdash">–</span><span class="rcell b">${gres(b.result, false)}</span></div>` : '';
            return `<div class="bcard ${b.status === 2 ? 'done' : ''}">
                <div class="bplayer"><span class="bava">${avatar(b.blackName)}</span><span class="bname">${esc(b.blackName)}${botTag(b.blackIsBot)}</span><span class="bscore">${b.blackScore}</span></div>
                <div class="bclock js-clock ${bActive ? 'active' : ''}" data-ms="${b.blackMs}" data-active="${bActive ? 1 : 0}">${mmss(b.blackMs)}</div>
                <div class="bboard">${miniBoard(b.fen, b.lastFrom, b.lastTo, b.checkSquare)}${res}</div>
                <div class="bclock js-clock ${wActive ? 'active' : ''}" data-ms="${b.whiteMs}" data-active="${wActive ? 1 : 0}">${mmss(b.whiteMs)}</div>
                <div class="bplayer"><span class="bava">${avatar(b.whiteName)}</span><span class="bname">${esc(b.whiteName)}${botTag(b.whiteIsBot)}</span><span class="bscore">${b.whiteScore}</span></div>
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
                <div class="pod-name">${esc(p.name)}${botTag(p.isBot)}</div>
                <div class="pod-stat">${p.games} ${esc(L.podgames)} • ${p.wins} ${esc(L.podwins)} • ${p.score} ${esc(L.podpts)}</div></div>`).join('');
        const list = r.map(s => {
            const chips = s.results.length ? `<div class="st-chips">${s.results.map(x => `<span class="chip ${chipClass(x)}">${x}</span>`).join('')}</div>` : '';
            return `<div class="st-card"><div class="st-head">
                <span class="st-ava">${avatar(s.name)}</span>
                <span class="st-name">${esc(s.name)}${botTag(s.isBot)} ${s.onFire ? `<span class="fire">🔥 ${s.streak}</span>` : ''}</span>
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
        boardSig = null; // клетки пересозданы — следующая renderBoard обязана отрисовать
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
        // Защита от ложной перерисовки: арена шлёт пуш на ЛЮБОЕ событие турнира (в т.ч. ход бота в чужой
        // партии), и без этого доска мигала бы на каждый пуш. Перерисовываем 64 клетки, только если реально
        // изменились позиция / выбор / предход / подсветка хода. Выбор и предход — в подписи, чтобы клики
        // (они зовут renderBoard) всё-таки перерисовывали.
        const sig = chess.fen() + '|' + (sel || '') + '|' + (premove ? premove.from + premove.to : '') +
            '|' + g.status + '|' + (g.lastFrom || '') + (g.lastTo || '') + (g.checkSquare || '');
        if (sig === boardSig) return;
        boardSig = sig;
        for (const f of FILES) for (let r = 1; r <= 8; r++) {
            const sq = f + r, el = cells[sq]; if (!el) continue;
            const p = chess.get(sq);
            el.innerHTML = coordHtml[sq] + (p ? pieceImg(p.color, p.type, 'piece-img') : '');
            el.classList.remove('hl', 'chk', 'sel', 'premove');
        }
        if (g.lastFrom && cells[g.lastFrom]) cells[g.lastFrom].classList.add('hl');
        if (g.lastTo && cells[g.lastTo]) cells[g.lastTo].classList.add('hl');
        if (g.checkSquare && cells[g.checkSquare]) cells[g.checkSquare].classList.add('chk');
        // Подсветку выбора/предхода восстанавливаем между перерисовками: доска пересобирается на каждый
        // пуш, и без этого выделение «слетало», пока игрок думает (выбор) или ждёт своего хода (предход).
        if (g.status === 1 && premove) {
            if (cells[premove.from]) cells[premove.from].classList.add('premove');
            if (cells[premove.to]) cells[premove.to].classList.add('premove');
        }
        if (g.status === 1 && sel && cells[sel]) cells[sel].classList.add('sel');
    }

    function onCellClick(sq) {
        const g = state.myGame;
        if (!g || g.status !== 1 || pendingPromo) return;
        if (chess.turn() !== myColor) { premoveClick(sq); return; } // ход соперника → копим предход
        clearPremove();                                             // наш ход — обычная логика
        const p = chess.get(sq);
        if (sel === null) { if (p && p.color === myColor) { sel = sq; cells[sq].classList.add('sel'); } return; }
        if (sq === sel) { cells[sel] && cells[sel].classList.remove('sel'); sel = null; return; } // повторный клик — снять выделение
        if (p && p.color === myColor) { // клик по другой своей фигуре — сразу переключаем выделение (без второго клика)
            cells[sel] && cells[sel].classList.remove('sel');
            sel = sq; cells[sq].classList.add('sel'); return;
        }
        const from = sel; cells[from] && cells[from].classList.remove('sel'); sel = null;
        const mover = chess.get(from);
        if (mover && mover.type === 'p' && (sq[1] === '8' || sq[1] === '1')) { askPromotion(from, sq); return; }
        commitMove(from, sq, null);
    }

    // Клик на чужом ходу: задаём предход (выбрать свою фигуру, затем клетку). Предход-превращение —
    // по умолчанию ферзь (без всплывающего выбора на чужом ходу). Исполнится в maybeRunPremove.
    function premoveClick(sq) {
        const p = chess.get(sq);
        if (sel !== null) {
            if (sq === sel) { sel = null; renderBoard(); return; }                 // повторный клик — отмена выбора
            if (p && p.color === myColor) { sel = sq; renderBoard(); return; }     // другая своя фигура — сразу переключаем
            const from = sel; sel = null;
            const mover = chess.get(from);
            const promo = mover && mover.type === 'p' && (sq[1] === '8' || sq[1] === '1') ? 'q' : null;
            premove = { from, to: sq, promo };
            renderBoard();
            return;
        }
        clearPremove();
        if (p && p.color === myColor) { sel = sq; }
        renderBoard();
    }

    function clearPremove() { premove = null; }

    // Наступил наш ход и есть отложенный предход — пробуем исполнить (chess.js проверит легальность;
    // нелегальный молча отменяется). Сервер подтвердит ход обычным путём.
    function maybeRunPremove(g) {
        if (!premove || !g || g.status !== 1 || chess.turn() !== myColor) return;
        const mv = premove; premove = null;
        commitMove(mv.from, mv.to, mv.promo);
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
        const seek = document.getElementById('t-seek');
        if (seek) seek.onclick = () => { seek.disabled = true; conn.invoke('SeekOpponent', currentId).then(applyState).catch(() => seek.disabled = false); };
        const tostand = document.getElementById('t-standings-btn');
        if (tostand) tostand.onclick = () => { const s = document.getElementById('t-standings'); if (s) s.scrollIntoView({ behavior: 'smooth', block: 'start' }); };
        const berserk = document.getElementById('t-berserk');
        if (berserk) berserk.onclick = () => conn.invoke('Berserk', currentId).then(applyState).catch(() => { });
        const resign = document.getElementById('t-resign');
        if (resign) resign.onclick = () => conn.invoke('Resign', currentId).then(applyState).catch(() => { });
        const draw = document.getElementById('t-draw');
        if (draw) draw.onclick = () => {
            draw.disabled = true;
            conn.invoke('OfferDraw', currentId).then(outcome => {
                if (outcome === 'declined') { drawDeclinedAt = Date.now(); scheduleRender(); setTimeout(scheduleRender, 3100); }
                else conn.invoke('GetState', currentId).then(applyState).catch(() => { }); // accepted/offered → подтянуть состояние
            }).catch(() => { draw.disabled = false; });
        };
        const accept = document.getElementById('t-draw-accept');
        if (accept) accept.onclick = () => conn.invoke('AcceptDraw', currentId).then(applyState).catch(() => { });
        const decline = document.getElementById('t-draw-decline');
        if (decline) decline.onclick = () => conn.invoke('DeclineDraw', currentId).then(applyState).catch(() => { });
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

    // Мгновенный старт при появлении #t-root в DOM. Blazor enhanced-navigation НЕ исполняет вставленный
    // <script> и не всегда шлёт enhancedload на document — поэтому ловим именно вставку узла (как
    // schedule.js ловит сетку). Скрипт подключён глобально в App.razor, так что наблюдатель жив всегда.
    function watchForRoot() {
        if (window.__tRootObserver) return;
        const relevant = (n) => n.nodeType === 1 && (n.matches?.('#t-root') || n.querySelector?.('#t-root'));
        window.__tRootObserver = new MutationObserver((records) => {
            if (!records.some(r => Array.from(r.addedNodes).some(relevant))) return;
            if (window.__tBootRaf) return;                 // дебаунс: один setup на пачку мутаций
            window.__tBootRaf = requestAnimationFrame(() => { window.__tBootRaf = 0; setup(); });
        });
        window.__tRootObserver.observe(document.documentElement, { childList: true, subtree: true });
    }

    window.arenaTournamentSetup = setup;
    watchForRoot();                                        // переживает enhanced-навигацию
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', setup);
    else setup();                                          // прямой заход: DOM уже готов
    document.addEventListener('enhancedload', setup);      // запасной путь
})();
