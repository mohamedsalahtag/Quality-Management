-- One-shot wipe of the SAP container cache so the next manual pull
-- repopulates it from scratch with the new "Container ne ''" filter.
-- Nothing else references qms_sap_container_cache, so TRUNCATE is safe
-- (and resets IDENTITY).
TRUNCATE TABLE qms_sap_container_cache;
SELECT COUNT(*) AS rows_after FROM qms_sap_container_cache;
