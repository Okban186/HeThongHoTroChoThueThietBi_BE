-- ============================================================
-- Đồng bộ toàn bộ cột giữa PostgreSQL <-> Entity (.cs) <-> AppDbContext.cs
-- An toàn chạy lại nhiều lần (IF NOT EXISTS) trong 1 transaction.
-- ============================================================

BEGIN;

-- 1. warehouses: tọa độ GPS kho
ALTER TABLE warehouses
    ADD COLUMN IF NOT EXISTS latitude DOUBLE PRECISION NULL,
    ADD COLUMN IF NOT EXISTS longitude DOUBLE PRECISION NULL;

-- 2. handover_notes: lộ trình giao, ghi chú duyệt, phân loại phiếu & sự cố liên kết
ALTER TABLE handover_notes
    ALTER COLUMN handover_type TYPE VARCHAR(30),
    ADD COLUMN IF NOT EXISTS stop_order INTEGER NOT NULL DEFAULT 1,
    ADD COLUMN IF NOT EXISTS delivery_latitude DOUBLE PRECISION NULL,
    ADD COLUMN IF NOT EXISTS delivery_longitude DOUBLE PRECISION NULL,
    ADD COLUMN IF NOT EXISTS estimated_travel_minutes INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS estimated_install_minutes INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS approval_note TEXT NULL,
    ADD COLUMN IF NOT EXISTS assigned_storekeeper_id UUID NULL REFERENCES users(id),
    ADD COLUMN IF NOT EXISTS incident_id UUID NULL REFERENCES equipment_incidents(id);

-- 3. equipment_returns: loại thu hồi, Thủ kho phụ trách & chuyến thu hồi RETRIEVAL
ALTER TABLE equipment_returns
    ADD COLUMN IF NOT EXISTS return_type VARCHAR(30) NOT NULL DEFAULT 'NORMAL',
    ADD COLUMN IF NOT EXISTS assigned_storekeeper_id UUID NULL REFERENCES users(id),
    ADD COLUMN IF NOT EXISTS service_assignment_id UUID NULL REFERENCES service_assignments(id),
    ADD COLUMN IF NOT EXISTS stop_order INTEGER NOT NULL DEFAULT 1,
    ADD COLUMN IF NOT EXISTS pickup_latitude DOUBLE PRECISION NULL,
    ADD COLUMN IF NOT EXISTS pickup_longitude DOUBLE PRECISION NULL,
    ADD COLUMN IF NOT EXISTS estimated_travel_minutes INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS estimated_uninstall_minutes INTEGER NOT NULL DEFAULT 0;

-- 4. equipment_incidents: thông tin người báo & phân công KTV
ALTER TABLE equipment_incidents
    ADD COLUMN IF NOT EXISTS reported_by UUID NULL REFERENCES users(id),
    ADD COLUMN IF NOT EXISTS contact_name VARCHAR(150) NULL,
    ADD COLUMN IF NOT EXISTS contact_phone VARCHAR(20) NULL,
    ADD COLUMN IF NOT EXISTS remote_technician_staff_id UUID NULL REFERENCES staff(id),
    ADD COLUMN IF NOT EXISTS service_assignment_id UUID NULL REFERENCES service_assignments(id);

-- 5. equipment_incident_items: nguyên nhân lỗi & phiếu giao thay thế
ALTER TABLE equipment_incident_items
    ADD COLUMN IF NOT EXISTS fault_type VARCHAR(30) NULL,
    ADD COLUMN IF NOT EXISTS replacement_handover_note_id UUID NULL REFERENCES handover_notes(id);

-- 6. incident_charges: quy trình duyệt & cấn trừ cọc
ALTER TABLE incident_charges
    ALTER COLUMN status TYPE VARCHAR(30),
    ADD COLUMN IF NOT EXISTS proposed_by UUID NULL REFERENCES users(id),
    ADD COLUMN IF NOT EXISTS approved_by UUID NULL REFERENCES users(id),
    ADD COLUMN IF NOT EXISTS approved_at TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS settlement_method VARCHAR(30) NULL,
    ADD COLUMN IF NOT EXISTS deposit_offset_amount NUMERIC(18,2) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS manager_note TEXT NULL;

-- 7. maintenance_tasks: KTV tiếp nhận (claim)
ALTER TABLE maintenance_tasks
    ADD COLUMN IF NOT EXISTS assigned_technician_staff_id UUID NULL REFERENCES staff(id);

-- 8. equipment_disposals & replacement_request_items
ALTER TABLE equipment_disposals
    ADD COLUMN IF NOT EXISTS requested_by UUID NULL REFERENCES users(id),
    ADD COLUMN IF NOT EXISTS status VARCHAR(20) NOT NULL DEFAULT 'PENDING',
    ADD COLUMN IF NOT EXISTS approved_at TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS rejection_reason TEXT NULL;

ALTER TABLE replacement_request_items
    ADD COLUMN IF NOT EXISTS replacement_model_id UUID NULL REFERENCES equipment_models(id);

COMMIT;
