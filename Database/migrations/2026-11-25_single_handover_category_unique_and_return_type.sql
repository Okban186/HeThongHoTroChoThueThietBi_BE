-- 1. Chặn tạo nhiều phiếu bàn giao gốc đang hoạt động cho cùng 1 hợp đồng (chỉ giao trọn gói 1 phiếu)
CREATE UNIQUE INDEX IF NOT EXISTS uq_handover_one_active_initial_per_contract
ON handover_notes (rental_contract_id)
WHERE replacement_request_id IS NULL
  AND status NOT IN ('CANCELLED');

-- 2. Chặn trùng tên danh mục thiết bị (không phân biệt hoa thường, khoảng trắng đầu/cuối)
CREATE UNIQUE INDEX IF NOT EXISTS uq_categories_name_lower
ON categories (LOWER(TRIM(name)));

-- 3. Phân biệt phiếu nhận trả thường (NORMAL) và phiếu thu hồi do hủy giao giữa chừng (CANCELLED_SHIPMENT)
ALTER TABLE equipment_returns
ADD COLUMN IF NOT EXISTS return_type VARCHAR(30) NOT NULL DEFAULT 'NORMAL';

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'chk_equipment_returns_return_type'
    ) THEN
        ALTER TABLE equipment_returns
        ADD CONSTRAINT chk_equipment_returns_return_type
        CHECK (return_type IN ('NORMAL', 'CANCELLED_SHIPMENT'));
    END IF;
END $$;
