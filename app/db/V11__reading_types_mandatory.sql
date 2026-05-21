-- ===================================================================
-- V11  Mandatory flag on reading types.
--
-- Each reading type now carries an is_mandatory flag. When set, the
-- sample form refuses to save until that reading has a value entered.
-- Existing rows default to 0 (not mandatory) so behaviour is unchanged
-- for already-configured groups; admins opt-in per reading type from
-- the Reading Types page.
-- ===================================================================

IF COL_LENGTH('qms_reading_type','is_mandatory') IS NULL
    ALTER TABLE qms_reading_type ADD is_mandatory BIT NOT NULL CONSTRAINT DF_qms_reading_type_is_mandatory DEFAULT 0;
GO

-- Sanity report.
SELECT material_group,
       SUM(CASE WHEN is_mandatory = 1 THEN 1 ELSE 0 END) AS mandatory_count,
       COUNT(*) AS total_count
FROM   qms_reading_type
GROUP BY material_group
ORDER BY material_group;
