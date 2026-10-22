#!/usr/bin/env bash
# Сквозная проверка миграций CAMPUS-110 на чистой БД PostgreSQL.
# Используется в CI (job db-migrations) и локально:
#   PGHOST=localhost PGUSER=postgres PGPASSWORD=postgres ./run_migration_test.sh
set -euo pipefail
cd "$(dirname "$0")/.."
DB=${PGDATABASE:-campus_ci}
psql_db() { psql -v ON_ERROR_STOP=1 -X -q -d "$DB" "$@"; }

echo "== 1. Чистая БД и исходная схема"
dropdb --if-exists "$DB"; createdb "$DB"
psql_db -f migrations/00_baseline.sql
psql_db -f test/seed_test_data.sql

echo "== 2. Expand (два прогона — проверка идемпотентности)"
psql_db -f migrations/01_expand.sql 2>/dev/null
psql_db -f migrations/01_expand.sql 2>/dev/null

echo "== 3. Старый клиент пишет только time_slot -> триггер заполняет time_range"
r=$(psql_db -tA -c "insert into sport.bookings(facility_id,student_id,booking_date,time_slot) values (5,'CI-1','2026-12-15','18:00-19:30') returning time_range" | head -1)
[ "$r" = "[18:00:00,19:30:00)" ] || { echo "FAIL: триггер вернул '$r'"; exit 1; }
echo "   time_slot '18:00-19:30' -> time_range $r"

echo "== 4. Откат expand и повторное применение"
psql_db -f migrations/rollback/01_expand_down.sql
psql_db -f migrations/01_expand.sql 2>/dev/null

echo "== 5. Migrate"
psql_db -f migrations/02_migrate.sql > /dev/null 2>&1
nm=$(psql_db -tA -c "select count(*) from sport.bookings where time_range is null")
er=$(psql_db -tA -c "select count(*) from sport.bookings_migration_errors")
[ "$nm" = "$er" ] || { echo "FAIL: без time_range $nm строк, в журнале ошибок $er"; exit 1; }
echo "   перенесено: $(psql_db -tA -c "select count(*) from sport.bookings where time_range is not null"), в журнале ошибок: $er"

echo "== 6. Contract должен ОТКАЗАТЬСЯ работать при неразобранных ошибках"
if psql_db -f migrations/03_contract.sql 2>/dev/null; then echo "FAIL: contract прошёл с ошибками"; exit 1; fi
echo "   contract отменён предусловием — OK"

echo "== 7. Исправление данных и Contract"
psql_db -c "update sport.bookings b set time_slot='21:00-22:30' from sport.bookings_migration_errors e where e.booking_id=b.id; delete from sport.bookings_migration_errors;" > /dev/null
psql_db -f migrations/03_contract.sql
col=$(psql_db -tA -c "select count(*) from information_schema.columns where table_schema='sport' and table_name='bookings' and column_name='time_slot'")
[ "$col" = "0" ] || { echo "FAIL: колонка time_slot не удалена"; exit 1; }
echo "   колонка time_slot удалена, time_range NOT NULL"

echo "== 8. Запрет пересечений после Contract"
if psql_db -c "insert into sport.bookings(facility_id,student_id,booking_date,time_range) values (5,'CI-2','2026-12-15','[18:30,19:00)')" 2>/dev/null; then
  echo "FAIL: пересекающаяся бронь вставлена"; exit 1; fi
echo "   пересекающаяся бронь отклонена (ex_bookings_no_overlap) — OK"

echo "OK: все проверки миграций CAMPUS-110 пройдены"
