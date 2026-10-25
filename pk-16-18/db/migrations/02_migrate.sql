-- =====================================================================
-- CAMPUS-110 · Фаза 2 — MIGRATE (перенос исторических данных)
-- Запускается ПОСЛЕ выкатки v1.6.0, когда все экземпляры сервиса уже
-- пишут в обе колонки (dual write), а для старых клиентов работает триггер.
--
-- Принципы:
--   * перенос пачками по 1 000 строк с COMMIT после каждой пачки —
--     короткие транзакции, нет долгих блокировок, реплики не отстают;
--   * строки, которые не разбираются, не ломают миграцию, а попадают
--     в sport.bookings_migration_errors для ручного разбора;
--   * скрипт можно прервать и запустить заново — продолжит с места остановки.
-- ВАЖНО: CALL с COMMIT внутри нельзя выполнять внутри BEGIN ... COMMIT.
-- =====================================================================

SET lock_timeout = '3s';

CREATE OR REPLACE PROCEDURE sport.backfill_time_range(p_batch_size integer DEFAULT 1000)
LANGUAGE plpgsql
AS $$
DECLARE
    v_ids       uuid[];
    v_batch     integer := 0;
    v_updated   bigint  := 0;
    v_failed    bigint  := 0;
    v_rows      integer;
BEGIN
    LOOP
        -- следующая пачка необработанных строк (ошибочные пропускаем)
        SELECT array_agg(id)
        INTO v_ids
        FROM (
            SELECT b.id
            FROM sport.bookings b
            WHERE b.time_range IS NULL
              AND NOT EXISTS (SELECT 1 FROM sport.bookings_migration_errors e
                              WHERE e.booking_id = b.id)
            ORDER BY b.id
            LIMIT p_batch_size
            FOR UPDATE SKIP LOCKED      -- не ждём строки, которые сейчас правит приложение
        ) s;

        EXIT WHEN v_ids IS NULL;

        UPDATE sport.bookings
        SET time_range = sport.parse_time_slot(time_slot)
        WHERE id = ANY (v_ids)
          AND time_range IS NULL;
        GET DIAGNOSTICS v_rows = ROW_COUNT;

        -- то, что не разобралось, — в журнал ошибок
        INSERT INTO sport.bookings_migration_errors (booking_id, time_slot, reason)
        SELECT id, time_slot, 'Некорректный формат TimeSlot'
        FROM sport.bookings
        WHERE id = ANY (v_ids) AND time_range IS NULL
        ON CONFLICT (booking_id) DO NOTHING;

        v_failed  := v_failed + (SELECT count(*) FROM sport.bookings
                                 WHERE id = ANY (v_ids) AND time_range IS NULL);
        v_updated := v_updated + v_rows;
        v_batch   := v_batch + 1;

        COMMIT;   -- фиксируем пачку, освобождаем блокировки

        RAISE NOTICE 'CAMPUS-110 backfill: пачка %, обработано %, перенесено всего %, ошибок %',
            v_batch, coalesce(array_length(v_ids, 1), 0), v_updated - v_failed, v_failed;

        PERFORM pg_sleep(0.05);  -- даём «подышать» рабочей нагрузке
    END LOOP;

    RAISE NOTICE 'CAMPUS-110 backfill завершён: пачек %, ошибок %', v_batch, v_failed;
END $$;

CALL sport.backfill_time_range(1000);

-- ---------------------------------------------------------------------
-- Контроль результата (выполняется после CALL)
-- ---------------------------------------------------------------------

-- 1) Сколько строк ещё без time_range (должно быть 0 или = числу ошибок)
SELECT count(*) FILTER (WHERE time_range IS NULL)     AS not_migrated,
       count(*) FILTER (WHERE time_range IS NOT NULL) AS migrated,
       count(*)                                       AS total
FROM sport.bookings;

-- 2) Сверка: строка и диапазон описывают одно и то же время
SELECT count(*) AS mismatches
FROM sport.bookings
WHERE time_range IS NOT NULL
  AND sport.parse_time_slot(time_slot) IS DISTINCT FROM time_range;

-- 3) Записи для ручного разбора (исправить time_slot -> триггер обновит time_range)
SELECT booking_id, time_slot, reason, logged_at
FROM sport.bookings_migration_errors
ORDER BY logged_at;

INSERT INTO public.schema_changelog (jira_issue, phase, script)
VALUES ('CAMPUS-110', 'migrate', '02_migrate.sql');
