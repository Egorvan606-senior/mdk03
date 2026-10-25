# Runbook RB-001. Алерт `DatabaseConnectionPoolExhausted`

**Сервис:** `sport-service` v1.6.0 (модуль «Спортивный комплекс») · **Кому:** дежурный инженер (SRE / L2)
**Условие алерта:** занято > 95 % соединений пула Npgsql (`Maximum Pool Size = 100`) дольше 2 минут
**Симптом для пользователей:** запись на тренировку «зависает», API отвечает 500 / 504, в логе `The connection pool has been exhausted`

## Шаг 1. Диагностика (1–2 минуты)

```bash
# 1. Сколько подов и не перезапускаются ли они
kubectl get pods -n production -l app=sport-service

# 2. Ошибки пула в логах за 10 минут
kubectl logs -n production deploy/sport-service --since=10m | grep -i "connection pool"

# 3. Кто держит соединения в PostgreSQL (DBA / доступ только на чтение)
psql -h db.campus.local -U sre_readonly -d campus -c \
  "SELECT state, count(*) FROM pg_stat_activity WHERE application_name LIKE 'sport-service%' GROUP BY state;"
```

* Много соединений в состоянии `idle in transaction` / долгие запросы — «залип» фоновый воркер → **Шаг 2**.
* PostgreSQL недоступен целиком — это инцидент БД → **сразу Шаг 3 (эскалация DBA)**.

## Шаг 2. Действие — перезапуск воркера

Перезапуск подов сервиса освобождает все соединения пула (RollingUpdate — без простоя):

```bash
kubectl rollout restart deployment/sport-service -n production
kubectl rollout status  deployment/sport-service -n production --timeout=5m
```

Через 2 минуты проверьте: алерт погас, sanity-проверка записи на слот проходит
(`dotnet test --filter Category=Sanity`), доля 5xx < 1 %.

## Шаг 3. Критерий перехода к откату

**Откатывайте релиз, если выполняется ЛЮБОЕ условие:**

1. алерт повторился **в течение 15 минут** после перезапуска;
2. доля ответов **5xx > 2 % дольше 5 минут** после перезапуска;
3. проблема началась **в течение 24 часов после выкатки v1.6.0** (вероятная причина — новый код dual write).

```bash
kubectl rollout undo   deployment/sport-service -n production
kubectl rollout status deployment/sport-service -n production
```

Откат БД не нужен: фаза Expand обратно совместима, v1.5.3 работает со старой колонкой `time_slot`.
После отката — сообщение в `#campus-ops`, инцидент в Service Desk, разбор (Post-Mortem) в течение 2 рабочих дней.

## Эскалация

Если через 10 минут после шагов 2–3 сервис не восстановлен — дежурный DBA и Tech Lead модуля в `#emergency-sre`.
Приложите: время начала, вывод команд шага 1, TraceId одного неуспешного запроса.
