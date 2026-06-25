# TODO / технический долг

Список известных задач, которые осознанно отложены. Заводить сюда то, что иначе теряется
(пока в репозитории нет remote/issue-трекера). При появлении GitHub — перенести в Issues.

## Безопасность

### Прод тонкого клиента `/play`: обновление access-токена (refresh)
**Приоритет:** высокий (блокер для прода онлайн-игры).

Браузерный тонкий клиент [Play.razor](../ChessSchool.Web/Components/Pages/Play.razor) подключается
к SignalR-хабу GameServer напрямую, используя access-токен. Сейчас токен берётся разово и
**протухает (~1 час)** — после этого reconnect к `/gamehub` падает на авторизации, и длинная
партия рвётся.

**Что сделать:** для SignalR-клиента задать `accessTokenFactory`, который тянет свежий токен с
серверного эндпоинта (через `refresh_token`), а не использует статичный токен из cookie/claim.
Тогда reconnect и долгие партии переживают истечение токена.

**Затронуто:** `ChessSchool.Web` (клиент `/play`, эндпоинт выдачи game-токена),
валидация в `ChessSchool.GameServer`.

**Связанные грабли:** см. CLAUDE.md (раздел «Грабли») и память проекта про тонкий клиент.

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
