-- ============================================================
-- Cụm 2: Tách bạch KTV tự tiếp nhận (Claim) phiếu bảo trì tại kho
--   NULL     = chưa có KTV nhận (hiển thị cho toàn bộ đội KTV)
--   NOT NULL = đã có KTV tiếp nhận hoặc Manager chỉ định (chỉ KTV đó thấy & xử lý)
-- ============================================================

BEGIN;

ALTER TABLE maintenance_tasks
ADD COLUMN IF NOT EXISTS assigned_technician_staff_id UUID NULL REFERENCES staff(id);

CREATE INDEX IF NOT EXISTS ix_maintenance_tasks_assigned_technician
ON maintenance_tasks (assigned_technician_staff_id);

-- Backfill nhẹ cho các phiếu đã hoàn tất trước đây theo người thực hiện trong maintenance_records
UPDATE maintenance_tasks mt
SET assigned_technician_staff_id = (
    SELECT mr.performed_by
    FROM maintenance_records mr
    WHERE mr.maintenance_task_id = mt.id
    ORDER BY mr.completed_at DESC NULLS LAST
    LIMIT 1
)
WHERE mt.assigned_technician_staff_id IS NULL
  AND mt.status = 'COMPLETED'
  AND EXISTS (
      SELECT 1 FROM maintenance_records mr WHERE mr.maintenance_task_id = mt.id
  );

COMMIT;
