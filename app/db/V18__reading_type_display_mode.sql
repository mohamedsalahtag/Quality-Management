-- ===================================================================
-- V18  Reading Types: display_mode for the QO PDF grouped summary
--
-- Adds a display_mode column to qms_reading_type so admins can pick
-- how each reading appears in the new grouped summary on the Quality
-- Order PDF (page 1). Allowed:
--   text          -- distinct text values joined by comma
--   count         -- number of samples that have a value
--   sum           -- arithmetic sum of numeric_value
--   sum_over_size -- (sum numeric_value) / (sum sample_size) * 100  (%)
--   formula       -- placeholder; renders em-dash until the formula
--                    language is designed (separate follow-up)
--
-- Backfill: infer from the existing value_kind so existing rows behave
-- sensibly the moment the migration lands -- Numeric => 'sum',
-- Text => 'text'. Admins can refine later (e.g. flip Brix to
-- 'sum_over_size' if that reads better in the supplier-facing PDF).
-- ===================================================================

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('qms_reading_type') AND name = 'display_mode')
BEGIN
    ALTER TABLE qms_reading_type ADD display_mode VARCHAR(20) NULL;
END
GO

UPDATE qms_reading_type
   SET display_mode = CASE WHEN value_kind = 'Text' THEN 'text' ELSE 'sum' END
 WHERE display_mode IS NULL;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints WHERE name = 'CK_qms_reading_type_display_mode')
BEGIN
    ALTER TABLE qms_reading_type
        ADD CONSTRAINT CK_qms_reading_type_display_mode
        CHECK (display_mode IN ('text','count','sum','sum_over_size','formula'));
END
GO

-- DEFAULT so any pre-V18 code path that INSERTs a reading type without
-- supplying the new column still works (column was added in this
-- migration; the running service's SaveReadingType is rebuilt to
-- supply the value but the DEFAULT removes the deploy-window race).
IF NOT EXISTS (
    SELECT 1 FROM sys.default_constraints WHERE name = 'DF_qms_reading_type_display_mode')
BEGIN
    ALTER TABLE qms_reading_type
        ADD CONSTRAINT DF_qms_reading_type_display_mode DEFAULT ('sum') FOR display_mode;
END
GO

-- Promote to NOT NULL only after the backfill so a half-applied
-- migration in a noisy env can't leave NULLs that violate the
-- constraint.
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('qms_reading_type')
      AND name = 'display_mode' AND is_nullable = 1)
BEGIN
    ALTER TABLE qms_reading_type
        ALTER COLUMN display_mode VARCHAR(20) NOT NULL;
END
GO

SELECT 'qms_reading_type.display_mode' AS check_item,
       CASE WHEN EXISTS (SELECT 1 FROM sys.columns
            WHERE object_id = OBJECT_ID('qms_reading_type')
              AND name = 'display_mode' AND is_nullable = 0)
            THEN 'OK' ELSE 'MISSING' END AS status;
GO

SELECT display_mode, COUNT(*) AS rows_with_mode
  FROM qms_reading_type
 GROUP BY display_mode
 ORDER BY display_mode;
GO
