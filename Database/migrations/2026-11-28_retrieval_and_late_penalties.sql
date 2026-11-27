-- ============================================================
-- Cụm 3: Luồng chuyến thu hồi cuối kỳ/hết hạn/hủy HĐ (RETRIEVAL)
--        trên equipment_returns (gắn service_assignment_id + GPS + thứ tự chặng).
-- ============================================================

BEGIN;

ALTER TABLE equipment_returns
ADD COLUMN IF NOT EXISTS service_assignment_id UUID NULL REFERENCES service_assignments(id);

ALTER TABLE equipment_returns
ADD COLUMN IF NOT EXISTS stop_order INTEGER NOT NULL DEFAULT 1;

ALTER TABLE equipment_returns
ADD COLUMN IF NOT EXISTS pickup_latitude DOUBLE PRECISION NULL;

ALTER TABLE equipment_returns
ADD COLUMN IF NOT EXISTS pickup_longitude DOUBLE PRECISION NULL;

ALTER TABLE equipment_returns
ADD COLUMN IF NOT EXISTS estimated_travel_minutes INTEGER NOT NULL DEFAULT 0;

ALTER TABLE equipment_returns
ADD COLUMN IF NOT EXISTS estimated_uninstall_minutes INTEGER NOT NULL DEFAULT 0;

CREATE INDEX IF NOT EXISTS ix_equipment_returns_service_assignment
ON equipment_returns (service_assignment_id);

COMMIT;
