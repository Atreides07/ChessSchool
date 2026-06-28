// Страница /premium: навешивает обработчик кнопки оплаты. Режим paddle — hosted Checkout Paddle.js v2
// (карты у нас не ходят); режим dev — локальная активация без оплаты. Глобальный скрипт (App.razor),
// переживает enhanced-навигацию Blazor (как tournament.js): ловим вставку #prem-root.
(function () {
    if (window.__premium) return;
    window.__premium = true;

    // Показать пользователю причину (вместо «кнопка не работает»): пишем в #prem-status и в консоль.
    function status(msg) {
        console.error('[premium] ' + msg);
        var el = document.getElementById('prem-status');
        if (el) { el.textContent = msg; el.hidden = false; }
    }

    var paddleState = 0; // 0=не загружали, 1=грузится, 2=готов, -1=ошибка загрузки
    var paddleWaiters = [];

    // Грузим Paddle.js один раз; колбэк зовётся с true (готов) или false (не удалось загрузить CDN).
    function loadPaddle(cb) {
        if (paddleState === 2) { cb(true); return; }
        if (paddleState === -1) { cb(false); return; }
        paddleWaiters.push(cb);
        if (paddleState === 1) return;
        paddleState = 1;
        if (window.Paddle) { paddleState = 2; flush(true); return; }
        var s = document.createElement('script');
        s.src = 'https://cdn.paddle.com/paddle/v2/paddle.js';
        s.onload = function () { paddleState = 2; flush(true); };
        s.onerror = function () { paddleState = -1; flush(false); };
        document.head.appendChild(s);
    }
    function flush(ok) {
        var w = paddleWaiters; paddleWaiters = [];
        w.forEach(function (cb) { try { cb(ok); } catch (e) { } });
    }

    var paddleInited = false;
    function initPaddle(d) {
        if (paddleInited) return true;
        try {
            if (d.env) window.Paddle.Environment.set(d.env);
            window.Paddle.Initialize({
                token: d.token,
                // Ловим ошибку самого checkout (например, неверный price/токен) и показываем её.
                eventCallback: function (ev) {
                    if (ev && ev.name === 'checkout.error') {
                        status('Paddle: ' + ((ev.error && ev.error.detail) || 'не удалось открыть оплату.'));
                    }
                }
            });
            paddleInited = true;
            return true;
        } catch (e) {
            status('Paddle не инициализировался: ' + (e && e.message ? e.message : e));
            return false;
        }
    }

    function openCheckout(d, btn, price) {
        loadPaddle(function (ok) {
            if (!ok) { status('Не удалось загрузить Paddle (проверь сеть/блокировщик рекламы).'); return; }
            if (!initPaddle(d)) return;
            try {
                window.Paddle.Checkout.open({
                    items: [{ priceId: price || d.price, quantity: 1 }],
                    customData: { user_sub: d.sub },         // связь подписки с пользователем (в вебхуке)
                    settings: { successUrl: d.success }
                });
            } catch (e) {
                status('Не удалось открыть оплату: ' + (e && e.message ? e.message : e));
            }
        });
    }

    // Выбор плана (месяц/год): .prem-plan.is-sel задаёт цену для чекаута. Без карточек — обычный d.price.
    function selectedPrice(d) {
        var sel = document.querySelector('.prem-plan.is-sel');
        return (sel && sel.dataset.price) || d.price;
    }
    function wirePlans() {
        var plans = document.querySelectorAll('.prem-plan');
        plans.forEach(function (p) {
            if (p.__wired) return; p.__wired = true;
            p.onclick = function () { plans.forEach(function (x) { x.classList.remove('is-sel'); }); p.classList.add('is-sel'); };
        });
    }

    // Реальные локализованные цены в карточки (best-effort): Paddle PricePreview. Сбой — карточки без сумм.
    function fillPrices(d) {
        if (!d.priceAnnual) return;
        var ids = {}; document.querySelectorAll('.prem-plan').forEach(function (p) { if (p.dataset.price) ids[p.dataset.price] = p; });
        var items = Object.keys(ids).map(function (id) { return { priceId: id, quantity: 1 }; });
        if (!items.length || !window.Paddle.PricePreview) return;
        window.Paddle.PricePreview({ items: items }).then(function (res) {
            var li = res && res.data && res.data.details && res.data.details.lineItems;
            if (!li) return;
            li.forEach(function (item) {
                var card = ids[item.price && item.price.id];
                var amtEl = card && card.querySelector('[data-amt]');
                if (amtEl && item.formattedTotals) amtEl.textContent = item.formattedTotals.total;
            });
        }).catch(function () { });
    }

    var TXN_KEY = 'prem_txn';

    // После возврата с checkout Paddle добавляет ?_ptxn=... в URL. Сверяем статус из API (reconcile),
    // чтобы премиум активировался, даже если вебхук не дошёл; затем чистим URL и перечитываем страницу.
    // Подписка у Paddle создаётся асинхронно — на момент возврата её может ещё не быть, поэтому txn
    // запоминаем: «Обновить статус» повторит reconcile по нему (сервер найдёт подписку по клиенту).
    function reconcileOnReturn() {
        if (window.__premReconciled) return;
        var txn = new URLSearchParams(location.search).get('_ptxn');
        if (!txn) return;
        window.__premReconciled = true;
        try { localStorage.setItem(TXN_KEY, txn); } catch (e) { }
        fetch('/premium/reconcile?txn=' + encodeURIComponent(txn), { method: 'POST' })
            .finally(function () { location.replace(location.pathname); });
    }

    function setup() {
        reconcileOnReturn();

        // Кнопка «Обновить статус» работает в обоих состояниях: у не-премиума — добить активацию после
        // оплаты (по запомненной транзакции); у премиума — подтянуть статус, если подписку отменили в портале.
        var refresh = document.getElementById('prem-refresh');
        if (refresh && !refresh.__wired) {
            refresh.__wired = true;
            refresh.onclick = async function () {
                refresh.disabled = true;
                var saved = '';
                try { saved = localStorage.getItem(TXN_KEY) || ''; } catch (e) { }
                var url = saved ? '/premium/reconcile?txn=' + encodeURIComponent(saved) : '/premium/reconcile';
                try { await fetch(url, { method: 'POST' }); } catch (e) { }
                location.href = '/premium';
            };
        }

        // Уже премиум (видна карточка «есть подписка») — кнопки покупки нет; забываем запомненную транзакцию.
        if (document.querySelector('.prem-have')) { try { localStorage.removeItem(TXN_KEY); } catch (e) { } return; }

        var root = document.getElementById('prem-root');
        var btn = document.getElementById('prem-buy');
        if (!root || !btn || btn.__wired) return;
        btn.__wired = true;
        var d = root.dataset;

        if (d.mode === 'paddle') {
            if (!d.token || !d.price) { status('Не задан Paddle:ClientToken или PremiumPriceId.'); return; }
            wirePlans();   // выбор месяц/год (если карточки есть)
            // Вешаем обработчик СРАЗУ — клик всегда реагирует; Paddle грузится лениво при первом клике.
            btn.onclick = function () { openCheckout(d, btn, selectedPrice(d)); };
            // Предзагрузка в фоне + реальные цены в карточки, чтобы первый клик открывался без паузы.
            loadPaddle(function (ok) { if (ok && initPaddle(d)) fillPrices(d); });
        } else {
            btn.onclick = async function () {
                btn.disabled = true;
                try {
                    var r = await fetch('/premium/dev-activate', { method: 'POST' });
                    if (!r.ok) { status('Не удалось активировать (HTTP ' + r.status + ').'); btn.disabled = false; return; }
                } catch (e) { status('Сеть недоступна: ' + (e && e.message ? e.message : e)); btn.disabled = false; return; }
                location.href = '/premium'; // перечитать статус (премиум активирован)
            };
        }
    }

    function watch() {
        if (window.__premObs) return;
        var relevant = function (n) {
            return n.nodeType === 1 && (n.id === 'prem-root' || n.id === 'prem-refresh'
                || (n.querySelector && n.querySelector('#prem-root, #prem-refresh')));
        };
        window.__premObs = new MutationObserver(function (recs) {
            if (recs.some(function (r) { return Array.from(r.addedNodes).some(relevant); })) setup();
        });
        window.__premObs.observe(document.documentElement, { childList: true, subtree: true });
    }

    watch();
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', setup);
    else setup();
    document.addEventListener('enhancedload', setup);
})();
