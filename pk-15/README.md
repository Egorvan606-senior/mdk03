# ПК № 15 · Релизный процесс, Runbook и ADR · OrderService (вариант 10)

Иванов Е.М., группа И14-23-9. Кейс 10 — «Внедрение защищённого WAF перед API».

| Документ | Файл |
|---|---|
| Регламент: окно обслуживания, smoke-тесты, план отката | [docs/RELEASE_REGULATION.md](docs/RELEASE_REGULATION.md) |
| Runbook: 504 Gateway Timeout платёжного шлюза | [docs/runbooks/RB-001-payment-gateway-504.md](docs/runbooks/RB-001-payment-gateway-504.md) |
| Runbook: ложные срабатывания WAF (403) | [docs/runbooks/RB-002-waf-false-positive-403.md](docs/runbooks/RB-002-waf-false-positive-403.md) |
| ADR-001: платёжный шлюз через IPaymentService | [docs/adr/adr-001-payment-gateway.md](docs/adr/adr-001-payment-gateway.md) |
| ADR-002: WAF перед API | [docs/adr/adr-002-waf-before-api.md](docs/adr/adr-002-waf-before-api.md) |
| Манифесты Ingress с WAF (On / откат DetectionOnly) | [deploy/k8s](deploy/k8s) |
| Журнал изменений | [CHANGELOG.md](CHANGELOG.md) |

## Запуск в Visual Studio

1. Открыть `OrderService.sln`, профиль запуска **OrderService (WAF On)** → F5 — откроется `/health`.
2. Smoke-тесты: **Тест → Обозреватель тестов → Запустить все** (или `dotnet test`).
3. Профиль **OrderService (откат: WAF DetectionOnly)** — проверка состояния после отката.
