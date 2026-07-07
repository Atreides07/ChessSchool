namespace ChessSchool.Arena;

/// <summary>Премиум/подписки: dev-активация, разбор своих партий, портал/reconcile и админ-CRUD подписок.
/// Прокси к ApiService (источник истины) по внутреннему ключу; тонкие клиенты fetch'ат в request-контексте.</summary>
public static class PremiumEndpoints
{
    public static void MapPremiumEndpoints(this WebApplication app, string internalApiKey)
    {
        // Раздел переименован «Турниры» → «Трансляции»: старые пути 301-редиректятся на /broadcasts (без битых ссылок).
        // Dev-активация премиума без оплаты (только Development) — проксирует в ApiService dev-activate.
        if (app.Environment.IsDevelopment())
        {
            app.MapPost("/premium/dev-activate", async (HttpContext ctx, IHttpClientFactory http,
                ChessSchool.Arena.Services.IPlayerEntitlements ents, CancellationToken ct) =>
            {
                var sub = ctx.User.FindFirst("sub")?.Value;
                if (string.IsNullOrEmpty(sub)) return Results.Unauthorized();
                var client = http.CreateClient(ChessSchool.Arena.Services.PlayerEntitlements.HttpClientName);
                using var req = new HttpRequestMessage(HttpMethod.Post, "/internal/subscriptions/dev-activate");
                req.Headers.Add("X-Internal-Key", internalApiKey);
                req.Content = System.Net.Http.Json.JsonContent.Create(new ChessSchool.Contracts.DevActivateRequest(sub, "premium"));
                await client.SendAsync(req, ct);
                ents.Invalidate(sub); // сбросить кэш — статус подхватится на ближайшем запросе/перезагрузке
                return Results.Ok();
            }).RequireAuthorization("ConfirmedEmail").DisableAntiforgery(); // премиум — только с подтверждённым e-mail
        }

        // Данные партии для тонкого клиента страницы /me/games/{id}: позиции (стартовый FEN + FEN/ход после
        // каждого полухода), имена, премиум-статус и кэш разбора. Грузится браузером (fetch) — НЕ в рендере
        // Blazor-компонента (там исходящий HTTP зависает; здесь обычный request-контекст — работает).
        app.MapGet("/api/me/games/{id:guid}", async (Guid id, HttpContext ctx,
            ChessSchool.Arena.Services.ArenaReviewService review,
            ChessSchool.Arena.Services.IPlayerEntitlements ents, CancellationToken ct) =>
        {
            var sub = ctx.User.FindFirst("sub")?.Value;
            if (string.IsNullOrEmpty(sub)) return Results.Unauthorized();
            var detail = await review.GetAsync(id, sub, ct);
            if (detail is null) return Results.NotFound();

            var (startFen, plies) = ChessSchool.Arena.Services.GameReplay.FromPgn(detail.Pgn);
            var premium = await ents.IsPremiumAsync(sub, ct);
            var analysis = premium ? await review.GetCachedAnalysisAsync(id, sub, ct) : null;

            // Исход с точки зрения игрока (0 победа / 1 поражение / 2 ничья — как PlayerOutcome).
            var outcome = detail.Result switch
            {
                ChessSchool.Contracts.GameResult.WhiteWins => detail.MyColor == ChessSchool.Contracts.PieceColor.White ? 0 : 1,
                ChessSchool.Contracts.GameResult.BlackWins => detail.MyColor == ChessSchool.Contracts.PieceColor.Black ? 0 : 1,
                _ => 2,
            };

            return Results.Ok(new
            {
                startFen,
                plies = plies.Select(p => new { fen = p.Fen, san = p.San, from = p.From, to = p.To }),
                myColor = detail.MyColor == ChessSchool.Contracts.PieceColor.White ? "w" : "b",
                whiteName = detail.WhiteName,
                blackName = detail.BlackName,
                whiteIsBot = detail.WhiteIsBot,
                blackIsBot = detail.BlackIsBot,
                outcome,
                endReason = (int)detail.EndReason,
                premium,
                analysis,
            });
        }).RequireAuthorization();

        // Разбор партии для тонкого клиента страницы /me/games/{id}: считается в обычном request-контексте
        // (Stockfish/HTTP к ApiService тут работают, в отличие от Blazor-рендерера), кэшируется в ApiService.
        // Премиум-фича → гейт по подписке; только участник (GetAsync вернёт null постороннему).
        app.MapGet("/api/me/games/{id:guid}/analysis", async (Guid id, HttpContext ctx,
            ChessSchool.Arena.Services.ArenaReviewService review,
            ChessSchool.Arena.Services.IPlayerEntitlements ents, CancellationToken ct) =>
        {
            var sub = ctx.User.FindFirst("sub")?.Value;
            if (string.IsNullOrEmpty(sub)) return Results.Unauthorized();
            if (!await ents.IsPremiumAsync(sub, ct)) return Results.Forbid();
            var detail = await review.GetAsync(id, sub, ct);
            if (detail is null) return Results.NotFound();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(120)); // разбор не должен висеть бесконечно
            var analysis = await review.ComputeAnalysisAsync(id, sub, detail.Pgn, timeout.Token);
            return Results.Ok(analysis);
        }).RequireAuthorization();

        // Управление подпиской: редирект в hosted Customer Portal провайдера (URL берём у ApiService).
        app.MapGet("/premium/portal", async (HttpContext ctx, IHttpClientFactory http, CancellationToken ct) =>
        {
            var sub = ctx.User.FindFirst("sub")?.Value;
            if (string.IsNullOrEmpty(sub)) return Results.Unauthorized();
            var client = http.CreateClient(ChessSchool.Arena.Services.PlayerEntitlements.HttpClientName);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"/internal/subscriptions/{Uri.EscapeDataString(sub)}/portal");
            req.Headers.Add("X-Internal-Key", internalApiKey);
            using var resp = await client.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                var link = await resp.Content.ReadFromJsonAsync<ChessSchool.Contracts.PortalLinkDto>(ct);
                if (!string.IsNullOrEmpty(link?.Url)) return Results.Redirect(link.Url);
            }
            return Results.Redirect("/premium"); // портал недоступен (dev/нет клиента) — назад
        }).RequireAuthorization();

        // Вытягивание статуса (если вебхук Paddle не дошёл/опоздал). Сначала точный путь по транзакции (если
        // есть txn из success-URL), затем надёжное восстановление по e-mail пользователя — оно срабатывает,
        // даже когда у нас нет строки подписки и не сохранён txn (например, после ручного снятия в админке).
        app.MapPost("/premium/reconcile", async (HttpContext ctx, string? txn, IHttpClientFactory http,
            ChessSchool.Arena.Services.IPlayerEntitlements ents, CancellationToken ct) =>
        {
            var sub = ctx.User.FindFirst("sub")?.Value;
            if (string.IsNullOrEmpty(sub)) return Results.Unauthorized();
            var email = ctx.User.FindFirst("email")?.Value
                ?? ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
            var client = http.CreateClient(ChessSchool.Arena.Services.PlayerEntitlements.HttpClientName);

            // 1) Точный путь: по transaction id из возврата с checkout.
            if (!string.IsNullOrEmpty(txn))
            {
                using var rt = new HttpRequestMessage(HttpMethod.Post, "/internal/subscriptions/reconcile-transaction")
                { Content = System.Net.Http.Json.JsonContent.Create(new ChessSchool.Contracts.ReconcileTxnRequest(txn)) };
                rt.Headers.Add("X-Internal-Key", internalApiKey);
                try { await client.SendAsync(rt, ct); } catch { /* недоступность ApiService — не падаем */ }
            }

            // 2) Safety net: refresh по сохранённой подписке, а если её нет — по e-mail пользователя.
            var refreshUrl = $"/internal/subscriptions/{Uri.EscapeDataString(sub)}/refresh"
                + (string.IsNullOrEmpty(email) ? "" : $"?email={Uri.EscapeDataString(email)}");
            using var rf = new HttpRequestMessage(HttpMethod.Post, refreshUrl);
            rf.Headers.Add("X-Internal-Key", internalApiKey);
            try { await client.SendAsync(rf, ct); } catch { /* недоступность ApiService — не падаем */ }

            ents.Invalidate(sub); // статус мог измениться — сбросить кэш ноды, чтобы reload показал актуальное
            return Results.Ok();
        }).RequireAuthorization().DisableAntiforgery();

        // ---------------- Админка управления подписками (тонкий клиент /admin/subscriptions) ----------------
        // Прокси к ApiService (источник истины) под политикой Admin — браузер админки fetch'ит эти эндпоинты
        // в обычном request-контексте (НЕ из рендера Blazor, где исходящий HTTP зависает — грабля #12).
        // После изменения сбрасываем кэш entitlement на ноде, чтобы статус подхватился сразу (другие ноды — по TTL).
        app.MapGet("/admin/api/subscriptions", async (IHttpClientFactory http, CancellationToken ct) =>
        {
            var client = http.CreateClient(ChessSchool.Arena.Services.PlayerEntitlements.HttpClientName);
            using var req = new HttpRequestMessage(HttpMethod.Get, "/internal/admin/subscriptions?take=500");
            req.Headers.Add("X-Internal-Key", internalApiKey);
            try
            {
                using var resp = await client.SendAsync(req, ct);
                var rows = resp.IsSuccessStatusCode
                    ? await resp.Content.ReadFromJsonAsync<List<ChessSchool.Contracts.AdminSubscriptionDto>>(ct)
                    : null;
                return Results.Ok(rows ?? []);
            }
            catch { return Results.Ok(Array.Empty<ChessSchool.Contracts.AdminSubscriptionDto>()); }
        }).RequireAuthorization("Admin");

        app.MapPost("/admin/api/subscriptions/by-email", async (ChessSchool.Contracts.AdminSetByEmailRequest body,
            IHttpClientFactory http, ChessSchool.Arena.Services.IPlayerEntitlements ents, CancellationToken ct) =>
        {
            var client = http.CreateClient(ChessSchool.Arena.Services.PlayerEntitlements.HttpClientName);
            using var req = new HttpRequestMessage(HttpMethod.Post, "/internal/admin/subscriptions/by-email")
            { Content = System.Net.Http.Json.JsonContent.Create(body) };
            req.Headers.Add("X-Internal-Key", internalApiKey);
            using var resp = await client.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
            {
                try { ents.Invalidate(System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("sub").GetString()); }
                catch { /* не критично — кэш истечёт по TTL */ }
            }
            return Results.Content(json, "application/json", null, (int)resp.StatusCode);
        }).RequireAuthorization("Admin").DisableAntiforgery();

        app.MapPost("/admin/api/subscriptions/{sub}", async (string sub, ChessSchool.Contracts.AdminSetSubscriptionRequest body,
            IHttpClientFactory http, ChessSchool.Arena.Services.IPlayerEntitlements ents, CancellationToken ct) =>
        {
            var client = http.CreateClient(ChessSchool.Arena.Services.PlayerEntitlements.HttpClientName);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"/internal/admin/subscriptions/{Uri.EscapeDataString(sub)}")
            { Content = System.Net.Http.Json.JsonContent.Create(body) };
            req.Headers.Add("X-Internal-Key", internalApiKey);
            using var resp = await client.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode) ents.Invalidate(sub);
            return Results.Content(json, "application/json", null, (int)resp.StatusCode);
        }).RequireAuthorization("Admin").DisableAntiforgery();

        app.MapDelete("/admin/api/subscriptions/{sub}", async (string sub, IHttpClientFactory http,
            ChessSchool.Arena.Services.IPlayerEntitlements ents, CancellationToken ct) =>
        {
            var client = http.CreateClient(ChessSchool.Arena.Services.PlayerEntitlements.HttpClientName);
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"/internal/admin/subscriptions/{Uri.EscapeDataString(sub)}");
            req.Headers.Add("X-Internal-Key", internalApiKey);
            using var resp = await client.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode) ents.Invalidate(sub);
            return Results.StatusCode((int)resp.StatusCode);
        }).RequireAuthorization("Admin").DisableAntiforgery();
    }
}
