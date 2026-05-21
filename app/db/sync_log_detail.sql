SELECT TOP 5
  sync_log_id, endpoint_key, started_at, completed_at,
  success, rows_synced,
  CASE WHEN message IS NOT NULL THEN LEFT(message, 200) ELSE NULL END AS message_excerpt,
  triggered_by, trigger_source
FROM qms_sap_sync_log
ORDER BY sync_log_id DESC;
