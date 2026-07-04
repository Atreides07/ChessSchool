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
| Распределённый лимитер (мультисервер) | ✅ | Есть Redis → `RedisFixedWindowRateLimiter` ([RedisRateLimiting.cs](../ChessSchool.Auth/RedisRateLimiting.cs)): общий счётчик на все ноды (атомарный Lua `INCRBY`+`PEXPIRE`), лимит не размножается на реплики. Нет Redis → in-memory (dev/одна нода). Переключается по строке `redis`. **Fail-open** при сбое Redis (доступность важнее в этом узле). |

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
| Инвалидация сессий | ✅ | Отзыв **всех OIDC-токенов/разрешений** пользователя (`IOpenIddictTokenManager`/`…AuthorizationManager`) — краденые access/refresh умирают. Плюс **security-stamp** (см. §7): смена пароля перевыпускает метку → cookie-сессии IdP на всех устройствах отклоняются в пределах интервала проверки. |
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
Статус: ✅ (Arena/Auth).

**ЛК школы** (✅, модель владения): у `School` есть `OwnerSub` (IdP-`sub` тренера). Web — доверенный **BFF**:
страницы `/school`, `/attribution`, `/students/{id}` под `[Authorize]` (`AuthorizeRouteView` → неавторизованный
уводится на `/signin`); `sub` читается из `AuthenticationStateProvider` (работает и в InteractiveServer-контуре, где
нет `HttpContext`). SchoolApiClient ходит в ApiService server-to-server с `X-Internal-Key` (DelegatingHandler) и
передаёт вошедшего пользователя в `X-Acting-Sub`. Доменные эндпоинты ApiService — в группе `RequireInternalKey`
+ `RequireActingSub`; каждый обработчик проверяет владение через `SchoolAccessService` (`OwnsSchool`/`OwnsStudent`,
резолв `Student.GroupId→Group.SchoolId`) → `403` на чужой школе, `401` без ключа/sub. Провижининг: `GET /my-school`
(get-or-create) выдаёт школу владельца вместо фикс. `Demo.SchoolId`. `X-Acting-Sub` доверенный, т.к. канал закрыт
`X-Internal-Key` (constant-time) — спуфинг извне невозможен (тот же уровень доверия, что GameServer→ApiService).
**Публичный `/share/{token}`** остаётся анонимным (capability-URL родителю) — вне защищённой группы. Демо-школа
засеяна с `Demo.OwnerSub` (локаль/тесты действуют как владелец). Внедрено 2026-07-04 (было: открыто анониму).

**Смена e-mail** (✅): НЕподтверждённый адрес меняется сразу (исправить опечатку). **Подтверждённый** — по схеме
**verify-new-before-switch**: адрес не меняется, пока владение новым не доказано ссылкой (`AppUser.PendingEmail`,
purpose `ChangeEmail`, `/account/confirm-email-change`); ссылка уходит на **новый** адрес, уведомление — на **старый**
(OWASP); на confirm перевыпускается security-stamp (смена идентичности → прочие сессии гаснут).

## 7. Сессии, cookie, ключи

| Аспект | Статус | Детали |
|---|---|---|
| Cookie IdP-сессии | ✅ | `idp_sso`, `SameSite=Lax`, `HttpOnly` (дефолт), `Secure` (SameAsRequest → https в проде), скользящие **8ч**. |
| Регенерация при входе | ✅ | Новый `SignInAsync` при login/confirm/reset/change-email. |
| Раздутая cookie → HTTP 431 | ✅ | Server-side **ticket-store** (в cookie только ключ) + `Kestrel MaxRequestHeadersTotalSize=256KB`. [SsoExtensions](../ChessSchool.WebAuth/SsoExtensions.cs). |
| Общий keyring (мультисервер) | ✅ | DataProtection: Redis есть → общий keyring; нет → файловый. Ticket-store: Redis → `DistributedCacheTicketStore`, нет → `FileSystemTicketStore` (шифрован DataProtection). [Extensions](../ChessSchool.ServiceDefaults/Extensions.cs). |
| Security-stamp (логаут на всех устройствах) | ✅ | `AppUser.SecurityStamp` едет в claim cookie; `OnValidatePrincipal` сверяет его с БД и разлогинивает при несовпадении. Смена пароля перевыпускает метку → все прочие сессии отклоняются. Проверка с интервалом `Auth:SecurityStamp:ValidateMinutes` (дефолт **5**; `0` = каждый запрос) — баланс мгновенности и нагрузки на БД. Миграция `AddSecurityStamp` (grandfather: уникальная метка существующим). |
| MFA (TOTP) | ✅ | Двухфакторка ([Totp](../ChessSchool.Auth/Totp.cs)/[MfaService](../ChessSchool.Auth/MfaService.cs)): RFC 6238 (SHA-1, 6 цифр, 30с), совместимо с Google Authenticator и пр. Секрет в БД **зашифрован DataProtection** (общий keyring в мультисервере). Логин при включённой MFA — двухшаговый (пароль → второй фактор; между шагами короткоживущий DataProtection-маркер `idp_mfa`, 5 мин). **Резервные коды** одноразовые (в БД только SHA-256-хэш). Настройка — `/account/mfa`. Миграция `AddMfa`. |
| Обязательная MFA для админов | ✅ | `Auth:Mfa:RequiredForAdmins` (дефолт **вкл**): админ (e-mail в `Admin:Emails`) без 2FA форсится в настройку на входе, а **authorize не выдаёт код** до включения → нет `role=admin` в приложении без второго фактора. Отключить 2FA админу нельзя, пока правило включено. |

## 8. Аудит auth-событий

| Аспект | Статус | Детали |
|---|---|---|
| Запись событий | ✅ | `login success/failure`, `register`, `email confirmed`, `confirmation resent`, `email changed`, `password reset requested/done`. [AuthAudit](../ChessSchool.Auth/AuthAudit.cs), таблица `AuthEvents` (миграция `AddAuthAudit`). |
| Что пишется | ✅ | Тип, `UserId`, e-mail, IP, User-Agent, деталь, время. **Без секретов** (ни пароля, ни сырого токена). |
| Общий стор | ✅ | PostgreSQL — виден всем нодам; индексы по (email,время)/(userId,время)/времени. |
| Устойчивость | ✅ | Best-effort: сбой записи аудита не роняет auth-флоу (глушится + warning). |
| Метрики для алертинга | ✅ | Счётчики `chessschool.auth.events` (тег `type`) и `chessschool.auth.ratelimit.rejected` (тег `path`) ([AuthMetrics](../ChessSchool.Auth/AuthMetrics.cs)) экспортируются через OpenTelemetry — основа дашбордов и порогов. |
| Уведомление о входе с нового устройства | ✅ | Успешный вход с ранее не виденного IP (`ip is not null && были прежние входы && IP новый`) → письмо владельцу ([EmailTemplates](../ChessSchool.Auth/Email/EmailTemplates.cs) `NewSignIn`) + событие `NewDeviceLogin`. IP берётся из forwarded-заголовков. |
| Готовые пороговые правила алертинга | ✅ | Спецификация ниже; провязывается в системе мониторинга (Grafana/Datadog) поверх OTel-метрик. |

### Рекомендуемые пороговые правила (провязать в системе мониторинга)

На метриках/событиях выше (значения — стартовые, калибровать по трафику):

- **Брутфорс/бомбинг:** всплеск `chessschool.auth.ratelimit.rejected` — > 50/мин суммарно ИЛИ > 20/мин на один `path` → warning; кратный рост → critical. Прямой признак атаки (rate-limiter уже режет, алерт даёт видимость).
- **Подбор пароля:** rate `chessschool.auth.events{type=LoginFailure}` / `{type=LoginSuccess}` > 5 на скользящем окне 10 мин → warning (аномально много фейлов на успех).
- **Массовая компрометация:** `chessschool.auth.events{type=NewDeviceLogin}` > базовой линии ×3 → warning (волна входов с новых IP).
- **Атака на сброс:** `chessschool.auth.events{type=PasswordResetRequested}` > 30/мин → warning.
- **Отказ MFA:** всплеск `chessschool.auth.events{type=MfaChallengeFailed}` для одного пользователя → возможная кража пароля при живой 2FA.
- **Тишина аудита:** отсутствие любых `chessschool.auth.events` > 15 мин в рабочее время → возможен сбой IdP/пайплайна метрик (dead-man switch).

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

Все пункты из исходного чек-листа безопасности внедрены. Возможные усиления на будущее (низкий остаточный риск):

1. **Провязать пороговые правила алертинга** в системе мониторинга (Grafana/Datadog) по спецификации §8 — код-сторона (метрики/события/сигнал rejected) готова, остаётся конфиг в мониторинге. (§8)
2. **Распределённый rate-limiter покрыт**, но при экстремальном масштабе можно перейти на sliding-window/token-bucket в Redis вместо fixed-window. (§2)
3. **Пережатие/уменьшение картинок трансляций при ингесте** (миниатюры ~280px сейчас в среднем 44 КБ) — нужна image-библиотека (ImageSharp — split-лицензия; или Magick.NET); решение по зависимости за владельцем. Смягчено: картинки lazy+`immutable`.
