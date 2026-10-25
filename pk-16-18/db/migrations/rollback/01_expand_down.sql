-- =====================================================================
-- CAMPUS-110 · Откат фазы EXPAND (используется ТОЛЬКО если откатывается
-- сам Change Request целиком, до запуска 03_contract.sql).
-- Для отката приложения (kubectl rollout undo) этот скрипт НЕ нужен:
-- v1.5.x не знает о колонке time_range и продолжает работать с time_slot.
-- После 03_contract.sql откат невозможен — только восстановление из бэкапа.
-- =====================================================================
SET lock_timeout = '3s';

BEGIN;
DROP TRIGGER IF EXISTS bookings_sync_time_range ON sport.bookings;
DROP FUNCTION IF EXISTS sport.trg_bookings_sync_time_range();
DROP PROCEDURE IF EXISTS sport.backfill_time_range(integer);
ALTER TABLE sport.bookings DROP COLUMN IF EXISTS time_range;
DROP TABLE IF EXISTS sport.bookings_migration_errors;
DROP FUNCTION IF EXISTS sport.format_time_slot(sport.timerange);
DROP FUNCTION IF EXISTS sport.parse_time_slot(text);
DROP TYPE IF EXISTS sport.timerange;
COMMENT ON COLUMN sport.bookings.time_slot IS NULL;
COMMIT;

INSERT INTO public.schema_changelog (jira_issue, phase, script)
VALUES ('CAMPUS-110', 'rollback', '01_expand_down.sql');
