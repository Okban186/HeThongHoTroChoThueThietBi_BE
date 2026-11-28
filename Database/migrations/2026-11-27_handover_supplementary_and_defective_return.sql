-- 1. Phân loại phiếu bàn giao: INITIAL (phiếu gốc) và SUPPLEMENTARY (phiếu giao bù máy lỗi)
ALTER TABLE handover_notes
ADD COLUMN IF NOT EXISTS handover_type VARCHAR(20) NOT NULL DEFAULT 'INITIAL';

ALTER TABLE handover_notes
ADD COLUMN IF NOT EXISTS parent_handover_id UUID NULL REFERENCES handover_notes(id);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'chk_handover_notes_handover_type'
    ) THEN
        ALTER TABLE handover_notes
        ADD CONSTRAINT chk_handover_notes_handover_type
        CHECK (handover_type IN ('INITIAL', 'SUPPLEMENTARY'));
    END IF;
END $$;

-- 2. Cập nhật Partial Unique Index:
--    - Tối đa 1 phiếu INITIAL chưa hủy / hợp đồng
--    - Tối đa 1 phiếu SUPPLEMENTARY đang mở (chưa COMPLETED / CANCELLED) / hợp đồng
DROP INDEX IF EXISTS uq_handover_one_active_initial_per_contract;

CREATE UNIQUE INDEX IF NOT EXISTS uq_handover_one_active_initial_per_contract
ON handover_notes (rental_contract_id)
WHERE handover_type = 'INITIAL'
  AND status NOT IN ('CANCELLED');

CREATE UNIQUE INDEX IF NOT EXISTS uq_handover_one_open_supplementary_per_contract
ON handover_notes (rental_contract_id)
WHERE handover_type = 'SUPPLEMENTARY'
  AND status NOT IN ('COMPLETED', 'CANCELLED');

-- 3. Mở rộng return_type cho equipment_returns để hỗ trợ thu hồi máy lỗi khi giao (DEFECTIVE_ON_DELIVERY)
ALTER TABLE equipment_returns
DROP CONSTRAINT IF EXISTS chk_equipment_returns_return_type;

ALTER TABLE equipment_returns
ADD CONSTRAINT chk_equipment_returns_return_type
CHECK (return_type IN ('NORMAL', 'CANCELLED_SHIPMENT', 'DEFECTIVE_ON_DELIVERY'));
