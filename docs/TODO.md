# TODO / технический долг

Список известных задач, которые осознанно отложены. Заводить сюда то, что иначе теряется
(пока в репозитории нет remote/issue-трекера). При появлении GitHub — перенести в Issues.

## Безопасность

### ✅ ЛК школы гейтится авторизацией по владению (было ОТКРЫТО)
**Сделано** (2026-07-04, найдено аудитом авторизации). Добавлена модель владения `School.OwnerSub`; Web —
доверенный BFF: страницы ЛК под `[Authorize]` (`AuthorizeRouteView` → на `/signin`), `sub` из
`AuthenticationStateProvider`. Доменные эндпоинты [ApiService/Program.cs](../ChessSchool.ApiService/Program.cs)
вынесены в группу `RequireInternalKey` + `RequireActingSub`; владение проверяет `SchoolAccessService`
(403 на чужой школе, 401 без ключа/sub). Провижининг `GET /my-school` (get-or-create) заменил фикс.
`Demo.SchoolId`. Публичный `/share/{token}` остался анонимным. Ключ `X-Internal-Key` Web получает от AppHost.
Покрыто тестами (`ApiServiceTests`: 401/403/провижининг/анонимный share). См. [docs/SECURITY.md](SECURITY.md) §6.

### ✅ Прод тонкого клиента `/play`: обновление access-токена (refresh)
**Сделано** (пункт оказался уже закрыт в коде). SignalR-клиент в
[Play.razor](../ChessSchool.Web/Components/Pages/Play.razor) подключается к `/gamehub` с
`accessTokenFactory: getToken` + `withAutomaticReconnect()` — фабрика вызывается на каждом
connect/reconnect и тянет свежий токен с серверного эндпоинта `GET /api/game-token`
([Web/Program.cs](../ChessSchool.Web/Program.cs)). Эндпоинт читает токены из тикета сессии
(`SaveTokens=true`), и при истечении (буфер 30 с) обновляет access-токен по `refresh_token`
(grant `refresh_token` → `/connect/token`), сохраняя новые токены через `SignInAsync`
(переживает несколько нод — общий ticket-store в Redis). GameServer валидирует JWT на
(re)connect (`OnMessageReceived` берёт токен из query `access_token`). Итог: reconnect и
долгие партии переживают истечение access-токена.

### ✅ Возвращён тест безопасности JWKS (runtime-проверка)
**Сделано.** Добавлен интеграционный тест `AuthIntegrationTests.Jwks_ExposesOnlyPublicKeyMaterial`
(поднимает Auth через `WebApplicationFactory` + Testcontainers PostgreSQL, бьёт по `/.well-known/jwks`,
проверяет наличие `n`/`e` и отсутствие `d,p,q,dp,dq,qi`). Там же
`Authorize_WhenCookieUserMissing_RedirectsToLogin_NotServerError` — регрессия на graceful re-login.
Требует Docker (образ `postgres:18.3`).

## Чистка кода

### ✅ Удалён осиротевший `SigningKeyProvider` / `TokenService` в Auth
**Сделано.** После перехода Auth на OpenIddict эти классы и `JwksSecurityTests` больше не
регистрировались/не отражали боевой код — удалены. Токены и JWKS выпускает OpenIddict.
Осталось закрыть долг по runtime-тесту JWKS (см. раздел «Безопасность» выше).
