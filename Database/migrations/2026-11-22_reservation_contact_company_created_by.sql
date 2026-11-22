-- ============================================================
-- Cập nhật database ĐANG CHẠY cho khớp với equipment_rental_database.sql
--   1. reservations: thêm contact_name, contact_phone, expires_at
--   2. companies:    thêm created_by (Sales tạo công ty, cho phép NULL)
--
-- Database tạo mới từ equipment_rental_database.sql đã có sẵn các cột này,
-- KHÔNG cần chạy file này.
--
-- Script có thể chạy lại nhiều lần (IF NOT EXISTS) và chạy trong 1 transaction.
-- ============================================================

BEGIN;

-- ---------- 1. reservations ----------
ALTER TABLE reservations
    ADD COLUMN IF NOT EXISTS contact_name  VARCHAR(150),
    ADD COLUMN IF NOT EXISTS contact_phone VARCHAR(20),
    ADD COLUMN IF NOT EXISTS expires_at    TIMESTAMPTZ;

-- Gán giá trị cho các phiếu đã có trước khi đặt NOT NULL.
UPDATE reservations
SET contact_name = 'Chưa cập nhật'
WHERE contact_name IS NULL;

UPDATE reservations
SET contact_phone = ''
WHERE contact_phone IS NULL;

-- Quy tắc hạn giữ chỗ: min(created_at + 3 ngày, requested_from).
UPDATE reservations
SET expires_at = LEAST(created_at + INTERVAL '3 days', requested_from::timestamptz)
WHERE expires_at IS NULL;

ALTER TABLE reservations
    ALTER COLUMN contact_name  SET NOT NULL,
    ALTER COLUMN contact_phone SET NOT NULL,
    ALTER COLUMN expires_at    SET NOT NULL;

-- ---------- 2. companies ----------
ALTER TABLE companies
    ADD COLUMN IF NOT EXISTS created_by UUID REFERENCES users(id);

CREATE INDEX IF NOT EXISTS idx_companies_created_by
    ON companies(created_by);

COMMIT;
