-- ============================================================
-- Cụm 1: Phân định trách nhiệm sự cố, duyệt phạt / cấn trừ cọc,
-- và phiếu bàn giao đổi/thay thế thiết bị sự cố (INCIDENT_REPLACEMENT).
-- ============================================================

BEGIN;

-- 1. Mở rộng equipment_incident_items: nguyên nhân lỗi (TECHNICAL_FAULT | CUSTOMER_FAULT)
--    và liên kết phiếu bàn giao thay thế.
ALTER TABLE equipment_incident_items
ADD COLUMN IF NOT EXISTS fault_type VARCHAR(30) NULL;

ALTER TABLE equipment_incident_items
ADD COLUMN IF NOT EXISTS replacement_handover_note_id UUID NULL REFERENCES handover_notes(id);

ALTER TABLE equipment_incident_items
DROP CONSTRAINT IF EXISTS chk_equipment_incident_items_fault_type;

ALTER TABLE equipment_incident_items
ADD CONSTRAINT chk_equipment_incident_items_fault_type
CHECK (fault_type IS NULL OR fault_type IN ('TECHNICAL_FAULT', 'CUSTOMER_FAULT'));

-- 2. Mở rộng incident_charges: thêm trạng thái PENDING_APPROVAL (KTV đề xuất chờ Manager duyệt)
--    và thông tin duyệt/cấn trừ cọc.
ALTER TABLE incident_charges
ALTER COLUMN status TYPE VARCHAR(30);

DO $$
DECLARE
    con_name TEXT;
BEGIN
    FOR con_name IN
        SELECT c.conname
        FROM pg_constraint c
        JOIN pg_class t ON t.oid = c.conrelid
        WHERE t.relname = 'incident_charges'
          AND c.contype = 'c'
          AND pg_get_constraintdef(c.oid) ILIKE '%WAIVED%'
    LOOP
        EXECUTE format('ALTER TABLE incident_charges DROP CONSTRAINT %I', con_name);
    END LOOP;
END $$;

ALTER TABLE incident_charges
ADD CONSTRAINT chk_incident_charges_status
CHECK (status IN ('PENDING_APPROVAL', 'PENDING', 'CHARGED', 'WAIVED', 'CANCELLED'));

ALTER TABLE incident_charges
ADD COLUMN IF NOT EXISTS proposed_by UUID NULL REFERENCES users(id);

ALTER TABLE incident_charges
ADD COLUMN IF NOT EXISTS approved_by UUID NULL REFERENCES users(id);

ALTER TABLE incident_charges
ADD COLUMN IF NOT EXISTS approved_at TIMESTAMPTZ NULL;

ALTER TABLE incident_charges
ADD COLUMN IF NOT EXISTS settlement_method VARCHAR(30) NULL;

ALTER TABLE incident_charges
ADD COLUMN IF NOT EXISTS deposit_offset_amount NUMERIC(18,2) NOT NULL DEFAULT 0;

ALTER TABLE incident_charges
ADD COLUMN IF NOT EXISTS manager_note TEXT NULL;

ALTER TABLE incident_charges
DROP CONSTRAINT IF EXISTS chk_incident_charges_settlement_method;

ALTER TABLE incident_charges
ADD CONSTRAINT chk_incident_charges_settlement_method
CHECK (settlement_method IS NULL OR settlement_method IN ('OFFSET_DEPOSIT', 'DIRECT_BILLING', 'WAIVED'));

-- 3. Mở rộng handover_notes: thêm handover_type = 'INCIDENT_REPLACEMENT' và cột incident_id
ALTER TABLE handover_notes
ALTER COLUMN handover_type TYPE VARCHAR(30);

ALTER TABLE handover_notes
ADD COLUMN IF NOT EXISTS incident_id UUID NULL REFERENCES equipment_incidents(id);

ALTER TABLE handover_notes
DROP CONSTRAINT IF EXISTS chk_handover_notes_handover_type;

ALTER TABLE handover_notes
ADD CONSTRAINT chk_handover_notes_handover_type
CHECK (handover_type IN ('INITIAL', 'SUPPLEMENTARY', 'INCIDENT_REPLACEMENT'));

CREATE UNIQUE INDEX IF NOT EXISTS uq_handover_one_open_incident_replacement_per_incident
ON handover_notes (incident_id)
WHERE handover_type = 'INCIDENT_REPLACEMENT'
  AND incident_id IS NOT NULL
  AND status NOT IN ('COMPLETED', 'CANCELLED');

COMMIT;
