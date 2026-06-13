SELECT (SELECT COUNT(*) FROM qms_sap_container_cache) AS total_cache_rows,
       (SELECT COUNT(*) FROM qms_sap_container_cache WHERE has_arrival = 0) AS pending_lines,
       (SELECT COUNT(*) FROM (SELECT DISTINCT container_no, bol_no, ebeln
                              FROM qms_sap_container_cache
                              WHERE has_arrival = 0) x) AS pending_triplets;
