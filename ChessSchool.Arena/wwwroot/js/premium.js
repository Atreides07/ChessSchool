// Страница /premium: навешивает обработчик кнопки оплаты. Режим paddle — hosted Checkout Paddle.js v2
// (карты у нас не ходят); режим dev — локальная активация без оплаты. Глобальный скрипт (App.razor),
// переживает enhanced-навигацию Blazor (как tournament.js): ловим вставку #prem-root.
(function () {
    if (window.__premium) return;
    window.__premium = true;

    function loadPaddle(cb) {
        if (window.Paddle) { cb(); return; }
        var s = document.createElement('script');
        s.src = 'https://cdn.paddle.com/paddle/v2/paddle.js';
        s.onload = cb;
        document.head.appendChild(s);
    }

    function setup() {
        var root = document.getElementById('prem-root');
        var btn = document.getElementById('prem-buy');
        if (!root || !btn || btn.__wired) return;
        btn.__wired = true;
        var d = root.dataset;

        if (d.mode === 'paddle') {
            loadPaddle(function () {
                try {
                    if (d.env) window.Paddle.Environment.set(d.env);
                    window.Paddle.Initialize({ token: d.token });
                } catch (e) { }
                btn.onclick = function () {
                    window.Paddle.Checkout.open({
                        items: [{ priceId: d.price, quantity: 1 }],
                        customData: { user_sub: d.sub },         // связь подписки с пользователем (в вебхуке)
                        settings: { successUrl: d.success }
                    });
                };
            });
        } else {
            btn.onclick = async function () {
                btn.disabled = true;
                try { await fetch('/premium/dev-activate', { method: 'POST' }); } catch (e) { }
                location.href = '/premium'; // перечитать статус (премиум активирован)
            };
        }
    }

    function watch() {
        if (window.__premObs) return;
        var relevant = function (n) {
            return n.nodeType === 1 && (n.id === 'prem-root' || (n.querySelector && n.querySelector('#prem-root')));
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
