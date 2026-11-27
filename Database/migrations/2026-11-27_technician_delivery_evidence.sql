-- 1. Cho phép ảnh sự cố lúc bàn giao gắn chung cho cả đợt lỗi (handover_issue_item_id IS NULL)
--    hoặc gắn riêng cho từng máy lỗi cụ thể (handover_issue_item_id IS NOT NULL)
ALTER TABLE handover_issue_attachments
ADD COLUMN IF NOT EXISTS handover_issue_item_id UUID NULL REFERENCES handover_issue_items(id);

CREATE INDEX IF NOT EXISTS ix_handover_issue_attachments_item_id
ON handover_issue_attachments (handover_issue_item_id)
WHERE handover_issue_item_id IS NOT NULL;
