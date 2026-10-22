-- =====================================================================
-- ИС «Умный кампус», модуль «Спортивный комплекс»
-- 00_baseline.sql — исходная схема ДО изменений по задаче CAMPUS-110
-- (нужна только для локального стенда и CI: на проде таблица уже есть).
--
-- Слот бронирования хранится строкой в колонке time_slot (TimeSlot),
-- например '18:00-19:30'. Строку нельзя проверить на пересечения,
-- нельзя посчитать длительность, в неё попадают значения вида
-- '18.00 - 19.30', '9:00-10:00' и мусор.
-- =====================================================================

CREATE SCHEMA IF NOT EXISTS sport;

CREATE TABLE IF NOT EXISTS sport.bookings
(
    id            uuid         PRIMARY KEY DEFAULT gen_random_uuid(),
    facility_id   integer      NOT NULL,                  -- зал / корт / бассейн
    student_id    varchar(32)  NOT NULL,                  -- номер студенческого билета
    booking_date  date         NOT NULL,
    time_slot     varchar(20)  NOT NULL,                  -- TimeSlot: 'HH:MM-HH:MM'
    status        varchar(16)  NOT NULL DEFAULT 'Active', -- Active | Cancelled
    created_by    varchar(64)  NOT NULL DEFAULT 'app',
    created_at    timestamptz  NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_bookings_facility_date
    ON sport.bookings (facility_id, booking_date);
