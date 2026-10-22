# Отчёт по практической работе № 5

**Проектирование безопасных релизных процессов, паттернов миграции данных
и эксплуатационной документации ИС «Умный кампус»**

| | |
|---|---|
| **Дисциплина** | МДК 03.01 Внедрение и поддержка компьютерных систем |
| **Студент** | Иванов Егор Михайлович |
| **Группа** | И14-23-9 |
| **Вариант** | 10 |
| **Модуль ИС** | Спортивный комплекс |
| **Задача Jira** | CAMPUS-110 |
| **Суть миграции** | Изменение формата TimeSlot (string → TimeRange) |
| **Руководитель** | Чудинова А.А. |
| **Дата** | 23.10.2026 |

---

## Структура папки `homework-5/`

```text
homework-5/
├── REPORT.md                         ← этот отчёт
├── CHANGELOG.md                      ← журнал изменений (Keep a Changelog)
├── SmartCampus.Sport.sln             ← решение Visual Studio
├── src/SmartCampus.Sport.Api/        ← ASP.NET Core Web API (.NET 8)
│   ├── Controllers/SportBookingsController.cs   ← теги OpenTelemetry, бизнес-логика
│   ├── Telemetry/CampusTelemetry.cs             ← ActivitySource и имена тегов
│   ├── Domain/TimeRange.cs                      ← новый тип слота
│   ├── Domain/SportBooking.cs                   ← сущность и правила бронирования
│   ├── Data/PostgresBookingRepository.cs        ← dual write (Expand / Contract)
│   ├── Contracts/BookingContracts.cs            ← DTO API (аддитивное изменение)
│   └── Program.cs                               ← регистрация OpenTelemetry
├── tests/SmartCampus.Sport.SanityTests/
│   ├── ProductionSanityTests.cs                 ← production sanity + Cleanup
│   └── TimeRangeTests.cs                        ← модульные тесты
├── db/migrations/
│   ├── 00_baseline.sql                          ← исходная схема (для стенда/CI)
│   ├── 01_expand.sql  02_migrate.sql  03_contract.sql
│   └── rollback/01_expand_down.sql
├── db/test/run_migration_test.sh                ← сквозная проверка миграций (CI)
└── docs/
    ├── change-requests/CR-2026-110.md           ← Change Request
    ├── branch-protection.md                     ← правила защиты main
    ├── adr/0009-sport-timeslot-to-timerange.md  ← ADR
    └── evidence/                                ← журналы проверочных прогонов
```

## Ссылки на материалы

| Блок | Файл |
|---|---|
| 1. Цифровой след | [SportBookingsController.cs](src/SmartCampus.Sport.Api/Controllers/SportBookingsController.cs), [CampusTelemetry.cs](src/SmartCampus.Sport.Api/Telemetry/CampusTelemetry.cs), [Program.cs](src/SmartCampus.Sport.Api/Program.cs) |
| 2. Change Request | [docs/change-requests/CR-2026-110.md](docs/change-requests/CR-2026-110.md) |
| 2. Branch Protection | [docs/branch-protection.md](docs/branch-protection.md), [CI workflow](../.github/workflows/homework-5-ci.yml) |
| 3. Expand | [01_expand.sql](db/migrations/01_expand.sql) + dual write в [PostgresBookingRepository.cs](src/SmartCampus.Sport.Api/Data/PostgresBookingRepository.cs) |
| 3. Migrate | [02_migrate.sql](db/migrations/02_migrate.sql) |
| 3. Contract | [03_contract.sql](db/migrations/03_contract.sql) |
| 4. Sanity-тест | [ProductionSanityTests.cs](tests/SmartCampus.Sport.SanityTests/ProductionSanityTests.cs) |
| 4. ADR | [docs/adr/0009-sport-timeslot-to-timerange.md](docs/adr/0009-sport-timeslot-to-timerange.md) |
| 4. Changelog | [CHANGELOG.md](CHANGELOG.md) |

---

## Блок 1. Единый цифровой след (Digital Traceability)

В контроллере `SportBookingsController` на текущий span `Activity.Current`
(его создаёт инструментация ASP.NET Core для входящего HTTP-запроса) устанавливаются теги:

| Тег | Значение | Назначение |
|---|---|---|
| `jira.issue` | `CAMPUS-110` | связь запроса с задачей, по которой изменён код |
| `campus.booking_id` | GUID брони | доменный идентификатор сущности |
| `campus.facility_id` | номер зала | доменный идентификатор зала |
| `campus.time_range` | `[18:00,19:30)` | слот в новом формате |
| `campus.module` | `sport-complex` | модуль ИС |

Дополнительно создаётся дочерний span `sport.booking.create` из собственного
`ActivitySource("SmartCampus.Sport")`, номер задачи передаётся дальше через
`Baggage`, а клиент получает заголовок `X-Trace-Id` — по нему оператор Service Desk
находит запрос в Jaeger. В `Program.cs` подключены `AddAspNetCoreInstrumentation()`,
`AddSource("SmartCampus.Sport")` и экспорт OTLP (порт 4317).

Коммит с требуемым сообщением: **`[CAMPUS-110] Add tracing tags and business logic`**
(см. `git log` репозитория).

Фрагмент проверочного прогона (теги реальных span'ов, [полный журнал](docs/evidence/otel-trace-tags.txt)):

```text
span=sport.booking.create  trace=13ad5344…  jira.issue=CAMPUS-110, campus.facility_id=999,
     campus.time_range=[22:00,22:30), campus.booking_id=e97ef6bc-1b9f-48b6-ae8b-fbc9f0183f55
span=HttpRequestIn         trace=13ad5344…  jira.issue=CAMPUS-110, campus.module=sport-complex,
     campus.facility_id=999, campus.booking_id=e97ef6bc-1b9f-48b6-ae8b-fbc9f0183f55
```

## Блок 2. Регламент релиза и Change Management

### Текстовое описание заполненного Change Request (CR-2026-110)

**Релиз:** `sport-service` v1.9.0 (с v1.8.3), задача CAMPUS-110, нормальное изменение
со средним риском, согласуется CAB (владелец сервиса, DBA, дежурный SRE,
представитель спорткомплекса).

**Описание и бизнес-цель.** Слот бронирования зала переводится со строки
`time_slot` на диапазонный тип `sport.timerange`. Цель — исключить двойные
бронирования залов (строку нельзя проверить на пересечение), привести данные
к единому формату и получить аналитику загрузки залов по часам. В рамках CR
выполняются фазы Expand и Migrate; фаза Contract — отдельным CR-2026-118 не ранее
чем через 14 дней.

**Окно обслуживания:** среда 28.10.2026, 02:00–04:00 МСК. Обоснование: спорткомплекс
работает 07:00–23:00; по метрикам нагрузка на API бронирования в 02:00–05:00 менее
1 запроса в минуту (пики 07:30–09:00 и 19:00–22:00); окно начинается сразу после
резервного копирования БД (01:30–02:00) и не пересекается с ночным ETL (00:30–01:30);
середина недели (не пятница), не начало месяца и не сессия.

**План работ:** проверка свежего бэкапа → `01_expand.sql` → выкатка v1.9.0
(`kubectl set image …`, `kubectl rollout status`, RollingUpdate без недоступных подов) →
production sanity-тест → проверка трасс по тегу `jira.issue=CAMPUS-110` →
`02_migrate.sql` с контролем `mismatches = 0` → 30 минут наблюдения → закрытие окна.

**План отката:** при провале sanity-теста, 5xx > 2 % за 5 минут, p95 > 1 с или
расхождениях данных — `kubectl rollout undo deployment/sport-service -n smart-campus`
и `kubectl rollout status …` (RTO ≤ 10 минут). База данных не откатывается:
фаза Expand обратно совместима, v1.8.3 продолжает работать с `time_slot`.
При отмене CR целиком — `rollback/01_expand_down.sql`.

Полный документ: [CR-2026-110.md](docs/change-requests/CR-2026-110.md).

### Branch Protection Rules для `main`

PR обязателен; **2 одобрения**, сброс одобрений при новых коммитах, обязательный
Code Owner (DBA для `db/migrations`); обязательные проверки CI **`build-and-test`**
и **`db-migrations`**, ветка должна быть актуальной; закрытие всех обсуждений;
подписанные коммиты; линейная история; запрет force push и удаления; правила
обязательны для администраторов; сообщения коммитов по шаблону `^\[CAMPUS-\d+\] .+`.
Подробно, с JSON для GitHub API и аналогом для GitLab: [branch-protection.md](docs/branch-protection.md).

## Блок 3. Безопасная миграция БД (Expand and Contract)

| Фаза | Что делает | Почему без простоя |
|---|---|---|
| **01_expand.sql** | тип `sport.timerange`, nullable-колонка `time_range`, функция разбора старой строки, триггер синхронизации для старых клиентов, журнал ошибок | `ADD COLUMN` без DEFAULT меняет только метаданные; `lock_timeout = 3s`; скрипт идемпотентен |
| **C# dual write** | `PostgresBookingRepository.CreateAsync` пишет в `time_slot` и `time_range`; чтение — из `time_range` с запасным разбором `time_slot`; режим `BookingSchema:Phase` | старые и новые версии сервиса работают одновременно |
| **02_migrate.sql** | процедура `backfill_time_range` переносит историю пачками по 1000 строк с `COMMIT`, `FOR UPDATE SKIP LOCKED`; ошибочные строки — в журнал; контрольные запросы | короткие транзакции, процесс можно прервать и продолжить |
| **03_contract.sql** | предусловия (0 строк без диапазона, нет пересечений); `NOT NULL` через `CHECK NOT VALID` + `VALIDATE`; удаление триггера и `time_slot`; ограничение `EXCLUDE` против пересечений | валидация без длительной эксклюзивной блокировки |

Проверка на PostgreSQL 16 ([журнал](docs/evidence/migration-test.txt)):

```text
== 2. Expand (два прогона — проверка идемпотентности)
== 3. Старый клиент пишет только time_slot -> триггер заполняет time_range
   time_slot '18:00-19:30' -> time_range [18:00:00,19:30:00)
== 4. Откат expand и повторное применение
== 5. Migrate
   перенесено: 4966, в журнале ошибок: 35
== 6. Contract должен ОТКАЗАТЬСЯ работать при неразобранных ошибках
   contract отменён предусловием — OK
== 7. Исправление данных и Contract
   колонка time_slot удалена, time_range NOT NULL
== 8. Запрет пересечений после Contract
   пересекающаяся бронь отклонена (ex_bookings_no_overlap) — OK
OK: все проверки миграций CAMPUS-110 пройдены
```

## Блок 4. Sanity-тестирование и Docs-as-Code

**Production Sanity-тест** `ProductionSanityTests` (xUnit, `[Trait("Category","Sanity")]`):

1. создаёт тестовую бронь 22:00–22:30 в служебном зале 999 на дату через 60 дней
   (запись помечается `created_by = 'sanity-bot'` по токену `X-Sanity-Token`);
2. проверяет новый формат `timeRange`, сохранённое старое поле `timeSlot`,
   длительность, наличие `X-Trace-Id`;
3. читает бронь через `GET`, проверяет, что повторная бронь того же слота даёт `409 Conflict`;
4. второй тест проверяет, что старый клиент (строка `timeSlot`) по-прежнему принимается;
5. **Cleanup** — в `DisposeAsync()` (выполняется всегда, даже при упавших проверках)
   созданные записи удаляются и контрольным `GET` подтверждается `404`.
   API физически удаляет только записи `sanity-bot`.

Без переменной `SANITY_BASE_URL` тест помечается пропущенным, поэтому не ломает обычный CI.

Результаты ([журнал](docs/evidence/sanity-test.txt)):

```text
$ dotnet test --filter Category=Sanity
  [PASS] ProductionSanityTests.CreateBooking_InNewTimeRangeFormat_IsStoredAndReturnedWithLegacyField
         cleanup: OK
  [PASS] ProductionSanityTests.CreateBooking_InLegacyTimeSlotFormat_StillAccepted
         cleanup: OK
Итого: пройдено 2, упало 0, пропущено 0
```

Модульные тесты `TimeRangeTests`: 13 из 13 пройдено ([журнал](docs/evidence/unit-tests.txt)).

**ADR:** [docs/adr/0009-sport-timeslot-to-timerange.md](docs/adr/0009-sport-timeslot-to-timerange.md) —
контекст, решение, 5 рассмотренных альтернатив, последствия.

**Changelog:** [CHANGELOG.md](CHANGELOG.md) — версия 1.9.0 с разделами Added, Fixed,
Deprecated, Breaking Changes; будущая фаза Contract — в `[Unreleased]` как Breaking Change.

## Как запустить

```bash
# Visual Studio: открыть homework-5/SmartCampus.Sport.sln, F5
# Без строки подключения сервис работает с хранилищем в памяти.
dotnet run --project src/SmartCampus.Sport.Api

# модульные тесты
dotnet test --filter Category=Unit

# production sanity после релиза
set SANITY_BASE_URL=https://<адрес сервиса>
set SANITY_TOKEN=<секрет>
dotnet test --filter Category=Sanity

# миграции (PostgreSQL)
psql -d campus -f db/migrations/01_expand.sql
psql -d campus -f db/migrations/02_migrate.sql
psql -d campus -f db/migrations/03_contract.sql   # отдельным CR
```

## Выводы

Изменение формата слота бронирования спорткомплекса выполнено без простоя и с
сохранением возможности отката: фаза Expand добавляет новую структуру рядом со
старой, сервис v1.9.0 пишет в обе, исторические данные переносятся пакетами,
а удаление старой колонки вынесено в отдельный Change Request. Каждый запрос
оставляет цифровой след (`jira.issue=CAMPUS-110` и идентификатор брони в трассе),
который связывает код, коммит, CR, ADR и запись CHANGELOG в единую цепочку — это
и есть практическая реализация CALS-подхода на этапе сопровождения. Production
sanity-тест подтверждает работоспособность боевого сценария сразу после релиза и
не оставляет тестовых данных.
