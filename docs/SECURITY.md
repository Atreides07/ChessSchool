# SECURITY.md — реестр безопасности ChessSchool

Единая карта **всех принятых и внедрённых политик и настроек безопасности**. Ведётся по правилу «реестр
безопасности» из [CLAUDE.md](../CLAUDE.md) (§Принцип: безопасность по умолчанию) — обновляется в том же PR,
что и изменение auth/сессий/секретов/CORS/токенов/лимитов, как часть Definition of Done.

**Секретов здесь нет** — только политики, параметры, дефолты и флаги. Значения ключей/паролей/строк
подключения живут в секрет-менеджере (прод) и AppHost/`.env` (локально); шаблон — [env.example](../env.example).

Легенда статуса: ✅ внедрено · 🟡 частично · ⏳ отложено (с оценкой риска).

Дата актуализации: 2026-07-03.

---

## 1. Пароли (NIST 800-63B)

| Аспект | Статус | Детали |
|---|---|---|
| Решает длина, без композиции/ротации | ✅ | [Password.cs](../ChessSchool.Auth/Password.cs) `PasswordPolicy`. Min — `Auth:Password:MinLength` (дефолт **8**), max **128** (анти-DoS на хэшировании), длинные парольные фразы разрешены. |
| Проверка по базам утечек (HIBP k-anonymity) | ✅ | `PwnedPasswords`/`PwnedPasswordChecker`. Наружу уходит только 5-символьный префикс SHA-1; `Add-Padding: true`; padding-строки (count=0) игнорируются. Флаг `Auth:Password:CheckPwned` (дефолт **вкл**). |
| Поведение при недоступности HIBP | ✅ | **Fail-open**: таймаут 5с/ошибка → пароль пропускается (регистрация/сброс не блокируются), пишется warning. |
| Хэширование | ✅ | ASP.NET `PasswordHasher<AppUser>` (PBKDF2). В БД только хэш; сырой пароль нигде не логируется/не хранится. |
| Проверка политики на всех точках ввода | ✅ | Регистрация и сброс пароля прогоняют длину + HIBP. |

## 2. Rate-limiting (анти-перебор и анти-бомбинг письмами)

| Аспект | Статус | Детали |
|---|---|---|
| Лимит на перебор пароля/токенов | ✅ | Политика `auth` на `login`, `confirm`, `reset`. `RateLimit:Auth:Permit` (дефолт **20**) / `:WindowMinutes` (**5**). [Program.cs](../ChessSchool.Auth/Program.cs). |
| Лимит на отправку писем | ✅ | Политика `email-send` на `register`, `resend`, `change-email`, `forgot`. `RateLimit:Email:Permit` (дефолт **5**) / `:WindowMinutes` (**15**). |
| Партиционирование | ✅ | По IP клиента (за прокси корректно благодаря forwarded-заголовкам). `429` + `Retry-After`. |
| Распределённый лимитер (мультисервер) | ⏳ | Сейчас **in-memory, по-нодовый** → суммарный лимит = N×порог. Риск: при многих нодах лимит мягче. Смягчение: пороги с запасом. Follow-up — Redis-лимитер. |

## 3. Анти-энумерация аккаунтов

| Аспект | Статус | Детали |
|---|---|---|
| Constant-time логин | ✅ | При отсутствии пользователя всё равно считается dummy-хэш (`dummyPasswordHash`) — тайминг не выдаёт существование аккаунта. |
| Нейтральные ответы | ✅ | `forgot` и `resend` всегда отвечают «письмо отправлено, если аккаунт есть»; логин — единое сообщение об ошибке. |
| Осознанный компромисс | 🟡 | Мягкий nudge «подтвердите e-mail» чуть повышает энумерацию, но заметно улучшает активацию — приемлемо при rate-limiting. |

## 4. Одноразовые e-mail-токены (подтверждение / сброс)

| Аспект | Статус | Детали |
|---|---|---|
| Хранение | ✅ | В БД только **SHA-256-хэш**; сырой токен — лишь в ссылке письма. [EmailTokenService](../ChessSchool.Auth/EmailTokenService.cs). |
| Одноразовость | ✅ | Гасятся при использовании; при перевыпуске прежние неиспользованные — гасятся. |
| Срок жизни | ✅ | Подтверждение e-mail — **24ч**; сброс пароля — **1ч** (короче, т.к. даёт смену пароля). |
| Энтропия | ✅ | 32 байта из `RandomNumberGenerator`, URL-safe base64. |

## 5. Сброс пароля (OWASP Forgot Password)

| Аспект | Статус | Детали |
|---|---|---|
| Нейтральный запрос | ✅ | `/account/forgot` не раскрывает существование e-mail; rate-limit `email-send`. |
| Смена пароля по ссылке | ✅ | `/account/reset` — одноразовый токен (1ч), новый пароль проходит NIST+HIBP. |
| Инвалидация сессий | 🟡 | Отзыв **всех OIDC-токенов/разрешений** пользователя (`IOpenIddictTokenManager`/`…AuthorizationManager`) — краденые access/refresh умирают. Cookie IdP на др. устройствах живёт до истечения (скользящие 8ч). |
| Подтверждение владения | ✅ | Успешный сброс ставит `EmailConfirmed=true` (переход по ссылке доказывает владение адресом). |
| Уведомление владельцу | ✅ | Письмо «пароль изменён» ([EmailTemplates](../ChessSchool.Auth/Email/EmailTemplates.cs) `PasswordChanged`). |

## 6. Матрица доступа по подтверждению e-mail (мягкий гейт)

| Уровень доверия | Доступно |
|---|---|
| Аноним | публичные страницы (лендинг, `/p/{token}` родителю, листинги турниров) |
| Вошёл, e-mail **не** подтверждён | базовая ценность: играть/смотреть; nudge-баннер «подтвердите e-mail» |
| E-mail подтверждён | чувствительное: **оплата/премиум**, идентичность (смена подтверждённого e-mail) |
| Роль `admin` | админ-функции (claim `role=admin` из IdP) |

Механизм: claim **`email_verified`** едет в authorize/userinfo/cookie ([Program.cs](../ChessSchool.Auth/Program.cs)),
мапится в [WebAuth](../ChessSchool.WebAuth/SsoExtensions.cs); Арена — политика `ConfirmedEmail` на премиум + баннер.
Статус: ✅.

## 7. Сессии, cookie, ключи

| Аспект | Статус | Детали |
|---|---|---|
| Cookie IdP-сессии | ✅ | `idp_sso`, `SameSite=Lax`, `HttpOnly` (дефолт), `Secure` (SameAsRequest → https в проде), скользящие **8ч**. |
| Регенерация при входе | ✅ | Новый `SignInAsync` при login/confirm/reset/change-email. |
| Раздутая cookie → HTTP 431 | ✅ | Server-side **ticket-store** (в cookie только ключ) + `Kestrel MaxRequestHeadersTotalSize=256KB`. [SsoExtensions](../ChessSchool.WebAuth/SsoExtensions.cs). |
| Общий keyring (мультисервер) | ✅ | DataProtection: Redis есть → общий keyring; нет → файловый. Ticket-store: Redis → `DistributedCacheTicketStore`, нет → `FileSystemTicketStore` (шифрован DataProtection). [Extensions](../ChessSchool.ServiceDefaults/Extensions.cs). |
| Security-stamp (мгновенный логаут на всех устройствах) | ⏳ | Нет. Риск: после сброса пароля чужая cookie-сессия живёт до 8ч. Смягчение: OIDC-токены отзываются сразу, cookie короткоживущий. Follow-up. |
| MFA | ⏳ | Не реализовано. Кандидат — TOTP для админов/премиума. |

## 8. Аудит auth-событий

| Аспект | Статус | Детали |
|---|---|---|
| Запись событий | ✅ | `login success/failure`, `register`, `email confirmed`, `confirmation resent`, `email changed`, `password reset requested/done`. [AuthAudit](../ChessSchool.Auth/AuthAudit.cs), таблица `AuthEvents` (миграция `AddAuthAudit`). |
| Что пишется | ✅ | Тип, `UserId`, e-mail, IP, User-Agent, деталь, время. **Без секретов** (ни пароля, ни сырого токена). |
| Общий стор | ✅ | PostgreSQL — виден всем нодам; индексы по (email,время)/(userId,время)/времени. |
| Устойчивость | ✅ | Best-effort: сбой записи аудита не роняет auth-флоу (глушится + warning). |
| Алертинг по аномалиям | ⏳ | Данные пишутся; дашборды/алерты (всплеск `LoginFailure`, вход с нового IP) — follow-up (см. наблюдаемость OpenTelemetry). |

## 9. IdP / OIDC / токены

| Аспект | Статус | Детали |
|---|---|---|
| Flow | ✅ | OpenIddict: authorization code + **PKCE** (обязателен), refresh. Access-токен не шифруется (валидация ресурс-серверами по JWKS). |
| JWKS без приватного материала | ✅ | JWK строится только из публичных `n,e`; защищено `JwksSecurityTests`. |
| Сертификаты подписи/шифрования | ✅ | Dev — эфемерные; прод — X.509 из конфига `OpenIddict:SigningCertificate`/`:EncryptionCertificate` ([Certificates.cs](../ChessSchool.Auth/Certificates.cs)). |
| Роль admin в токене | ✅ | Источник истины — IdP (`Admin:Emails`); claim `role=admin` в access/id-токене. |

## 10. CORS, транспорт, секреты

| Аспект | Статус | Детали |
|---|---|---|
| CORS GameServer | ✅ | Dev — любой origin (порты Aspire динамические); **прод — строгий список** `Cors:Origins`. Any-origin + credentials в проде запрещён. [GameServer/Program.cs](../ChessSchool.GameServer/Program.cs). |
| Forwarded-заголовки | ✅ | За прокси/ingress доверяем `X-Forwarded-*` для корректного issuer/redirect_uri и IP в аудите/лимитере. |
| Транспорт | ✅ | Прод требует HTTPS; в Development требование ослаблено (локальные тесты без TLS). |
| Секреты | ✅ | Только из конфига/секрет-менеджера; **`.env` не читаем**, не коммитим (`.gitignore`: `.env`, `*.db*`, `**/keys/*.pem`). Внутренние S2S-вызовы — `X-Internal-Key`. |
| Server-to-server авторизация | ✅ | `/internal/*` эндпоинты Auth гейтятся `X-Internal-Key` (вне Development ключ обязателен). |

---

## Отложенное (follow-up с остаточным риском)

1. **Распределённый rate-limiter (Redis)** — сейчас лимит по-нодовый; риск мягче под мультисервером. (§2)
2. **Security-stamp** для мгновенной инвалидации cookie-сессий на всех устройствах после смены пароля. (§5, §7)
3. **MFA** (TOTP) — для админов и премиума. (§7)
4. **Алертинг по аудиту** — дашборды/пороги на всплески фейлов и вход с нового IP. (§8)
5. **Смена подтверждённого e-mail** по схеме verify-new-before-switch (сейчас меняется только неподтверждённый). (§6)
