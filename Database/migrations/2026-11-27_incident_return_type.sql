-- Mở rộng return_type cho equipment_returns để hỗ trợ thu hồi máy gặp sự cố tại hiện trường (INCIDENT_RETURN)
ALTER TABLE equipment_returns
DROP CONSTRAINT IF EXISTS chk_equipment_returns_return_type;

ALTER TABLE equipment_returns
ADD CONSTRAINT chk_equipment_returns_return_type
CHECK (return_type IN ('NORMAL', 'CANCELLED_SHIPMENT', 'DEFECTIVE_ON_DELIVERY', 'INCIDENT_RETURN'));
