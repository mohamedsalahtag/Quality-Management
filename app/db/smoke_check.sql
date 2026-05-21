SELECT
    (SELECT COUNT(*) FROM qms_arrival)               AS arrivals,
    (SELECT COUNT(*) FROM qms_arrival WHERE status_code='Completed') AS arrivals_completed,
    (SELECT COUNT(*) FROM qms_arrival_item)          AS arrival_items,
    (SELECT COUNT(*) FROM qms_arrival_checklist)     AS checklists,
    (SELECT COUNT(*) FROM qms_quality_order)         AS quality_orders,
    (SELECT COUNT(*) FROM qms_quality_order WHERE status_code='Open') AS qos_open,
    (SELECT COUNT(*) FROM qms_quality_order_material)AS qo_materials,
    (SELECT COUNT(*) FROM qms_sample WHERE is_deleted=0) AS samples,
    (SELECT COUNT(*) FROM qms_sample_reading)        AS readings,
    (SELECT COUNT(*) FROM qms_sample_defect)         AS defects,
    (SELECT COUNT(*) FROM qms_status_history)        AS status_history,
    (SELECT COUNT(*) FROM qms_report_log)            AS report_logs;
