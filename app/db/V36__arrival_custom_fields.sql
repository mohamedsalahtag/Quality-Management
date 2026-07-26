-- =====================================================================
-- V36  Arrival custom fields (2026-07-07)
--
-- Admin-defined extra fields on an Arrival, each linked to exactly ONE
-- material group. The field is only shown / editable on an arrival whose
-- line items (qms_arrival_item) contain that material group, and it is
-- printed in the Arrival Checklist PDF identity block after Seal Number.
--
-- Modeled on the Sample Header Field system (V20): a typed catalog table
-- plus an EAV value table with one column per value kind.
--   value_kind: 'Text' | 'Numeric' | 'Date' | 'YesNo'
--     Text   -> text_value      (also stores YesNo as 'Yes'/'No')
--     Numeric-> numeric_value
--     Date   -> date_value
--
-- Managed from Parameters > Arrival Fields (Manager/SiteAdmin).
-- =====================================================================

IF OBJECT_ID('qms_arrival_field') IS NULL
BEGIN
    CREATE TABLE qms_arrival_field (
        field_id        INT IDENTITY(1,1) PRIMARY KEY,
        field_name      NVARCHAR(80)  NOT NULL,
        value_kind      VARCHAR(10)   NOT NULL
            CONSTRAINT DF_qms_arrival_field_kind DEFAULT 'Text',
        material_group  VARCHAR(40)   NOT NULL,
        sort_order      INT           NOT NULL
            CONSTRAINT DF_qms_arrival_field_sort DEFAULT 500,
        is_active       BIT           NOT NULL
            CONSTRAINT DF_qms_arrival_field_active DEFAULT 1,
        created_at      DATETIME2     NOT NULL
            CONSTRAINT DF_qms_arrival_field_created DEFAULT SYSUTCDATETIME(),
        created_by      NVARCHAR(100) NULL,
        CONSTRAINT CK_qms_arrival_field_kind
            CHECK (value_kind IN ('Text','Numeric','Date','YesNo')),
        CONSTRAINT UQ_qms_arrival_field_name_group
            UNIQUE (field_name, material_group)
    );
END
GO

IF OBJECT_ID('qms_arrival_field_value') IS NULL
BEGIN
    CREATE TABLE qms_arrival_field_value (
        value_id        BIGINT IDENTITY(1,1) PRIMARY KEY,
        arrival_id      BIGINT        NOT NULL,
        field_id        INT           NOT NULL,
        text_value      NVARCHAR(400) NULL,
        numeric_value   DECIMAL(18,4) NULL,
        date_value      DATE          NULL,
        updated_at      DATETIME2     NOT NULL
            CONSTRAINT DF_qms_arrival_field_value_upd DEFAULT SYSUTCDATETIME(),
        updated_by      NVARCHAR(100) NULL,
        CONSTRAINT UQ_qms_arrival_field_value UNIQUE (arrival_id, field_id),
        CONSTRAINT FK_qms_arrival_field_value_arrival
            FOREIGN KEY (arrival_id) REFERENCES qms_arrival (arrival_id) ON DELETE CASCADE,
        CONSTRAINT FK_qms_arrival_field_value_field
            FOREIGN KEY (field_id) REFERENCES qms_arrival_field (field_id)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_qms_arrival_field_value_field'
                 AND object_id = OBJECT_ID('qms_arrival_field_value'))
BEGIN
    CREATE INDEX IX_qms_arrival_field_value_field
        ON qms_arrival_field_value (field_id);
END
GO

SELECT 'qms_arrival_field'       AS check_item, COUNT(*) AS value FROM qms_arrival_field;
SELECT 'qms_arrival_field_value' AS check_item, COUNT(*) AS value FROM qms_arrival_field_value;
