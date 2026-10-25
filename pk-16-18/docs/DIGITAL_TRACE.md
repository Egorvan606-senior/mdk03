# Блок 1. Цифровой след (Digital Trace) задачи CAMPUS-110

Модуль «Спортивный комплекс» ИС «Умный кампус», вариант 10.
Задача Jira: **CAMPUS-110** — логирование записи в бассейн/зал, изменение формата `TimeSlot` (string → `TimeRange`).

## 1. Сквозная цепочка CALS

```text
[ Jira: CAMPUS-110 ] ➔ [ Ветка: feature/CAMPUS-110-timerange ] ➔ [ Коммит: [CAMPUS-110] Add tracing tags and business logic ]
        ➔ [ Pull Request в main (2 коллеги + Tech Lead, xunit-tests) ] ➔ [ Образ sport-service:1.6.0 ] ➔ [ Trace Tag: jira.issue = CAMPUS-110 ]
```

| Звено | Где смотреть | Значение |
|---|---|---|
| Бизнес-требование | Jira | `CAMPUS-110` — запись на слот тренировки хранится диапазоном, каждое действие логируется |
| Ветка | Git | `feature/CAMPUS-110-timerange` |
| Коммит | Git | `[CAMPUS-110] Add tracing tags and business logic` (формат проверяется Ruleset `^\[CAMPUS-\d+\] .+`) |
| Контейнер | реестр образов | `registry.campus.local/sport-service:1.6.0`, атрибут ресурса `campus.release.jira=CAMPUS-110` |
| Трасса | Jaeger | теги `jira.issue=CAMPUS-110`, `campus.entity_id=<id брони>`, `campus.facility_id`, `campus.time_range` |

## 2. Код: проброс тегов в Activity (C#)

`src/SmartCampus.Sport.Api/Telemetry/CampusTelemetry.cs` и `Controllers/SportBookingsController.cs`:

```csharp
// span входящего HTTP-запроса, созданный инструментацией ASP.NET Core
Activity.Current?.SetTag("jira.issue", "CAMPUS-110");
Activity.Current?.SetTag("campus.entity_id", booking.Id.ToString());

// дочерний span бизнес-операции — видно время именно логики бронирования
using var operation = CampusTelemetry.Source.StartActivity("sport.booking.create");
operation?.SetTag("jira.issue", "CAMPUS-110");
operation?.SetTag("campus.entity_id", booking.Id.ToString());
```

Наличие тегов проверяет автотест `DigitalTraceTests.BookingSpan_HasJiraAndEntityTags`.

## 3. Как связать лог в консоли контейнера с графом вызовов в Jaeger (traceparent)

1. Клиент (или API-шлюз) передаёт заголовок W3C **`traceparent`**:

   ```text
   traceparent: 00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01
                 │  └──────── trace-id (32 hex) ────────┘ └ span-id (16) ┘ └ флаги (01 = sampled)
                 версия
   ```

   Если заголовка нет, ASP.NET Core создаёт новый trace-id сам.
2. Инструментация OpenTelemetry продолжает эту трассу: все span'ы запроса (HTTP, бизнес-операция,
   SQL) получают **тот же trace-id**, и вся цепочка экспортируется по OTLP (порт 4317) в Jaeger.
3. Логгер сервиса пишет trace-id в каждую строку лога (scope логирования `IncludeScopes = true`):

   ```text
   02:25:14 info: SmartCampus.Sport.Controllers.SportBookingsController[0]
         => SpanId:00f067aa0ba902b7, TraceId:4bf92f3577b34da6a3ce929d0e0e4736, ParentId:... => ...
         [CAMPUS-110] Создана бронь 5c1e..., зал 999, 2026-11-24, 22:00-22:30, trace 4bf92f3577b34da6a3ce929d0e0e4736
   ```

   Этот же trace-id API возвращает клиенту в заголовке ответа **`X-Trace-Id`**.
4. Дежурный берёт trace-id из строки лога (`kubectl logs deploy/sport-service -n production | grep CAMPUS-110`)
   или из жалобы пользователя (`X-Trace-Id`) и открывает в Jaeger UI (порт 16686):
   **Search → поле Trace ID** или адрес `http://jaeger:16686/trace/4bf92f3577b34da6a3ce929d0e0e4736`.
   Jaeger показывает граф (waterfall) этого запроса: сколько миллисекунд заняли HTTP-обработка,
   `sport.booking.create` и SQL-запрос, и на каком шаге возникла ошибка.
5. Обратный поиск — от задачи к запросам: Jaeger → Service `smart-campus-sport` → Tags `jira.issue=CAMPUS-110`
   — все запросы, прошедшие через изменённый по задаче код.

## 4. Результат: зафиксированная цепочка

| Jira | Git Commit | OpenTelemetry Trace Tag |
|---|---|---|
| **CAMPUS-110** | `[CAMPUS-110] Add tracing tags and business logic` (ветка `feature/CAMPUS-110-timerange`) | `jira.issue = CAMPUS-110`, `campus.entity_id = <id брони>`; trace-id в логе = `X-Trace-Id` = Trace ID в Jaeger |
