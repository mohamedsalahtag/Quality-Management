SELECT
    (SELECT COUNT(*) FROM qms_sap_container_cache)                                AS total_rows,
    (SELECT COUNT(*) FROM qms_sap_container_cache WHERE po_type IS NOT NULL
                                                    AND po_type <> '')           AS with_po_type,
    (SELECT COUNT(*) FROM qms_sap_container_cache WHERE po_type IS NULL
                                                     OR po_type = '')            AS without_po_type;

SELECT TOP 5 container_no, bol_no, ebeln, po_type
FROM   qms_sap_container_cache
ORDER  BY last_seen_at DESC;
