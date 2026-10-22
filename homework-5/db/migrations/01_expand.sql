-- =====================================================================
-- CAMPUS-110 · Фаза 1 — EXPAND (расширение схемы)
-- Модуль: Спортивный комплекс. TimeSlot (string) -> TimeRange.
--
-- Что делает скрипт:
--   1) создаёт диапазонный тип sport.timerange (диапазон по типу time);
--   2) добавляет НОВУЮ nullable-колонку time_range рядом со старой time_slot;
--   3) создаёт функцию разбора старой строки в диапазон;
--   4) вешает триггер совместимости: если запись пришла от СТАРОЙ версии
--      сервиса (пишет только time_slot), новая колонка заполнится сама.
--
-- Почему без простоя:
--   * ADD COLUMN без DEFAULT — изменение только метаданных, таблица
--     не переписывается, блокировка берётся на миллисекунды;
--   * старая колонка не трогается — версия v1.8.x продолжает работать;
--   * lock_timeout не даёт миграции «повиснуть» в очереди блокировок
--     и заблокировать пользовательские запросы (при таймауте — повтор).
-- Скрипт идемпотентен: его можно безопасно запустить повторно.
-- =====================================================================

SET lock_timeout = '3s';
SET statement_timeout = '60s';

BEGIN;

-- 1. Диапазонный тип: интервал времени [начало, конец)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1
                   FROM pg_type t
                   JOIN pg_namespace n ON n.oid = t.typnamespace
                   WHERE n.nspname = 'sport' AND t.typname = 'timerange') THEN
        CREATE TYPE sport.timerange AS RANGE (subtype = time);
    END IF;
END $$;

-- 2. Новая колонка (NULL допустим до окончания фазы 2)
ALTER TABLE sport.bookings
    ADD COLUMN IF NOT EXISTS time_range sport.timerange NULL;

COMMENT ON COLUMN sport.bookings.time_slot  IS 'DEPRECATED (CAMPUS-110): заменена на time_range, удаляется в 03_contract.sql';
COMMENT ON COLUMN sport.bookings.time_range IS 'CAMPUS-110: слот бронирования, диапазон [начало, конец)';

-- 3. Разбор старого формата: '18:00-19:30', '18.00 - 19.30', '9:00–10:00'.
--    Для некорректных значений возвращает NULL (не роняет запись).
CREATE OR REPLACE FUNCTION sport.parse_time_slot(p_slot text)
RETURNS sport.timerange
LANGUAGE plpgsql
IMMUTABLE
AS $$
DECLARE
    m       text[];
    v_start time;
    v_end   time;
BEGIN
    IF p_slot IS NULL THEN
        RETURN NULL;
    END IF;

    m := regexp_match(p_slot, '^\s*(\d{1,2})[:.](\d{2})\s*[-–—]\s*(\d{1,2})[:.](\d{2})\s*$');
    IF m IS NULL THEN
        RETURN NULL;
    END IF;

    IF m[1]::int > 23 OR m[3]::int > 24 OR m[2]::int > 59 OR m[4]::int > 59
       OR (m[3]::int = 24 AND m[4]::int <> 0) THEN
        RETURN NULL;
    END IF;

    v_start := make_time(m[1]::int, m[2]::int, 0);
    v_end   := CASE WHEN m[3]::int = 24 THEN time '24:00'
                    ELSE make_time(m[3]::int, m[4]::int, 0) END;

    IF v_end <= v_start THEN
        RETURN NULL;
    END IF;

    RETURN sport.timerange(v_start, v_end, '[)');
END $$;

-- Обратное преобразование — нужно триггеру, если новый клиент
-- прислал только time_range (старые отчёты читают time_slot).
CREATE OR REPLACE FUNCTION sport.format_time_slot(p_range sport.timerange)
RETURNS varchar
LANGUAGE sql
IMMUTABLE
AS $$
    SELECT CASE WHEN p_range IS NULL THEN NULL
                ELSE to_char(lower(p_range), 'HH24:MI') || '-' || to_char(upper(p_range), 'HH24:MI')
           END
$$;

-- 4. Триггер совместимости на время переходного периода
CREATE OR REPLACE FUNCTION sport.trg_bookings_sync_time_range()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        -- старый клиент (v1.8.x) пишет только строку
        IF NEW.time_range IS NULL AND NEW.time_slot IS NOT NULL THEN
            NEW.time_range := sport.parse_time_slot(NEW.time_slot);
        -- новый клиент без строки
        ELSIF NEW.time_slot IS NULL AND NEW.time_range IS NOT NULL THEN
            NEW.time_slot := sport.format_time_slot(NEW.time_range);
        END IF;
    ELSE -- UPDATE
        IF NEW.time_slot IS DISTINCT FROM OLD.time_slot
           AND NEW.time_range IS NOT DISTINCT FROM OLD.time_range THEN
            NEW.time_range := sport.parse_time_slot(NEW.time_slot);
        ELSIF NEW.time_range IS DISTINCT FROM OLD.time_range
           AND NEW.time_slot IS NOT DISTINCT FROM OLD.time_slot THEN
            NEW.time_slot := sport.format_time_slot(NEW.time_range);
        END IF;
    END IF;
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS bookings_sync_time_range ON sport.bookings;
CREATE TRIGGER bookings_sync_time_range
    BEFORE INSERT OR UPDATE OF time_slot, time_range ON sport.bookings
    FOR EACH ROW
    EXECUTE FUNCTION sport.trg_bookings_sync_time_range();

-- Журнал записей, которые не удалось перенести в фазе 2
CREATE TABLE IF NOT EXISTS sport.bookings_migration_errors
(
    booking_id  uuid        PRIMARY KEY,
    time_slot   varchar(20),
    reason      text        NOT NULL,
    logged_at   timestamptz NOT NULL DEFAULT now()
);

COMMIT;

-- Версия схемы (для аудита и CALS-следа)
CREATE TABLE IF NOT EXISTS public.schema_changelog
(
    id          bigserial   PRIMARY KEY,
    jira_issue  varchar(16) NOT NULL,
    phase       varchar(16) NOT NULL,
    script      varchar(64) NOT NULL,
    applied_at  timestamptz NOT NULL DEFAULT now()
);
INSERT INTO public.schema_changelog (jira_issue, phase, script)
VALUES ('CAMPUS-110', 'expand', '01_expand.sql');
