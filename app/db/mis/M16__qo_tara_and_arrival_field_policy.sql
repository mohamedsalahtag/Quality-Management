-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M16  -  material tara + arrival field policy
-- ============================================================================
--  Prod mirror of V41. Tables live in the qms schema (dbo synonyms over them).
--
--  Part A: qms.qms_quality_order_material.tara_weight — the shared, material-
--          level tare weight (net = each sample's gross - this tara). Backfilled
--          from existing per-sample TARA readings.
--
--  Part B: (added with W6) per-field policy for arrival editable fields.
--
--  Idempotent: safe to re-run.
-- ============================================================================

-- Part A ------------------------------------------------------------
IF COL_LENGTH('qms.qms_quality_order_material', 'tara_weight') IS NULL
    ALTER TABLE qms.qms_quality_order_material ADD tara_weight DECIMAL(18,3) NULL;
GO

UPDATE m
SET    m.tara_weight = t.tara
FROM   qms.qms_quality_order_material m
CROSS APPLY (
    SELECT TOP 1 r.numeric_value AS tara
    FROM   qms.qms_sample s
    JOIN   qms.qms_sample_reading r ON r.sample_id = s.sample_id
    WHERE  s.qo_material_id = m.qo_material_id AND s.is_deleted = 0
      AND  r.reading_type_code IN ('TARA', 'TARA_WEIGHT')
      AND  r.numeric_value IS NOT NULL
    ORDER  BY s.sample_no
) t
WHERE  m.tara_weight IS NULL;
GO

SELECT 'qo_material_tara_backfilled' AS check_item, COUNT(*) AS value
FROM   qms.qms_quality_order_material WHERE tara_weight IS NOT NULL;
GO

-- Part B ------------------------------------------------------------
-- Per-field policy table (overrides only; code holds the registry + defaults).
IF OBJECT_ID(N'qms.qms_arrival_field_policy', N'U') IS NULL
BEGIN
    CREATE TABLE qms.qms_arrival_field_policy (
        field_key            VARCHAR(64)   NOT NULL CONSTRAINT PK_qms_arrival_field_policy PRIMARY KEY,
        is_mandatory         BIT           NOT NULL CONSTRAINT DF_qafp_mandatory DEFAULT (0),
        editable_when_closed BIT           NOT NULL CONSTRAINT DF_qafp_ewc       DEFAULT (0),
        updated_at           DATETIME2     NULL,
        updated_by           NVARCHAR(256) NULL
    );
END;
GO

-- dbo synonym so the app's unqualified queries resolve (M05 convention).
IF NOT EXISTS (SELECT 1 FROM sys.synonyms WHERE name = 'qms_arrival_field_policy' AND schema_id = SCHEMA_ID('dbo'))
    CREATE SYNONYM dbo.qms_arrival_field_policy FOR qms.qms_arrival_field_policy;
GO

-- New admin screen row (action permission auto-discovered from the controller
-- attribute at startup; only the screen catalogue row needs seeding).
IF NOT EXISTS (SELECT 1 FROM qms.qms_screen WHERE screen_key = 'Parameters.ArrivalFieldRules')
    INSERT INTO qms.qms_screen (screen_key, display_name, group_name, sort_order, supports_access_level)
    VALUES ('Parameters.ArrivalFieldRules', N'Arrival Field Rules', 'Admin', 380, 0);
-- Lives in the Admin menu (moved from Parameters); keep the matrix grouping in step.
UPDATE qms.qms_screen SET group_name = 'Admin', sort_order = 380
WHERE  screen_key = 'Parameters.ArrivalFieldRules' AND group_name <> 'Admin';
GO

SELECT 'arrival_field_rules_screen' AS check_item, COUNT(*) AS value
FROM   qms.qms_screen WHERE screen_key = 'Parameters.ArrivalFieldRules';
