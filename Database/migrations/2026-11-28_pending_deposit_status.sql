-- Bổ sung trạng thái PENDING_DEPOSIT (Chờ đặt cọc sau khi Manager duyệt bản PDF đã ký)
-- vào CHECK constraint của rental_contracts và contract_status_history.

BEGIN;

DO $$
DECLARE
    con_name TEXT;
BEGIN
    FOR con_name IN
        SELECT c.conname
        FROM pg_constraint c
        JOIN pg_class t ON t.oid = c.conrelid
        WHERE t.relname = 'rental_contracts'
          AND c.contype = 'c'
          AND pg_get_constraintdef(c.oid) ILIKE '%PENDING_SIGNATURE%'
    LOOP
        EXECUTE format('ALTER TABLE rental_contracts DROP CONSTRAINT %I', con_name);
    END LOOP;
END $$;

ALTER TABLE rental_contracts
ADD CONSTRAINT chk_rental_contracts_status
CHECK (status IN (
    'DRAFT',
    'PENDING_APPROVAL',
    'REJECTED',
    'PENDING_SIGNATURE',
    'SIGNED',
    'PENDING_DEPOSIT',
    'PREPARING',
    'PARTIALLY_DELIVERED',
    'DELIVERED',
    'COMPLETED',
    'EXPIRED',
    'CANCELLATION_REQUESTED',
    'CANCELLED'
));

DO $$
DECLARE
    con_name TEXT;
BEGIN
    FOR con_name IN
        SELECT c.conname
        FROM pg_constraint c
        JOIN pg_class t ON t.oid = c.conrelid
        WHERE t.relname = 'contract_status_history'
          AND c.contype = 'c'
          AND pg_get_constraintdef(c.oid) ILIKE '%PENDING_SIGNATURE%'
    LOOP
        EXECUTE format('ALTER TABLE contract_status_history DROP CONSTRAINT %I', con_name);
    END LOOP;
END $$;

ALTER TABLE contract_status_history
ADD CONSTRAINT chk_contract_status_history_status
CHECK (status IN (
    'DRAFT',
    'PENDING_APPROVAL',
    'REJECTED',
    'PENDING_SIGNATURE',
    'SIGNED',
    'PENDING_DEPOSIT',
    'PREPARING',
    'PARTIALLY_DELIVERED',
    'DELIVERED',
    'COMPLETED',
    'EXPIRED',
    'CANCELLATION_REQUESTED',
    'CANCELLED'
));

ALTER TABLE contract_status_history
ADD CONSTRAINT chk_contract_status_history_previous_status
CHECK (previous_status IN (
    'DRAFT',
    'PENDING_APPROVAL',
    'REJECTED',
    'PENDING_SIGNATURE',
    'SIGNED',
    'PENDING_DEPOSIT',
    'PREPARING',
    'PARTIALLY_DELIVERED',
    'DELIVERED',
    'COMPLETED',
    'EXPIRED',
    'CANCELLATION_REQUESTED',
    'CANCELLED'
));

COMMIT;
