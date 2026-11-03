-- ============================================================
-- DATABASE: Equipment Rental Management System
-- PostgreSQL
-- Final schema - course project scope
-- ============================================================
CREATE DATABASE QL_ThietBiChoThue;
CREATE EXTENSION IF NOT EXISTS pgcrypto;

-- ============================================================
-- 1. USERS / ROLES / STAFF / COMPANIES
-- ============================================================
CREATE TABLE storage_files (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    object_key TEXT NOT NULL UNIQUE,
    bucket_name VARCHAR(100) NOT NULL,
    original_name VARCHAR(255) NOT NULL,
    content_type VARCHAR(150),
    size_bytes BIGINT NOT NULL
        CHECK (size_bytes >= 0),
    metadata JSONB,
    uploaded_by UUID REFERENCES users(id),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE TABLE users (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    username VARCHAR(100) NOT NULL UNIQUE,
    password_hash TEXT NOT NULL,
    full_name VARCHAR(150) NOT NULL,
    avatar_storage_file_id UUID NULL,
    email VARCHAR(255),
    phone VARCHAR(20),
    status VARCHAR(20) NOT NULL DEFAULT 'ACTIVE'
        CHECK (status IN ('ACTIVE', 'INACTIVE', 'LOCKED')),
    last_login_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT fk_users_avatar_storage_file
    FOREIGN KEY (avatar_storage_file_id)
    REFERENCES storage_files(id)
    ON DELETE SET NULL;
);

CREATE TABLE roles (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code VARCHAR(50) NOT NULL UNIQUE,
    name VARCHAR(100) NOT NULL,
    description TEXT
);

CREATE TABLE user_roles (
    user_id UUID NOT NULL REFERENCES users(id),
    role_id UUID NOT NULL REFERENCES roles(id),
    assigned_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (user_id, role_id)
);

CREATE TABLE staff (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id UUID NOT NULL UNIQUE REFERENCES users(id),
    employee_code VARCHAR(50) NOT NULL UNIQUE,
    position VARCHAR(100),
    hire_date DATE,
    status VARCHAR(20) NOT NULL DEFAULT 'ACTIVE'
        CHECK (status IN ('ACTIVE', 'INACTIVE')),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE companies (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code VARCHAR(50) NOT NULL UNIQUE,
    name VARCHAR(255) NOT NULL,
    tax_code VARCHAR(50) NOT NULL UNIQUE,
    address TEXT,
    status VARCHAR(20) NOT NULL DEFAULT 'ACTIVE'
        CHECK (status IN ('ACTIVE', 'INACTIVE')),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE company_users (
    user_id UUID PRIMARY KEY REFERENCES users(id),
    company_id UUID NOT NULL REFERENCES companies(id),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE notifications (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id UUID NOT NULL REFERENCES users(id),
    title VARCHAR(255) NOT NULL,
    content TEXT NOT NULL,
    type VARCHAR(50),
    is_read BOOLEAN NOT NULL DEFAULT FALSE,
    read_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ============================================================
-- FILE STORAGE
-- ============================================================



-- ============================================================
-- 2. WAREHOUSE / CATEGORY / EQUIPMENT
-- ============================================================

CREATE TABLE warehouses (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code VARCHAR(50) NOT NULL UNIQUE,
    name VARCHAR(150) NOT NULL,
    address TEXT,
    phone VARCHAR(20),
    status VARCHAR(20) NOT NULL DEFAULT 'ACTIVE'
        CHECK (status IN ('ACTIVE', 'INACTIVE')),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE categories (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    parent_id UUID REFERENCES categories(id),
    code VARCHAR(50) NOT NULL UNIQUE,
    name VARCHAR(150) NOT NULL,
    description TEXT,
    status VARCHAR(20) NOT NULL DEFAULT 'ACTIVE'
        CHECK (status IN ('ACTIVE', 'INACTIVE')),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE equipment_models (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code VARCHAR(50) NOT NULL UNIQUE,
    name VARCHAR(255) NOT NULL,
    manufacturer VARCHAR(150),
    model_number VARCHAR(100),
    description TEXT,
    estimated_installation_minutes INTEGER NOT NULL DEFAULT 0,
    specifications JSONB,
    unit VARCHAR(30) NOT NULL DEFAULT 'UNIT',
    rental_price_daily NUMERIC(18,2) NOT NULL DEFAULT 0
        CHECK (rental_price_daily >= 0),
    rental_price_monthly NUMERIC(18,2) NOT NULL DEFAULT 0
        CHECK (rental_price_monthly >= 0),
    status VARCHAR(20) NOT NULL DEFAULT 'ACTIVE'
        CHECK (status IN ('ACTIVE', 'INACTIVE')),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE TABLE equipment_model_media (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    equipment_model_id UUID NOT NULL REFERENCES equipment_models(id) ON DELETE CASCADE,
    storage_file_id UUID NOT NULL REFERENCES storage_files(id) ON DELETE CASCADE,
    media_type VARCHAR(20) NOT NULL DEFAULT 'IMAGE' 
        CHECK (media_type IN ('IMAGE', 'VIDEO')),
    display_order INTEGER NOT NULL DEFAULT 1 
        CHECK (display_order > 0),
    is_primary BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (equipment_model_id, storage_file_id)
);

CREATE TABLE equipment_model_categories (
    equipment_model_id UUID NOT NULL REFERENCES equipment_models(id),
    category_id UUID NOT NULL REFERENCES categories(id),
    PRIMARY KEY (equipment_model_id, category_id)
);

CREATE TABLE equipments (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    equipment_model_id UUID NOT NULL REFERENCES equipment_models(id),
    warehouse_id UUID NOT NULL REFERENCES warehouses(id),
    serial_number VARCHAR(100) NOT NULL UNIQUE,
    status VARCHAR(20) NOT NULL DEFAULT 'AVAILABLE'
        CHECK (status IN (
            'AVAILABLE',
            'RESERVED',
            'RENTED',
            'MAINTENANCE',
            'DAMAGED',
            'LOST',
            'DISPOSED'
        )),
    condition VARCHAR(20) NOT NULL DEFAULT 'GOOD'
        CHECK (condition IN ('GOOD', 'FAIR', 'DAMAGED')),
    acquired_at DATE,
    acquisition_cost NUMERIC(18,2)
        CHECK (acquisition_cost IS NULL OR acquisition_cost >= 0),
    note TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ============================================================
-- 3. RESERVATIONS
-- ============================================================

CREATE TABLE reservations (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    reservation_code VARCHAR(50) NOT NULL UNIQUE,
    company_id UUID NOT NULL REFERENCES companies(id),
    requested_from DATE NOT NULL,
    requested_to DATE NOT NULL,
    note TEXT,
    status VARCHAR(20) NOT NULL DEFAULT 'PENDING'
        CHECK (status IN (
            'PENDING',
            'APPROVED',
            'REJECTED',
            'CANCELLED',
            'EXPIRED',
            'CONVERTED'
        )),
    created_by UUID NOT NULL REFERENCES users(id),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CHECK (requested_to >= requested_from)
);

CREATE TABLE reservation_items (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    reservation_id UUID NOT NULL REFERENCES reservations(id),
    equipment_model_id UUID NOT NULL REFERENCES equipment_models(id),
    quantity INTEGER NOT NULL CHECK (quantity > 0),
    note TEXT
);

-- ============================================================
-- 4. RENTAL CONTRACTS / ITEMS / SERVICES
-- A rental contract is the central record for the rental lifecycle.
-- ============================================================

CREATE TABLE rental_contracts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    contract_code VARCHAR(50) NOT NULL UNIQUE,
    reservation_id UUID UNIQUE REFERENCES reservations(id),
    company_id UUID NOT NULL REFERENCES companies(id),
    start_date DATE NOT NULL,
    end_date DATE NOT NULL,
    rental_type VARCHAR(20) NOT NULL
        CHECK (rental_type IN ('SHORT_TERM', 'LONG_TERM')),
    delivery_address TEXT,
    delivery_note TEXT,
    -- Agreed total recorded on the signed contract; deposit is commonly 20% but may vary by agreement.
    contract_total_amount NUMERIC(18,2) NOT NULL DEFAULT 0
        CHECK (contract_total_amount >= 0),
    -- Deposit is tracked separately from rent and other charges.
    deposit_required_amount NUMERIC(18,2) NOT NULL DEFAULT 0
        CHECK (deposit_required_amount >= 0),
    deposit_paid_amount NUMERIC(18,2) NOT NULL DEFAULT 0
        CHECK (deposit_paid_amount >= 0),
    deposit_applied_amount NUMERIC(18,2) NOT NULL DEFAULT 0
        CHECK (deposit_applied_amount >= 0),
    deposit_refunded_amount NUMERIC(18,2) NOT NULL DEFAULT 0
        CHECK (deposit_refunded_amount >= 0),
    contract_file_id UUID REFERENCES storage_files(id),
    signed_at TIMESTAMPTZ,
    effective_from DATE,
    effective_to DATE,
    status VARCHAR(30) NOT NULL DEFAULT 'DRAFT'
        CHECK (status IN (
            'DRAFT',
            'PENDING_APPROVAL',
            'REJECTED',
            'PENDING_SIGNATURE',
            'SIGNED',
            'PREPARING',
            'PARTIALLY_DELIVERED',
            'DELIVERED',
            'COMPLETED',
            'EXPIRED',
            'CANCELLATION_REQUESTED',
            'CANCELLED'
        )),
    created_by UUID NOT NULL REFERENCES users(id),
    approved_by UUID REFERENCES users(id),
    approved_at TIMESTAMPTZ,
    rejection_reason TEXT,
    cancellation_reason TEXT,
    cancellation_requested_at TIMESTAMPTZ,
    cancellation_requested_by UUID REFERENCES users(id),
    cancelled_at TIMESTAMPTZ,
    cancelled_by UUID REFERENCES users(id),
    note TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CHECK (end_date >= start_date),
    CHECK (deposit_applied_amount + deposit_refunded_amount <= deposit_paid_amount),
    CHECK (
        effective_to IS NULL
        OR effective_from IS NULL
        OR effective_to >= effective_from
    )
);

CREATE TABLE rental_contract_items (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    rental_contract_id UUID NOT NULL REFERENCES rental_contracts(id),
    equipment_model_id UUID NOT NULL REFERENCES equipment_models(id),
    quantity INTEGER NOT NULL CHECK (quantity > 0),
    billing_unit VARCHAR(20) NOT NULL
        CHECK (billing_unit IN ('DAILY', 'MONTHLY')),
    agreed_unit_price NUMERIC(18,2) NOT NULL
        CHECK (agreed_unit_price >= 0),
    note TEXT
);

CREATE TABLE rental_contract_services (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    rental_contract_id UUID NOT NULL REFERENCES rental_contracts(id),
    service_type VARCHAR(30) NOT NULL
        CHECK (service_type IN ('DELIVERY', 'LOADING', 'UNLOADING', 'OTHER')),
    provided_by VARCHAR(30) NOT NULL
        CHECK (provided_by IN ('RENTAL_COMPANY', 'CUSTOMER')),
    quantity INTEGER NOT NULL DEFAULT 1 CHECK (quantity > 0),
    unit_price NUMERIC(18,2) NOT NULL DEFAULT 0 CHECK (unit_price >= 0),
    amount NUMERIC(18,2) NOT NULL DEFAULT 0 CHECK (amount >= 0),
    note TEXT
);


CREATE TABLE contract_status_history (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    rental_contract_id UUID NOT NULL REFERENCES rental_contracts(id),
    status VARCHAR(30) NOT NULL,
    previous_status VARCHAR(30) NOT NULL,
    changed_by UUID NOT NULL REFERENCES users(id),
    changed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    note TEXT
);

-- ============================================================
-- 5. SERVICE ASSIGNMENTS
-- ============================================================

CREATE TABLE service_assignments (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    type VARCHAR(30) NOT NULL
        CHECK (type IN ('HANDOVER', 'INCIDENT', 'MAINTENANCE','RETRIEVAL')),
    title VARCHAR(255) NOT NULL,
    description TEXT,
    status VARCHAR(20) NOT NULL DEFAULT 'PENDING'
        CHECK (status IN (
            'PENDING',
            'IN_PROGRESS',
            'COMPLETED',
            'CANCELLED'
        )),
    assigned_at TIMESTAMPTZ,
    scheduled_start_at TIMESTAMPTZ,
    estimated_duration_minutes INTEGER,
    started_at TIMESTAMPTZ,
    completed_at TIMESTAMPTZ,
    created_by UUID NOT NULL REFERENCES users(id),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE service_assignment_members (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    service_assignment_id UUID NOT NULL REFERENCES service_assignments(id),
    staff_id UUID NOT NULL REFERENCES staff(id),
    role VARCHAR(20) NOT NULL
        CHECK (role IN ('LEADER', 'MEMBER')),
    UNIQUE (service_assignment_id, staff_id)
);

-- ============================================================
-- 6. HANDOVER
-- ============================================================

CREATE TABLE handover_notes (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    handover_code VARCHAR(50) NOT NULL UNIQUE,
    rental_contract_id UUID NOT NULL REFERENCES rental_contracts(id),
    service_assignment_id UUID REFERENCES service_assignments(id),
    handover_date DATE NOT NULL,
    replacement_request_id UUID,
    delivery_attempt_number INTEGER NOT NULL DEFAULT 1
        CHECK (delivery_attempt_number > 0),
    failed_reason TEXT,
    failed_at TIMESTAMPTZ,
    received_by_name VARCHAR(150) NOT NULL,
    received_by_phone VARCHAR(20),
    note TEXT,
    status VARCHAR(20) NOT NULL DEFAULT 'DRAFT'
        CHECK (status IN (
            'DRAFT',             
    'PREPARING',       
    'PENDING_APPROVAL',  
    'SHIPPING',         
    'COMPLETED',    
    'CANCELLED'  
        )),
    created_by UUID NOT NULL REFERENCES users(id),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE handover_items (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    handover_note_id UUID NOT NULL REFERENCES handover_notes(id),
    equipment_id UUID NOT NULL REFERENCES equipments(id),
    note TEXT,
    UNIQUE (handover_note_id, equipment_id)
);

CREATE TABLE handover_issues (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    handover_note_id UUID NOT NULL REFERENCES handover_notes(id),
    description TEXT NOT NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'OPEN'
        CHECK (status IN ('OPEN', 'RESOLVED', 'CANCELLED')),
    created_by UUID NOT NULL REFERENCES users(id),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    resolved_at TIMESTAMPTZ
);

CREATE TABLE handover_issue_items (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    handover_issue_id UUID NOT NULL REFERENCES handover_issues(id),
    equipment_id UUID NOT NULL REFERENCES equipments(id),
    issue_type VARCHAR(30) NOT NULL
        CHECK (issue_type IN ('DAMAGED', 'MALFUNCTION', 'OTHER')),
    description TEXT,
    status VARCHAR(20) NOT NULL DEFAULT 'OPEN'
        CHECK (status IN ('OPEN', 'RESOLVED', 'CANCELLED'))
);

-- ============================================================
-- 7. RETURNS / CONTRACT CHARGES
-- ============================================================

CREATE TABLE equipment_returns (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    return_code VARCHAR(50) NOT NULL UNIQUE,
    rental_contract_id UUID NOT NULL REFERENCES rental_contracts(id),
    returned_at TIMESTAMPTZ NOT NULL,
    received_by UUID NOT NULL REFERENCES users(id),
    status VARCHAR(20) NOT NULL DEFAULT 'DRAFT'
        CHECK (status IN (
            'DRAFT',
            'PROCESSING',
            'COMPLETED',
            'CANCELLED'
        )),
    note TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE equipment_return_items (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    equipment_return_id UUID NOT NULL REFERENCES equipment_returns(id),
    equipment_id UUID NOT NULL REFERENCES equipments(id),
    status VARCHAR(20) NOT NULL DEFAULT 'RETURNED'
        CHECK (status IN ('RETURNED', 'MISSING')),
    note TEXT,
    UNIQUE (equipment_return_id, equipment_id)
);

CREATE TABLE rental_charges (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- Nguồn phát sinh khoản thu: hợp đồng, sự cố hoặc khoản thu khác.
    source_type VARCHAR(20) NOT NULL DEFAULT 'CONTRACT'
        CHECK (source_type IN ('CONTRACT', 'INCIDENT', 'OTHER')),

    -- Có thể NULL nếu khoản thu không gắn trực tiếp với hợp đồng.
    rental_contract_id UUID REFERENCES rental_contracts(id),
    equipment_id UUID REFERENCES equipments(id),

    -- RENTAL dùng cho tiền thuê; các loại còn lại là phí phát sinh.
    charge_type VARCHAR(30) NOT NULL
        CHECK (charge_type IN (
            'RENTAL',
            'LATE_RETURN',
            'EARLY_CANCELLATION',
            'DAMAGE',
            'LOSS',
            'TRANSPORT',
            'LATE_PAYMENT',
            'OTHER'
        )),

    description TEXT,
    quantity NUMERIC(18,2) NOT NULL DEFAULT 1
        CHECK (quantity > 0),
    unit_price NUMERIC(18,2) NOT NULL DEFAULT 0
        CHECK (unit_price >= 0),
    amount NUMERIC(18,2) NOT NULL DEFAULT 0
        CHECK (amount >= 0),

    -- Với SHORT_TERM: một khoản RENTAL bao trùm toàn bộ thời gian thuê.
    -- Với LONG_TERM: mỗi khoản RENTAL tương ứng một kỳ thuê/tháng.
    period_start DATE,
    period_end DATE,
    due_date DATE,

    paid_amount NUMERIC(18,2) NOT NULL DEFAULT 0
        CHECK (paid_amount >= 0),
    status VARCHAR(20) NOT NULL DEFAULT 'UNPAID'
        CHECK (status IN (
            'UNPAID',
            'PARTIALLY_PAID',
            'PAID',
            'WAIVED',
            'CANCELLED'
        )),
    created_by UUID NOT NULL REFERENCES users(id),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    paid_at TIMESTAMPTZ,
    note TEXT,

    CHECK (paid_amount <= amount),
    CHECK (period_end IS NULL OR period_start IS NULL OR period_end >= period_start),
    CHECK (source_type <> 'CONTRACT' OR rental_contract_id IS NOT NULL)
);

-- ============================================================
-- 8. INCIDENTS
-- ============================================================

CREATE TABLE equipment_incidents (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    incident_code VARCHAR(50) NOT NULL UNIQUE,
    company_id UUID NOT NULL REFERENCES companies(id),
    description TEXT NOT NULL,
    reported_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    resolved_at TIMESTAMPTZ,
    status VARCHAR(30) NOT NULL DEFAULT 'OPEN'
        CHECK (status IN (
            'OPEN',
            'REMOTE_SUPPORT',
            'DISPATCHING',
            'ON_SITE',
            'RETURN_REQUIRED',
            'RESOLVED',
            'CANCELLED'
        )),
    resolution TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE equipment_incident_items (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    incident_id UUID NOT NULL REFERENCES equipment_incidents(id),
    equipment_id UUID NOT NULL REFERENCES equipments(id),
    description TEXT,
    assessment TEXT,
    
    note TEXT
);

CREATE TABLE incident_charges (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    incident_item_id UUID NOT NULL REFERENCES equipment_incident_items(id),
    charge_type VARCHAR(20) NOT NULL
        CHECK (charge_type IN ('REPAIR', 'REPLACEMENT', 'OTHER')),
    description TEXT,
    amount NUMERIC(18,2) NOT NULL DEFAULT 0
        CHECK (amount >= 0),
    status VARCHAR(20) NOT NULL DEFAULT 'PENDING'
        CHECK (status IN ('PENDING', 'CHARGED', 'WAIVED', 'CANCELLED')),
    rental_charge_id UUID REFERENCES rental_charges(id),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ============================================================
-- 9. REPLACEMENT
-- ============================================================

CREATE TABLE replacement_requests (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    request_code VARCHAR(50) NOT NULL UNIQUE,
    incident_id UUID REFERENCES equipment_incidents(id),
    rental_contract_id UUID NOT NULL REFERENCES rental_contracts(id),
    handover_id UUID REFERENCES handover_notes(id),
    reason TEXT NOT NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'PENDING'
        CHECK (status IN (
            'PENDING',
            'APPROVED',
            'REJECTED',
            'IN_PROGRESS',
            'COMPLETED',
            'CANCELLED'
        )),
    requested_by UUID NOT NULL REFERENCES users(id),
    approved_by UUID REFERENCES users(id),
    approved_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

ALTER TABLE handover_notes
    ADD CONSTRAINT fk_handover_notes_replacement_request
    FOREIGN KEY (replacement_request_id) REFERENCES replacement_requests(id);

CREATE TABLE replacement_request_items (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    replacement_request_id UUID NOT NULL
        REFERENCES replacement_requests(id) ON DELETE CASCADE,

    old_equipment_id UUID NOT NULL
        REFERENCES equipments(id),

    note TEXT,

    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    UNIQUE (replacement_request_id, old_equipment_id)
);

-- ============================================================
-- 10. MAINTENANCE / DISPOSAL
-- ============================================================

CREATE TABLE maintenance_tasks (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    equipment_id UUID NOT NULL REFERENCES equipments(id),
    service_assignment_id UUID REFERENCES service_assignments(id),
    type VARCHAR(20) NOT NULL
        CHECK (type IN ('PREVENTIVE', 'CORRECTIVE', 'INSPECTION')),
    description TEXT NOT NULL,
    scheduled_at TIMESTAMPTZ,
    started_at TIMESTAMPTZ,
    completed_at TIMESTAMPTZ,
    status VARCHAR(20) NOT NULL DEFAULT 'PENDING'
        CHECK (status IN (
            'PENDING',
            'IN_PROGRESS',
            'COMPLETED',
            'CANCELLED'
        )),
    result TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE maintenance_records (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    maintenance_task_id UUID NOT NULL REFERENCES maintenance_tasks(id),
    performed_by UUID NOT NULL REFERENCES staff(id),
    description TEXT NOT NULL,
    labor_cost NUMERIC(18,2) NOT NULL DEFAULT 0
        CHECK (labor_cost >= 0),
    completed_at TIMESTAMPTZ,
    note TEXT
);

CREATE TABLE maintenance_parts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    maintenance_record_id UUID NOT NULL REFERENCES maintenance_records(id),
    name VARCHAR(255) NOT NULL,
    quantity NUMERIC(18,2) NOT NULL
        CHECK (quantity > 0),
    unit_cost NUMERIC(18,2) NOT NULL DEFAULT 0
        CHECK (unit_cost >= 0),
    total_cost NUMERIC(18,2) NOT NULL DEFAULT 0
        CHECK (total_cost >= 0),
    note TEXT
);

CREATE TABLE equipment_disposals (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    equipment_id UUID NOT NULL UNIQUE REFERENCES equipments(id),
    reason TEXT NOT NULL,
    disposal_date DATE NOT NULL,
    method VARCHAR(20) NOT NULL
        CHECK (method IN (
            'SOLD',
            'SCRAPPED',
            'DONATED',
            'DESTROYED',
            'OTHER'
        )),
    amount NUMERIC(18,2) NOT NULL DEFAULT 0
        CHECK (amount >= 0),
    description TEXT,
    approved_by UUID NOT NULL REFERENCES users(id),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ============================================================
-- CONTRACT REPRESENTATIVE / DOCUMENTS
-- ============================================================

CREATE TABLE rental_contract_representatives (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    rental_contract_id UUID NOT NULL UNIQUE
        REFERENCES rental_contracts(id),
    full_name VARCHAR(150) NOT NULL,
    id_number VARCHAR(50) NOT NULL,
    phone VARCHAR(20),
    email VARCHAR(255),
    representative_type VARCHAR(30) NOT NULL
        CHECK (representative_type IN (
            'LEGAL_REPRESENTATIVE',
            'AUTHORIZED_REPRESENTATIVE'
        )),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE rental_contract_documents (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    rental_contract_id UUID NOT NULL
        REFERENCES rental_contracts(id),
    representative_id UUID
        REFERENCES rental_contract_representatives(id),
    document_type VARCHAR(30) NOT NULL
        CHECK (document_type IN (
            'ID_CARD',
            'AUTHORIZATION_LETTER',
            'OTHER'
        )),
    storage_file_id UUID NOT NULL
        REFERENCES storage_files(id),
    display_order INTEGER NOT NULL DEFAULT 1
        CHECK (display_order > 0),
    document_number VARCHAR(100),
    issued_date DATE,
    note TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ============================================================
-- 13. ATTACHMENTS
-- ============================================================

CREATE TABLE service_assignment_attachments (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    service_assignment_id UUID NOT NULL
        REFERENCES service_assignments(id),
    storage_file_id UUID NOT NULL
        REFERENCES storage_files(id),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (service_assignment_id, storage_file_id)
);

CREATE TABLE handover_note_attachments (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    handover_note_id UUID NOT NULL
        REFERENCES handover_notes(id),
    storage_file_id UUID NOT NULL
        REFERENCES storage_files(id),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (handover_note_id, storage_file_id)
);

CREATE TABLE handover_issue_attachments (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    handover_issue_id UUID NOT NULL
        REFERENCES handover_issues(id),
    storage_file_id UUID NOT NULL
        REFERENCES storage_files(id),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (handover_issue_id, storage_file_id)
);

CREATE TABLE equipment_incident_attachments (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    incident_id UUID NOT NULL
        REFERENCES equipment_incidents(id),
    storage_file_id UUID NOT NULL
        REFERENCES storage_files(id),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (incident_id, storage_file_id)
);

CREATE TABLE maintenance_task_attachments (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    maintenance_task_id UUID NOT NULL
        REFERENCES maintenance_tasks(id),
    storage_file_id UUID NOT NULL
        REFERENCES storage_files(id),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (maintenance_task_id, storage_file_id)
);

-- ============================================================
-- INDEXES
-- ============================================================

CREATE INDEX idx_user_roles_role_id
    ON user_roles(role_id);

CREATE INDEX idx_staff_user_id
    ON staff(user_id);

CREATE INDEX idx_company_users_company_id
    ON company_users(company_id);

CREATE INDEX idx_notifications_user_id
    ON notifications(user_id);

CREATE INDEX idx_categories_parent_id
    ON categories(parent_id);

CREATE INDEX idx_equipment_model_categories_category_id
    ON equipment_model_categories(category_id);

CREATE INDEX idx_equipments_model_id
    ON equipments(equipment_model_id);

CREATE INDEX idx_equipments_warehouse_id
    ON equipments(warehouse_id);

CREATE INDEX idx_equipments_status
    ON equipments(status);

CREATE INDEX idx_reservations_company_id
    ON reservations(company_id);

CREATE INDEX idx_reservation_items_reservation_id
    ON reservation_items(reservation_id);

CREATE INDEX idx_contracts_company_id
    ON rental_contracts(company_id);

CREATE INDEX idx_contracts_status
    ON rental_contracts(status);

CREATE INDEX idx_contracts_reservation_id
    ON rental_contracts(reservation_id);

CREATE INDEX idx_contract_items_contract_id
    ON rental_contract_items(rental_contract_id);

CREATE INDEX idx_contract_services_contract_id
    ON rental_contract_services(rental_contract_id);

CREATE INDEX idx_contract_status_history_contract_id
    ON contract_status_history(rental_contract_id);

CREATE INDEX idx_service_assignment_members_assignment_id
    ON service_assignment_members(service_assignment_id);

CREATE INDEX idx_service_assignment_members_staff_id
    ON service_assignment_members(staff_id);

CREATE INDEX idx_handover_notes_contract_id
    ON handover_notes(rental_contract_id);

CREATE INDEX idx_handover_notes_replacement_request_id
    ON handover_notes(replacement_request_id);

CREATE INDEX idx_handover_items_equipment_id
    ON handover_items(equipment_id);

CREATE INDEX idx_handover_issues_handover_id
    ON handover_issues(handover_note_id);

CREATE INDEX idx_handover_issue_items_issue_id
    ON handover_issue_items(handover_issue_id);

CREATE INDEX idx_handover_issue_items_equipment_id
    ON handover_issue_items(equipment_id);

CREATE INDEX idx_equipment_returns_contract_id
    ON equipment_returns(rental_contract_id);

CREATE INDEX idx_equipment_return_items_equipment_id
    ON equipment_return_items(equipment_id);

CREATE INDEX idx_rental_charges_contract_id
    ON rental_charges(rental_contract_id);

CREATE INDEX idx_rental_charges_equipment_id
    ON rental_charges(equipment_id);

CREATE INDEX idx_incidents_company_id
    ON equipment_incidents(company_id);

CREATE INDEX idx_incident_items_incident_id
    ON equipment_incident_items(incident_id);

CREATE INDEX idx_incident_items_equipment_id
    ON equipment_incident_items(equipment_id);

CREATE INDEX idx_incident_charges_incident_item_id
    ON incident_charges(incident_item_id);

CREATE INDEX idx_replacement_requests_contract_id
    ON replacement_requests(rental_contract_id);

CREATE INDEX idx_rental_charges_due_date
    ON rental_charges(due_date)
    WHERE status IN ('UNPAID', 'PARTIALLY_PAID');

CREATE INDEX idx_rental_charges_contract_period
    ON rental_charges(rental_contract_id, period_start, period_end)
    WHERE charge_type = 'RENTAL';

CREATE INDEX idx_replacement_requests_incident_id
    ON replacement_requests(incident_id);

CREATE INDEX idx_replacement_request_items_request_id
    ON replacement_request_items(replacement_request_id);

CREATE INDEX idx_maintenance_tasks_equipment_id
    ON maintenance_tasks(equipment_id);

CREATE INDEX idx_maintenance_tasks_assignment_id
    ON maintenance_tasks(service_assignment_id);

CREATE INDEX idx_maintenance_records_task_id
    ON maintenance_records(maintenance_task_id);

CREATE INDEX idx_maintenance_parts_record_id
    ON maintenance_parts(maintenance_record_id);

CREATE INDEX idx_storage_files_uploaded_by
    ON storage_files(uploaded_by);

CREATE INDEX idx_contract_items_model_id
    ON rental_contract_items(equipment_model_id);

CREATE INDEX idx_contract_documents_contract_id
    ON rental_contract_documents(rental_contract_id);

CREATE INDEX idx_contract_documents_representative_id
    ON rental_contract_documents(representative_id);

CREATE INDEX idx_contract_documents_storage_file_id
    ON rental_contract_documents(storage_file_id);

-- ============================================================
-- INITIAL ROLES
-- ============================================================

INSERT INTO roles (code, name, description)
VALUES
    ('ADMIN', 'Quản trị hệ thống', 'Quản lý toàn bộ hệ thống'),
    ('MANAGER', 'Quản lý', 'Duyệt hợp đồng và điều phối hoạt động'),
    ('SALES', 'Kinh doanh', 'Tiếp nhận và xử lý yêu cầu thuê'),
    ('STOREKEEPER', 'Thủ kho', 'Quản lý thiết bị vật lý và kho'),
    ('TECHNICIAN', 'Kỹ thuật viên', 'Xử lý sự cố và bảo trì thiết bị'),
    ('COMPANY', 'Khách hàng doanh nghiệp', 'Tài khoản của doanh nghiệp thuê thiết bị')
ON CONFLICT (code) DO NOTHING;

-- ============================================================
-- END
-- ============================================================

-- ============================================================
-- AUTHENTICATION TOKENS
-- UUIDv7 identifiers are generated by the .NET application.
-- ============================================================

-- Run this only if the original equipment-rental schema has already been applied.
-- Token IDs and family_id are generated by the .NET application with UUIDv7.
CREATE TABLE refresh_tokens (
    id UUID PRIMARY KEY,
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    token_hash VARCHAR(64) NOT NULL UNIQUE,
    family_id UUID NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    revoked_at TIMESTAMPTZ NULL,
    replaced_by_token_id UUID NULL
        REFERENCES refresh_tokens(id) ON DELETE SET NULL,
    CONSTRAINT chk_refresh_tokens_expiration
        CHECK (expires_at > created_at)
);

CREATE INDEX idx_refresh_tokens_user_id ON refresh_tokens(user_id);
CREATE INDEX idx_refresh_tokens_family_id ON refresh_tokens(family_id);
CREATE INDEX idx_refresh_tokens_expires_at ON refresh_tokens(expires_at);
CREATE INDEX idx_refresh_tokens_active_user
    ON refresh_tokens(user_id) WHERE revoked_at IS NULL;

CREATE TABLE revoked_tokens (
    jti UUID PRIMARY KEY,
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    expires_at TIMESTAMPTZ NOT NULL,
    revoked_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_revoked_tokens_user_id ON revoked_tokens(user_id);
CREATE INDEX idx_revoked_tokens_expires_at ON revoked_tokens(expires_at);
