-- 1. Tách bạch người phụ trách (tiếp nhận) giữa các Thủ kho trên phiếu bàn giao và phiếu thu hồi:
--    NULL = chưa có Thủ kho nào tiếp nhận (tất cả Thủ kho đều thấy để nhận)
--    NOT NULL = đã có Thủ kho tiếp nhận (chỉ Thủ kho đó thấy và xử lý, Thủ kho khác không thấy)
ALTER TABLE handover_notes
ADD COLUMN IF NOT EXISTS assigned_storekeeper_id UUID NULL REFERENCES users(id);

CREATE INDEX IF NOT EXISTS ix_handover_notes_assigned_storekeeper
ON handover_notes (assigned_storekeeper_id);

ALTER TABLE equipment_returns
ADD COLUMN IF NOT EXISTS assigned_storekeeper_id UUID NULL REFERENCES users(id);

CREATE INDEX IF NOT EXISTS ix_equipment_returns_assigned_storekeeper
ON equipment_returns (assigned_storekeeper_id);

-- Backfill nhẹ cho dữ liệu cũ:
-- - Phiếu bàn giao đã có thiết bị và do Thủ kho tạo thì gán luôn người tạo làm người tiếp nhận.
UPDATE handover_notes hn
SET assigned_storekeeper_id = hn.created_by
WHERE hn.assigned_storekeeper_id IS NULL
  AND EXISTS (SELECT 1 FROM handover_items hi WHERE hi.handover_note_id = hn.id)
  AND EXISTS (
      SELECT 1
      FROM user_roles ur
      JOIN roles r ON r.id = ur.role_id
      WHERE ur.user_id = hn.created_by
        AND r.code IN ('STOREKEEPER', 'LEAD_STOREKEEPER')
  );

-- - Phiếu thu hồi đã hoàn tất (COMPLETED) gán người nhận làm người tiếp nhận; phiếu PROCESSING giữ NULL để Thủ kho vào tiếp nhận.
UPDATE equipment_returns er
SET assigned_storekeeper_id = er.received_by
WHERE er.assigned_storekeeper_id IS NULL
  AND er.status = 'COMPLETED'
  AND EXISTS (
      SELECT 1
      FROM user_roles ur
      JOIN roles r ON r.id = ur.role_id
      WHERE ur.user_id = er.received_by
        AND r.code IN ('STOREKEEPER', 'LEAD_STOREKEEPER')
  );
