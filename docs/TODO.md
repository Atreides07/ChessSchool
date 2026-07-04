# TODO / технический долг

Список известных задач, которые осознанно отложены. Заводить сюда то, что иначе теряется
(пока в репозитории нет remote/issue-трекера). При появлении GitHub — перенести в Issues.

## Безопасность

### ⚠️ ОТКРЫТО: ЛК школы (Web + ApiService) не гейтится авторизацией
Найдено аудитом авторизации 2026-07-04. Страницы `/school`, `/attribution`, `/students/{id}`
([ChessSchool.Web/Components/Pages](../ChessSchool.Web/Components/Pages/)) без `[Authorize]`; доменные
эндпоинты [ApiService/Program.cs](../ChessSchool.ApiService/Program.cs) L77–125 (`/schools/{id}/students`,
`/students/{id}`, `/games/{id}/attribute`, `/students/{id}/link|share`) — вне группы `/internal`, открыты
анониму **во всех окружениях** (комментарий «в проде гейтятся JWT» — гейт не реализован).
**Остаточный риск:** чтение PII учеников, создание/привязка учеников, раздача родительских ссылок,
искажение рейтинга атрибуцией — без входа. Уместно, пока это лишь локальное демо на фикс. `Demo.SchoolId`;
опасно, как только Web/ApiService доступны извне.
**Что нужно:** модель владения школой (аккаунт↔школа/роль тренера), затем `[Authorize]` на страницах ЛК +
авторизация доменных эндпоинтов ApiService (JWT от IdP или internal-key от веб-бэкенда) с проверкой, что
пользователь владеет запрашиваемой школой/учеником. До этого — хотя бы гейтить открытое состояние
`IsDevelopment()`. См. [docs/SECURITY.md](SECURITY.md) §6.

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
