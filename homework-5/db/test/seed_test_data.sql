-- тестовые исторические данные: 5000 броней, включая «грязные» форматы
INSERT INTO sport.bookings (facility_id, student_id, booking_date, time_slot, created_at)
SELECT (g % 12) + 1,
       'ST' || lpad((100000 + g)::text, 6, '0'),
       date '2026-09-01' + (g / 60),
       CASE
         WHEN g % 250 = 0 THEN 'весь день'            -- мусор
         WHEN g % 333 = 0 THEN '19:00-18:00'          -- конец раньше начала
         WHEN g % 7 = 0   THEN lpad((7 + (g / 12) % 5 * 3)::text, 2, '0') || '.00 - ' || lpad((8 + (g / 12) % 5 * 3)::text, 2, '0') || '.30'
         ELSE (7 + (g / 12) % 5 * 3)::text || ':00-' || (8 + (g / 12) % 5 * 3)::text || ':30'
       END,
       now() - interval '30 days'
FROM generate_series(1, 5000) g;
