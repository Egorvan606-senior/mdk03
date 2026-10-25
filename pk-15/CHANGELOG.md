# Журнал изменений (Changelog) — OrderService

Формат — [Keep a Changelog](https://keepachangelog.com/ru/1.1.0/), версии — [SemVer](https://semver.org/lang/ru/).

## [2.0.0] - 2026-10-28

### ⚠️ Breaking Changes (Ломающие изменения)
- API защищён WAF (ModSecurity + OWASP CRS): запросы с признаками SQL-инъекций и XSS получают `403`
  с полем `ruleId`; тело запроса ограничено 64 КБ (`413`), частота — 50 запросов/с с IP.
- Оплата может вернуть статус `AwaitingChallenge` и `challengeUrl` (подтверждение 3-D Secure 2.0) —
  клиенты должны открыть страницу подтверждения банка.

### Added (Новые функции)
- Новый платёжный шлюз с 3-D Secure 2.0 через интерфейс `IPaymentService` (ADR-001).
- WAF перед API, режимы `On` / `DetectionOnly` / `Off`, заголовок ответа `X-Waf-Mode` (ADR-002).
- `GET /health` проверяет подключение к БД (`DbContext.Database.CanConnect()`) и показывает шлюз и режим WAF.
- Smoke-тесты релиза (`dotnet test --filter Category=Smoke`), Runbook RB-001 и RB-002.

### Fixed (Исправления ошибок)
- Таймаут платёжного шлюза теперь возвращает `504 Gateway Timeout` с описанием, а не `500`.
