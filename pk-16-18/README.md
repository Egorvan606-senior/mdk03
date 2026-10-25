# ПК № 16–18 · CALS: цифровой след, регламент релиза, откат, тестирование и документация

Иванов Е.М., группа И14-23-9. **Вариант 10 — «Спортивный комплекс»**, задача Jira **CAMPUS-110**:
логирование записи в бассейн/зал, изменение формата поля `TimeSlot` (string → `TimeRange`),
sanity-тест «запись на слот тренировки с отменой». Релиз **v1.6.0**.

| Блок | Что сделано | Где |
|---|---|---|
| 1. Цифровой след | теги `jira.issue`, `campus.entity_id` в `Activity.Current` и в span `sport.booking.create`; цепочка Jira → Commit → Trace Tag; связь лога и Jaeger через `traceparent` | `src/.../Telemetry/CampusTelemetry.cs`, `Controllers/SportBookingsController.cs`, [docs/DIGITAL_TRACE.md](docs/DIGITAL_TRACE.md) |
| 2. Регламент и Change Management | Branch Protection Rules (запрет push/force push, xUnit в CI, 2 коллеги + Tech Lead); Change Request релиза v1.6.0 с окном обслуживания | [docs/branch-protection.md](docs/branch-protection.md), [docs/change-requests/CR-2026-110.md](docs/change-requests/CR-2026-110.md) |
| 3. Откат и миграции | `kubectl rollout undo/status/history`; SQL Expand → Migrate → Contract + скрипт отката | CR, раздел 5; [db/migrations](db/migrations) |
| 4. Тесты и документация | xUnit sanity-тест с Cleanup (production) + стенд-тесты; Runbook `DatabaseConnectionPoolExhausted`; ADR; CHANGELOG 1.6.0 | [tests](tests/SmartCampus.Sport.SanityTests), [docs/runbooks](docs/runbooks), [docs/adr](docs/adr), [CHANGELOG.md](CHANGELOG.md) |

## Запуск

* Visual Studio: `SmartCampus.Sport.sln` → профиль **Sport API (стенд, трассы в консоль)** → F5.
  Запросы — файл `SmartCampus.Sport.Api.http` (кнопка *Send request*); span'ы с тегами печатаются в консоль сервиса.
* Тесты: **Тест → Обозреватель тестов → Запустить все** (production sanity пропускается без `SANITY_BASE_URL`).
* Миграции: `PGHOST=localhost PGUSER=postgres ./db/test/run_migration_test.sh` (результат — `docs/evidence/migration-test.txt`).
