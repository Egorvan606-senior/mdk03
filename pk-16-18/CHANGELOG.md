# Changelog

Все значимые изменения сервиса `sport-service` (модуль «Спортивный комплекс»
ИС «Умный кампус») фиксируются в этом файле.

Формат основан на [Keep a Changelog](https://keepachangelog.com/ru/1.1.0/),
версии соответствуют [Semantic Versioning](https://semver.org/lang/ru/).

## [Unreleased]

### Breaking Changes
- [CAMPUS-110] Фаза Contract (CR-2026-118): колонка `sport.bookings.time_slot`
  будет удалена, `time_range` станет `NOT NULL`. Прямые потребители БД
  (BI-отчёты, выгрузки) должны читать `time_range`. Откат на v1.5.x после
  этого изменения невозможен.
- [CAMPUS-110] В API v2.0.0 будет удалено устаревшее поле `timeSlot`.

### Added
- [CAMPUS-110] Ограничение БД `ex_bookings_no_overlap`: пересекающиеся активные
  брони одного зала в один день запрещены на уровне PostgreSQL.

## [1.6.0] - 2026-10-28

### Added
- [CAMPUS-110] Тип `sport.timerange` и колонка `sport.bookings.time_range` —
  слот бронирования хранится как диапазон времени `[начало, конец)`.
- [CAMPUS-110] Поле `timeRange { start, end }` и `durationMinutes` в API
  `POST/GET /api/v1/sport/bookings`; запрос принимает новый формат слота.
- [CAMPUS-110] Трассировка OpenTelemetry: теги `jira.issue`, `campus.entity_id`, `campus.booking_id`,
  `campus.facility_id`, `campus.time_range`; заголовок ответа `X-Trace-Id`; TraceId в каждой строке лога.
- [CAMPUS-110] Двойная запись (dual write) в `time_slot` и `time_range`,
  режим переключается настройкой `BookingSchema:Phase`.
- [CAMPUS-110] Миграции Expand and Contract: `01_expand.sql`, `02_migrate.sql`,
  `03_contract.sql`, скрипт отката `rollback/01_expand_down.sql`.
- [CAMPUS-110] Production sanity-тест с очисткой тестовых данных (Cleanup)
  и служебный эндпоинт удаления записей `sanity-bot`; стенд-тесты того же сценария для CI.
- [CAMPUS-110] Runbook `DatabaseConnectionPoolExhausted`, правила защиты ветки `main`, CI-проверки `xunit-tests`, `db-migrations` и `k8s-manifests`.
- [CAMPUS-110] Манифесты Kubernetes: `deploy/k8s/sport-service.yaml` (RollingUpdate без простоя,
  readiness/liveness `/health`, история ревизий для `kubectl rollout undo`) и манифест отката
  `deploy/k8s/rollback/sport-service-v1.5.3.yaml`.

### Fixed
- [CAMPUS-110] Двойное бронирование зала при одновременных запросах: проверка
  пересечения слотов теперь выполняется по диапазону, а не по строке.
- [CAMPUS-110] Слоты в формате `18.00 - 19.30` и `9:00-10:00` больше не
  отклоняются — приводятся к единому виду `HH:mm`.
- [CAMPUS-110] Некорректные слоты (конец раньше начала, вне часов работы
  07:00–23:00, короче 30 минут) отклоняются с ответом 400 и понятным сообщением.

### Deprecated
- [CAMPUS-110] Поле `timeSlot` в запросе и ответе API — используйте `timeRange`.
- [CAMPUS-110] Колонка БД `sport.bookings.time_slot`.

### Breaking Changes
- Нет. Изменение контракта API аддитивное, клиенты v1.5.x продолжают работать.

## [1.5.3] - 2026-09-30

### Fixed
- [CAMPUS-097] Отмена брони больше не возвращает 500 при повторном вызове.

[Unreleased]: https://github.com/Egorvan606-senior/mdk03/compare/v1.6.0...HEAD
[1.6.0]: https://github.com/Egorvan606-senior/mdk03/compare/v1.5.3...v1.6.0
[1.5.3]: https://github.com/Egorvan606-senior/mdk03/releases/tag/v1.5.3
