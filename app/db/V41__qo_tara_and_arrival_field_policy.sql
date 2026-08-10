-- ===================================================================
-- V41  (dev DB)  Material-level tara weight + arrival field policy
--
-- Part A: tare weight moves from a per-sample TARA reading to a single
--         value on the QO material (shared by every sample; net = each
--         sample's gross - this tara). Backfills from existing per-sample
--         TARA readings so current orders keep their numbers.
--
-- Part B: (added with W6) per-field policy for arrival editable fields.
--
-- Idempotent.
-- ===================================================================

-- Part A ------------------------------------------------------------
IF COL_LENGTH('dbo.qms_quality_order_material', 'tara_weight') IS NULL
    ALTER TABLE dbo.qms_quality_order_material ADD tara_weight DECIMAL(18,3) NULL;
GO

-- Backfill: first non-null per-sample TARA reading per material.
UPDATE m
SET    m.tara_weight = t.tara
FROM   dbo.qms_quality_order_material m
CROSS APPLY (
    SELECT TOP 1 r.numeric_value AS tara
    FROM   dbo.qms_sample s
    JOIN   dbo.qms_sample_reading r ON r.sample_id = s.sample_id
    WHERE  s.qo_material_id = m.qo_material_id AND s.is_deleted = 0
      AND  r.reading_type_code IN ('TARA', 'TARA_WEIGHT')
      AND  r.numeric_value IS NOT NULL
    ORDER  BY s.sample_no
) t
WHERE  m.tara_weight IS NULL;
GO

-- Part B ------------------------------------------------------------
-- Per-field policy for editable arrival fields. Rows are OVERRIDES only —
-- the app falls back to code defaults (ArrivalFieldRegistry) for any field
-- without a row, so no seed is needed here.
IF OBJECT_ID(N'dbo.qms_arrival_field_policy', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.qms_arrival_field_policy (
        field_key            VARCHAR(64)   NOT NULL CONSTRAINT PK_qms_arrival_field_policy PRIMARY KEY,
        is_mandatory         BIT           NOT NULL CONSTRAINT DF_qafp_mandatory DEFAULT (0),
        editable_when_closed BIT           NOT NULL CONSTRAINT DF_qafp_ewc       DEFAULT (0),
        updated_at           DATETIME2     NULL,
        updated_by           NVARCHAR(256) NULL
    );
END;
GO

