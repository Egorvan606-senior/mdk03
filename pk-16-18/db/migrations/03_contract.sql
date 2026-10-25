-- =====================================================================
-- CAMPUS-110 · Фаза 3 — CONTRACT (сжатие схемы)
-- Выполняется отдельным Change Request, НЕ раньше чем:
--   * все экземпляры sport-service обновлены до v1.6.x и переключены
--     на чтение из time_range (BookingSchema:ReadFromTimeRange = true);
--   * отключена запись в старую колонку (BookingSchema:WriteLegacyTimeSlot = false);
--   * отчёты и интеграции (BI, турникеты зала) больше не читают time_slot;
--   * журнал ошибок фазы 2 разобран (0 строк без time_range).
-- После этой фазы откат на v1.5.x невозможен (Breaking Change).
-- =====================================================================

SET lock_timeout = '3s';

-- 0. Предусловия: если что-то не так — скрипт прерывается, схема не меняется
DO $$
DECLARE
    v_not_migrated bigint;
    v_overlaps     bigint;
BEGIN
    SELECT count(*) INTO v_not_migrated
    FROM sport.bookings WHERE time_range IS NULL;

    IF v_not_migrated > 0 THEN
        RAISE EXCEPTION 'CAMPUS-110 contract отменён: % строк без time_range (см. sport.bookings_migration_errors)',
            v_not_migrated;
    END IF;

    SELECT count(*) INTO v_overlaps
    FROM sport.bookings a
    JOIN sport.bookings b
      ON a.facility_id = b.facility_id
     AND a.booking_date = b.booking_date
     AND a.id < b.id
     AND a.status = 'Active' AND b.status = 'Active'
     AND a.time_range && b.time_range;

    IF v_overlaps > 0 THEN
        RAISE EXCEPTION 'CAMPUS-110 contract отменён: найдено % пересекающихся активных броней', v_overlaps;
    END IF;
END $$;

-- 1. NOT NULL без длительной блокировки:
--    CHECK ... NOT VALID ставится мгновенно, VALIDATE сканирует таблицу под
--    мягкой блокировкой (чтение и запись продолжают работать),
--    а SET NOT NULL (PostgreSQL 12+) использует проверенный CHECK и не сканирует заново.
ALTER TABLE sport.bookings
    ADD CONSTRAINT ck_bookings_time_range_not_null CHECK (time_range IS NOT NULL) NOT VALID;
ALTER TABLE sport.bookings
    VALIDATE CONSTRAINT ck_bookings_time_range_not_null;

BEGIN;

ALTER TABLE sport.bookings ALTER COLUMN time_range SET NOT NULL;
ALTER TABLE sport.bookings DROP CONSTRAINT ck_bookings_time_range_not_null;

-- 2. Убираем механизмы переходного периода
DROP TRIGGER IF EXISTS bookings_sync_time_range ON sport.bookings;
DROP FUNCTION IF EXISTS sport.trg_bookings_sync_time_range();

-- 3. Удаляем старую структуру
ALTER TABLE sport.bookings DROP COLUMN IF EXISTS time_slot;
DROP FUNCTION IF EXISTS sport.format_time_slot(sport.timerange);
-- parse_time_slot оставляем: используется при импорте старых выгрузок

-- 4. Бизнес-правило, ради которого всё делалось: БД сама запрещает
--    пересекающиеся активные брони одного зала в один день.
CREATE EXTENSION IF NOT EXISTS btree_gist;

ALTER TABLE sport.bookings
    ADD CONSTRAINT ck_bookings_time_range_valid
        CHECK (NOT isempty(time_range) AND lower_inc(time_range) AND NOT upper_inc(time_range));

ALTER TABLE sport.bookings
    ADD CONSTRAINT ex_bookings_no_overlap
        EXCLUDE USING gist (facility_id WITH =, booking_date WITH =, time_range WITH &&)
        WHERE (status = 'Active');

COMMENT ON COLUMN sport.bookings.time_range IS 'Слот бронирования [начало, конец); без пересечений для активных броней';

COMMIT;

INSERT INTO public.schema_changelog (jira_issue, phase, script)
VALUES ('CAMPUS-110', 'contract', '03_contract.sql');
