-- Inspect the last few ContainerCache pull attempts.
SELECT TOP 10
    sync_log_id, started_at, completed_at, success, rows_synced,
    triggered_by, trigger_source,
    DATEDIFF(second, started_at, COALESCE(completed_at, SYSUTCDATETIME())) AS elapsed_seconds,
    LEFT(message, 200) AS message
FROM   qms_sap_sync_log
WHERE  endpoint_key = 'ContainerCache'
ORDER  BY started_at DESC;

SELECT (SELECT COUNT(*) FROM qms_sap_container_cache) AS total_cache_rows,
       (SELECT COUNT(*) FROM qms_sap_container_cache WHERE has_arrival = 0) AS pending_rows;
