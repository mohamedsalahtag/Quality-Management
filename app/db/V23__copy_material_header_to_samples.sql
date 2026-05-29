-- ===================================================================
-- V23  Copy material-scoped header values down onto every sample.
--
-- Material-scoped header values (V21/V22) were stored only at the
-- material level (qms_qo_material_header_value) and shown on samples via
-- a read-only "inherited" recap. Per the new design each sample now owns
-- its own copy of those values in qms_sample_header_value, and the recap
-- panels are removed. Going forward the copy-down happens on material
-- save / sample create; this migration backfills the values onto every
-- EXISTING sample so already-created samples reflect the data immediately.
--
-- Idempotent: wipe the Material-scoped rows on every sample, then
-- re-insert from each sample's material. Re-running converges.
-- ===================================================================

DELETE shv
FROM   qms_sample_header_value shv
JOIN   qms_sample_header_field f ON f.field_id = shv.field_id
WHERE  f.scope = 'Material';
GO

INSERT INTO qms_sample_header_value (sample_id, field_id, text_value, numeric_value, date_value)
SELECT s.sample_id, v.field_id, v.text_value, v.numeric_value, v.date_value
FROM   qms_sample s
JOIN   qms_qo_material_header_value v ON v.qo_material_id = s.qo_material_id
WHERE  s.is_deleted = 0;
GO

-- Sanity
SELECT 'material-scoped values copied onto samples' AS check_item, COUNT(*) AS value
FROM   qms_sample_header_value shv
JOIN   qms_sample_header_field f ON f.field_id = shv.field_id
WHERE  f.scope = 'Material';
GO
