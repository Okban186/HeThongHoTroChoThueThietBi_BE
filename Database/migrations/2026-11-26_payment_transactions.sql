-- ============================================================
-- Bổ sung bảng payment_transactions phục vụ thanh toán:
--   - VNPAY Sandbox (QR + IPN, thời hạn 15 phút, chống ghi trùng theo txn_ref)
--   - Tiền mặt (CASH: chụp phiếu thu chờ Manager duyệt)
-- Áp dụng cho: Tiền đặt cọc (DEPOSIT), Đợt tiền thuê (RENTAL_INSTALLMENT),
--              Khoản phạt/phí phát sinh (CHARGE), Phí sự cố (INCIDENT_CHARGE).
--
-- Script có thể chạy lại nhiều lần (IF NOT EXISTS) trong 1 transaction.
-- ============================================================

BEGIN;

CREATE TABLE IF NOT EXISTS payment_transactions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    rental_contract_id UUID NOT NULL REFERENCES rental_contracts(id),
    rental_charge_id UUID NULL REFERENCES rental_charges(id),
    incident_charge_id UUID NULL REFERENCES incident_charges(id),
    target_type VARCHAR(30) NOT NULL
        CHECK (target_type IN ('DEPOSIT', 'RENTAL_INSTALLMENT', 'CHARGE', 'INCIDENT_CHARGE')),
    payment_method VARCHAR(20) NOT NULL
        CHECK (payment_method IN ('VNPAY', 'CASH')),
    txn_ref VARCHAR(100) NOT NULL UNIQUE,
    amount NUMERIC(18,2) NOT NULL CHECK (amount > 0),
    order_info TEXT NOT NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'PENDING'
        CHECK (status IN ('PENDING', 'VERIFIED', 'FAILED', 'CANCELLED', 'EXPIRED')),
    payment_url TEXT NULL,
    expires_at TIMESTAMPTZ NULL,
    vnp_transaction_no VARCHAR(100) NULL,
    vnp_bank_code VARCHAR(50) NULL,
    vnp_response_code VARCHAR(20) NULL,
    vnp_pay_date VARCHAR(20) NULL,
    receipt_file_id UUID NULL REFERENCES storage_files(id),
    created_by UUID NOT NULL REFERENCES users(id),
    verified_by UUID NULL REFERENCES users(id),
    verified_at TIMESTAMPTZ NULL,
    rejected_reason TEXT NULL,
    note TEXT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_payment_transactions_contract_id
    ON payment_transactions(rental_contract_id);

CREATE INDEX IF NOT EXISTS idx_payment_transactions_rental_charge_id
    ON payment_transactions(rental_charge_id);

CREATE INDEX IF NOT EXISTS idx_payment_transactions_incident_charge_id
    ON payment_transactions(incident_charge_id);

CREATE INDEX IF NOT EXISTS idx_payment_transactions_status
    ON payment_transactions(status);

COMMIT;
