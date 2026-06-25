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

## Чистка кода

### Осиротевший `SigningKeyProvider` / `TokenService` в Auth
**Приоритет:** низкий.

После перехода Auth на **OpenIddict** (dev-сертификат подписи) классы
[SigningKeyProvider.cs](../ChessSchool.Auth/Services/SigningKeyProvider.cs) и
[TokenService.cs](../ChessSchool.Auth/Services/TokenService.cs) больше **не регистрируются** в
`Program.cs` — токены выпускает OpenIddict. Эти классы (и переменная `Jwt:KeyPath`) остались
только в тесте `JwksSecurityTests`.

**Что сделать:** либо удалить мёртвый код и переписать `JwksSecurityTests` на реальный JWKS-эндпоинт
OpenIddict (`/.well-known/jwks`), либо явно задокументировать, зачем классы сохранены.
