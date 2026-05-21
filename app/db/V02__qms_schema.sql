-- ============================================================================
--  SharbatlyQMS  -  V02  -  Core QMS schema
-- ============================================================================
--  Implements section 6 of QMS_Extended_Assessment_and_Execution_Plan.md.
--  All transactional tables prefixed qms_ to keep pack tables (Users,
--  EmailGroups, AlertRules, etc.) easy to spot.
--  Status fields are NVARCHAR with CHECK constraints rather than CLR enums,
--  matching the plan's editability matrix in section 7.
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 6.1 Core identity tables
-- ---------------------------------------------------------------------------

-- Customer Arrival / Arrival Overview header
CREATE TABLE qms_arrival (
    arrival_id      BIGINT IDENTITY(1,1) PRIMARY KEY,
    arrival_no      VARCHAR(20) NOT NULL UNIQUE,
    source_system   VARCHAR(20) NOT NULL DEFAULT 'S4HANA',
    bol_no          VARCHAR(35) NULL,
    container_no    VARCHAR(35) NULL,
    ebeln           VARCHAR(10) NULL,                 -- SAP PO number
    bukrs           VARCHAR(4)  NULL,                 -- Company code
    vendor_no       VARCHAR(10) NULL,
    vendor_name     NVARCHAR(80) NULL,
    status_code     VARCHAR(20) NOT NULL DEFAULT 'Draft',
    created_at      DATETIME2   NOT NULL DEFAULT SYSUTCDATETIME(),
    created_by      NVARCHAR(80) NOT NULL,
    completed_at    DATETIME2   NULL,
    completed_by    NVARCHAR(80) NULL,
    row_version     ROWVERSION,
    CONSTRAINT CK_qms_arrival_status CHECK (status_code IN ('Draft','Completed','Cancelled'))
);
CREATE INDEX IX_qms_arrival_container ON qms_arrival(container_no);
CREATE INDEX IX_qms_arrival_bol       ON qms_arrival(bol_no);
CREATE INDEX IX_qms_arrival_status    ON qms_arrival(status_code);

-- Immutable snapshot of SAP/OData data captured at arrival save
CREATE TABLE qms_arrival_sap_snapshot (
    snapshot_id        BIGINT IDENTITY(1,1) PRIMARY KEY,
    arrival_id         BIGINT NOT NULL REFERENCES qms_arrival(arrival_id),
    odata_service_name NVARCHAR(120) NOT NULL,
    odata_query_hash   CHAR(64) NOT NULL,
    payload_json       NVARCHAR(MAX) NOT NULL,
    captured_at        DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    captured_by        NVARCHAR(80) NOT NULL
);
CREATE INDEX IX_qms_arrival_sap_snapshot_arrival ON qms_arrival_sap_snapshot(arrival_id);

-- One row per SAP material/item line selected for an arrival
CREATE TABLE qms_arrival_item (
    arrival_item_id     BIGINT IDENTITY(1,1) PRIMARY KEY,
    arrival_id          BIGINT NOT NULL REFERENCES qms_arrival(arrival_id),
    ebeln               VARCHAR(10) NOT NULL,
    ebelp               VARCHAR(5)  NOT NULL,
    material_no         VARCHAR(40) NOT NULL,
    material_desc       NVARCHAR(120) NULL,
    plant               VARCHAR(4)  NULL,
    storage_location    VARCHAR(4)  NULL,
    batch_no            VARCHAR(20) NULL,
    quantity            DECIMAL(18,3) NULL,
    uom                 VARCHAR(3)  NULL,
    material_group      VARCHAR(9)  NULL,
    material_group_desc NVARCHAR(80) NULL,
    major_category      NVARCHAR(80) NULL,
    row_version         ROWVERSION
);
CREATE UNIQUE INDEX UX_qms_arrival_item_natural
    ON qms_arrival_item(arrival_id, ebeln, ebelp, material_no, plant, storage_location, batch_no);

-- ---------------------------------------------------------------------------
-- 6.2 Arrival checklist + shipment snapshot
-- ---------------------------------------------------------------------------

CREATE TABLE qms_arrival_checklist (
    checklist_id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    arrival_id                    BIGINT NOT NULL UNIQUE REFERENCES qms_arrival(arrival_id),
    seal_no                       VARCHAR(30)   NULL,
    carrier_name                  NVARCHAR(80)  NULL,
    seal_intact                   BIT NULL,
    seal_matches_documents        BIT NULL,
    external_damage_exists        BIT NULL,
    set_temperature               DECIMAL(6,2)  NULL,
    display_temperature           DECIMAL(6,2)  NULL,
    cargo_smell_normal            BIT NULL,
    visual_cargo_acceptable       BIT NULL,
    cargo_shifted_collapsed_water BIT NULL,
    pulp_temp_front               DECIMAL(6,2)  NULL,
    pulp_temp_middle              DECIMAL(6,2)  NULL,
    pulp_temp_back                DECIMAL(6,2)  NULL,
    data_logger_located           BIT NULL,
    data_logger_serial            NVARCHAR(50)  NULL,
    data_logger_photo_taken       BIT NULL,
    logger_handed_over            BIT NULL,
    logger_active_data_available  BIT NULL,
    logger_temperature            DECIMAL(6,2)  NULL,
    notes                         NVARCHAR(MAX) NULL,
    updated_at                    DATETIME2 NULL,
    updated_by                    NVARCHAR(80) NULL
);

CREATE TABLE qms_shipment_snapshot (
    shipment_snapshot_id  BIGINT IDENTITY(1,1) PRIMARY KEY,
    arrival_id            BIGINT NOT NULL UNIQUE REFERENCES qms_arrival(arrival_id),
    internal_shipment_no  VARCHAR(20) NOT NULL UNIQUE,
    loading_date          DATE NULL,
    sailing_date          DATE NULL,
    examination_date      DATE NULL,
    arrival_date          DATE NULL,
    unloading_date        DATE NULL,
    inspection_date       DATE NULL,
    transit_days          SMALLINT NULL,
    time_bar              SMALLINT NULL,
    loading_port          NVARCHAR(60) NULL,
    loading_country       NVARCHAR(60) NULL,
    arrival_place         NVARCHAR(80) NULL,
    vessel_name           NVARCHAR(80) NULL,
    voyage_number         NVARCHAR(50) NULL,
    pullout_date          DATE NULL,
    receive_date          DATE NULL,
    time_bar_exceeded     BIT NULL,
    inspection_point      NVARCHAR(60) NULL,
    joint_survey          BIT NULL,
    status_code           VARCHAR(20) NOT NULL DEFAULT 'Draft',
    CONSTRAINT CK_qms_shipment_snapshot_status CHECK (status_code IN ('Draft','Confirmed'))
);

-- ---------------------------------------------------------------------------
-- 6.3 Quality order header + materials
-- ---------------------------------------------------------------------------

CREATE TABLE qms_quality_order (
    quality_order_id  BIGINT IDENTITY(1,1) PRIMARY KEY,
    quality_order_no  VARCHAR(20) NOT NULL UNIQUE,
    arrival_id        BIGINT NOT NULL REFERENCES qms_arrival(arrival_id),
    status_code       VARCHAR(20) NOT NULL DEFAULT 'Initial',
    opened_at         DATETIME2 NULL,
    opened_by         NVARCHAR(80) NULL,
    closed_at         DATETIME2 NULL,
    closed_by         NVARCHAR(80) NULL,
    close_reason      NVARCHAR(500) NULL,
    reopened_at       DATETIME2 NULL,
    reopened_by       NVARCHAR(80) NULL,
    reopen_reason     NVARCHAR(500) NULL,
    created_at        DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    created_by        NVARCHAR(80) NOT NULL,
    row_version       ROWVERSION,
    CONSTRAINT CK_qms_quality_order_status CHECK
        (status_code IN ('Initial','Open','Closed','Reopened','Cancelled'))
);
-- Only one active (non-cancelled) quality order per arrival
CREATE UNIQUE INDEX UX_qms_quality_order_active_per_arrival
    ON qms_quality_order(arrival_id) WHERE status_code <> 'Cancelled';

CREATE TABLE qms_quality_order_material (
    qo_material_id          BIGINT IDENTITY(1,1) PRIMARY KEY,
    quality_order_id        BIGINT NOT NULL REFERENCES qms_quality_order(quality_order_id),
    arrival_item_id         BIGINT NOT NULL REFERENCES qms_arrival_item(arrival_item_id),
    material_no             VARCHAR(40) NOT NULL,
    material_desc           NVARCHAR(120) NULL,
    origin                  NVARCHAR(60) NULL,
    variety                 NVARCHAR(80) NULL,
    material_class          NVARCHAR(80) NULL,
    net_weight              DECIMAL(18,3) NULL,
    material_size           NVARCHAR(20) NULL,
    material_group          VARCHAR(9)  NULL,
    material_group_desc     NVARCHAR(80) NULL,
    major_category          NVARCHAR(80) NULL,
    brand                   NVARCHAR(80) NULL,
    pack_type               NVARCHAR(80) NULL,
    size_overridden         BIT NOT NULL DEFAULT 0,
    original_material_size  NVARCHAR(20) NULL,
    override_material_size  NVARCHAR(20) NULL,
    override_reason         NVARCHAR(500) NULL,
    override_approved_by    NVARCHAR(80) NULL,
    override_approved_at    DATETIME2 NULL
);
CREATE INDEX IX_qms_quality_order_material_qo  ON qms_quality_order_material(quality_order_id);
CREATE INDEX IX_qms_quality_order_material_mat ON qms_quality_order_material(material_no);

-- ---------------------------------------------------------------------------
-- 6.4 Sample / readings / observations / defects
-- ---------------------------------------------------------------------------

CREATE TABLE qms_sample (
    sample_id           BIGINT IDENTITY(1,1) PRIMARY KEY,
    quality_order_id    BIGINT NOT NULL REFERENCES qms_quality_order(quality_order_id),
    qo_material_id      BIGINT NOT NULL REFERENCES qms_quality_order_material(qo_material_id),
    sample_no           INT NOT NULL,
    carton_count        SMALLINT NULL,
    carton_identifier   NVARCHAR(50) NULL,
    sample_scope        VARCHAR(20) NOT NULL DEFAULT 'OneCarton',
    sample_size         SMALLINT NULL,
    grower              NVARCHAR(80) NULL,
    pallet_no           NVARCHAR(50) NULL,
    grower_pallet       NVARCHAR(50) NULL,
    pack_code           NVARCHAR(80) NULL,
    date_code           NVARCHAR(80) NULL,
    label_value         NVARCHAR(80) NULL,
    lot_no              NVARCHAR(80) NULL,
    packaging_material  NVARCHAR(80) NULL,
    created_at          DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    created_by          NVARCHAR(80) NOT NULL,
    updated_at          DATETIME2 NULL,
    updated_by          NVARCHAR(80) NULL,
    is_deleted          BIT NOT NULL DEFAULT 0,
    deleted_at          DATETIME2 NULL,
    deleted_by          NVARCHAR(80) NULL,
    row_version         ROWVERSION,
    CONSTRAINT CK_qms_sample_scope CHECK (sample_scope IN ('OneCarton','MultiCarton','Pallet','Lot'))
);
CREATE UNIQUE INDEX UX_qms_sample_active
    ON qms_sample(qo_material_id, sample_no) WHERE is_deleted = 0;

CREATE TABLE qms_sample_reading (
    reading_id        BIGINT IDENTITY(1,1) PRIMARY KEY,
    sample_id         BIGINT NOT NULL REFERENCES qms_sample(sample_id),
    reading_type_code VARCHAR(40) NOT NULL,
    numeric_value     DECIMAL(18,4) NULL,
    text_value        NVARCHAR(100) NULL,
    unit_code         VARCHAR(20) NULL,
    is_within_spec    BIT NULL,
    reading_sequence  INT NOT NULL DEFAULT 0,
    created_at        DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    created_by        NVARCHAR(80) NOT NULL
);
CREATE INDEX IX_qms_sample_reading_sample ON qms_sample_reading(sample_id);

CREATE TABLE qms_sample_observation (
    observation_id        BIGINT IDENTITY(1,1) PRIMARY KEY,
    sample_id             BIGINT NOT NULL REFERENCES qms_sample(sample_id),
    observation_type_code VARCHAR(40) NOT NULL,
    observation_text      NVARCHAR(MAX) NOT NULL,
    severity_code         VARCHAR(20) NULL,
    created_at            DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    created_by            NVARCHAR(80) NOT NULL
);
CREATE INDEX IX_qms_sample_observation_sample ON qms_sample_observation(sample_id);

CREATE TABLE qms_defect_catalog (
    defect_id        INT IDENTITY(1,1) PRIMARY KEY,
    defect_code      VARCHAR(50) NOT NULL UNIQUE,
    defect_name      NVARCHAR(100) NOT NULL,
    defect_category  VARCHAR(20) NOT NULL DEFAULT 'Minor',
    default_unit     VARCHAR(20) NOT NULL DEFAULT 'Count',
    is_active        BIT NOT NULL DEFAULT 1,
    sort_order       INT NOT NULL DEFAULT 0,
    CONSTRAINT CK_qms_defect_catalog_category CHECK
        (defect_category IN ('Major','Minor','Critical','Other'))
);

CREATE TABLE qms_material_group_defect (
    material_group_defect_id INT IDENTITY(1,1) PRIMARY KEY,
    material_group           VARCHAR(9) NOT NULL,
    major_category           NVARCHAR(80) NULL,
    defect_id                INT NOT NULL REFERENCES qms_defect_catalog(defect_id),
    is_required              BIT NOT NULL DEFAULT 0,
    is_active                BIT NOT NULL DEFAULT 1,
    display_section          VARCHAR(20) NOT NULL DEFAULT 'Minor',
    sort_order               INT NOT NULL DEFAULT 0,
    CONSTRAINT CK_qms_material_group_defect_section CHECK
        (display_section IN ('Major','Minor','Readings'))
);
CREATE INDEX IX_qms_material_group_defect_lookup
    ON qms_material_group_defect(material_group, major_category, is_active);

CREATE TABLE qms_sample_defect (
    sample_defect_id    BIGINT IDENTITY(1,1) PRIMARY KEY,
    sample_id           BIGINT NOT NULL REFERENCES qms_sample(sample_id),
    defect_id           INT NOT NULL REFERENCES qms_defect_catalog(defect_id),
    defect_value        DECIMAL(18,4) NULL,
    defect_percentage   DECIMAL(9,4) NULL,
    severity_code       VARCHAR(20) NULL,
    comment             NVARCHAR(500) NULL,
    is_within_tolerance BIT NULL,
    created_at          DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    created_by          NVARCHAR(80) NOT NULL
);
CREATE UNIQUE INDEX UX_qms_sample_defect_one_per_sample
    ON qms_sample_defect(sample_id, defect_id);

-- ---------------------------------------------------------------------------
-- 6.5 Image model
-- ---------------------------------------------------------------------------

CREATE TABLE qms_image_asset (
    image_id           BIGINT IDENTITY(1,1) PRIMARY KEY,
    storage_provider   VARCHAR(30) NOT NULL DEFAULT 'Local',
    original_file_name NVARCHAR(255) NOT NULL,
    content_type       VARCHAR(100) NOT NULL,
    file_size_bytes    BIGINT NOT NULL,
    storage_url        NVARCHAR(1000) NOT NULL,
    thumbnail_url      NVARCHAR(1000) NULL,
    checksum_sha256    CHAR(64) NULL,
    width_px           INT NULL,
    height_px          INT NULL,
    uploaded_at        DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    uploaded_by        NVARCHAR(80) NOT NULL,
    is_deleted         BIT NOT NULL DEFAULT 0
);

CREATE TABLE qms_image_link (
    image_link_id     BIGINT IDENTITY(1,1) PRIMARY KEY,
    image_id          BIGINT NOT NULL REFERENCES qms_image_asset(image_id),
    owner_type        VARCHAR(30) NOT NULL,
    owner_id          BIGINT NOT NULL,
    image_category    VARCHAR(40) NOT NULL,
    display_order     INT NOT NULL DEFAULT 0,
    caption           NVARCHAR(255) NULL,
    include_in_report BIT NOT NULL DEFAULT 1,
    created_at        DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    created_by        NVARCHAR(80) NOT NULL,
    CONSTRAINT CK_qms_image_link_owner_type CHECK
        (owner_type IN ('Arrival','ArrivalChecklist','QualityOrder','QualityOrderMaterial','Sample'))
);
CREATE INDEX IX_qms_image_link_owner ON qms_image_link(owner_type, owner_id);

-- ---------------------------------------------------------------------------
-- 6.6 Reading types catalog (admin-configurable, per plan §8.5)
-- ---------------------------------------------------------------------------

CREATE TABLE qms_reading_type (
    reading_type_id   INT IDENTITY(1,1) PRIMARY KEY,
    reading_type_code VARCHAR(40) NOT NULL UNIQUE,
    reading_name      NVARCHAR(100) NOT NULL,
    value_kind        VARCHAR(20) NOT NULL DEFAULT 'Numeric',  -- Numeric | Text
    default_unit      VARCHAR(20) NULL,
    is_active         BIT NOT NULL DEFAULT 1,
    sort_order        INT NOT NULL DEFAULT 0,
    CONSTRAINT CK_qms_reading_type_kind CHECK (value_kind IN ('Numeric','Text'))
);

CREATE TABLE qms_material_group_reading (
    material_group_reading_id INT IDENTITY(1,1) PRIMARY KEY,
    material_group            VARCHAR(9) NOT NULL,
    major_category            NVARCHAR(80) NULL,
    reading_type_id           INT NOT NULL REFERENCES qms_reading_type(reading_type_id),
    is_required               BIT NOT NULL DEFAULT 0,
    is_active                 BIT NOT NULL DEFAULT 1,
    sort_order                INT NOT NULL DEFAULT 0
);
CREATE INDEX IX_qms_material_group_reading_lookup
    ON qms_material_group_reading(material_group, major_category, is_active);

-- ---------------------------------------------------------------------------
-- 6.7 Status, audit, report log
-- ---------------------------------------------------------------------------

CREATE TABLE qms_status_history (
    status_history_id BIGINT IDENTITY(1,1) PRIMARY KEY,
    entity_type       VARCHAR(40) NOT NULL,
    entity_id         BIGINT NOT NULL,
    old_status        VARCHAR(30) NULL,
    new_status        VARCHAR(30) NOT NULL,
    reason            NVARCHAR(500) NULL,
    changed_at        DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    changed_by        NVARCHAR(80) NOT NULL
);
CREATE INDEX IX_qms_status_history_entity ON qms_status_history(entity_type, entity_id);

CREATE TABLE qms_audit_log (
    audit_id        BIGINT IDENTITY(1,1) PRIMARY KEY,
    entity_type     VARCHAR(40) NOT NULL,
    entity_id       BIGINT NOT NULL,
    action_code     VARCHAR(40) NOT NULL,
    old_values_json NVARCHAR(MAX) NULL,
    new_values_json NVARCHAR(MAX) NULL,
    changed_at      DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    changed_by      NVARCHAR(80) NOT NULL,
    source_ip       VARCHAR(45) NULL
);
CREATE INDEX IX_qms_audit_log_entity ON qms_audit_log(entity_type, entity_id);

CREATE TABLE qms_report_log (
    report_log_id     BIGINT IDENTITY(1,1) PRIMARY KEY,
    quality_order_id  BIGINT NOT NULL REFERENCES qms_quality_order(quality_order_id),
    archive_path      NVARCHAR(1000) NOT NULL,
    calculation_ver   VARCHAR(20) NOT NULL DEFAULT 'v1',
    page_count        INT NULL,
    file_size_bytes   BIGINT NULL,
    generated_at      DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    generated_by      NVARCHAR(80) NOT NULL
);
CREATE INDEX IX_qms_report_log_qo ON qms_report_log(quality_order_id);

-- ---------------------------------------------------------------------------
-- Number generation: arrival_no, internal_shipment_no, quality_order_no
-- ---------------------------------------------------------------------------
CREATE SEQUENCE seq_qms_arrival_no       AS INT START WITH 1 INCREMENT BY 1;
CREATE SEQUENCE seq_qms_shipment_no      AS INT START WITH 1 INCREMENT BY 1;
CREATE SEQUENCE seq_qms_quality_order_no AS INT START WITH 1 INCREMENT BY 1;
