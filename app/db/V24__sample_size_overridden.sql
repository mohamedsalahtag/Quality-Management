-- ===================================================================
-- V24  Add size_overridden flag to qms_sample.
--
-- A sample's sample_size has historically been inherited from its
-- material's qms_qo_material.sample_size and bulk-overwritten whenever
-- the material's value changed. With the new per-sample Size input on
-- the sample form, a user can override the size for an individual
-- sample. This flag is set to 1 when the override is in effect and
-- must protect that sample from the material-level propagation
-- UPDATE in QualityOrderService.SaveMaterialHeaderValuesAndSizeAsync.
--
-- Idempotent.
-- ===================================================================

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.qms_sample')
      AND name = N'size_overridden'
)
BEGIN
    ALTER TABLE dbo.qms_sample
        ADD size_overridden BIT NOT NULL
            CONSTRAINT DF_qms_sample_size_overridden DEFAULT (0);
END;
