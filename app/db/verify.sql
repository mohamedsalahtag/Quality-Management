-- Sanity check after V03 seed.
SELECT
    (SELECT COUNT(*) FROM Users)                                                AS users_count,
    (SELECT TOP 1 Role FROM Users WHERE Username='admin')                       AS admin_role,
    (SELECT COUNT(*) FROM qms_defect_catalog)                                   AS defects,
    (SELECT COUNT(*) FROM qms_reading_type)                                     AS reading_types,
    (SELECT COUNT(*) FROM qms_material_group_defect WHERE major_category='Apples')  AS apple_defect_bindings,
    (SELECT COUNT(*) FROM qms_material_group_reading WHERE major_category='Apples') AS apple_reading_bindings,
    (SELECT COUNT(*) FROM SiteConfiguration)                                    AS site_config_keys,
    (SELECT COUNT(*) FROM EmailGroups)                                          AS email_groups;
