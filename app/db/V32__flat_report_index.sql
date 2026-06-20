-- ===================================================================
-- V32  Flat-line data-hub report support.
--
-- Adds a filtered index on qms_sample(created_at DESC) for the flat
-- per-defect report's date-range scan. The report's hot path filters
-- by created_at and is_deleted; the existing PK index on sample_id is
-- not useful here. Filtered to is_deleted = 0 keeps the index small.
-- ===================================================================

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_qms_sample_created_at_active'
      AND object_id = OBJECT_ID('qms_sample')
)
    CREATE INDEX IX_qms_sample_created_at_active
        ON qms_sample(created_at DESC)
        INCLUDE (sample_id, qo_material_id, quality_order_id, sample_no, sample_size)
        WHERE is_deleted = 0;
GO
