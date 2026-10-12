-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M40  -  the Defect reports screen
-- ============================================================================
--  Reports -> Defect reports: the five defect pivots the QC team kept in Excel
--  (per week, per defect & class, per branch & defect, per sub-major, per
--  major) as tabs on one page, with drill-down to the orders behind a number.
--
--  The screen row must exist BEFORE the binaries are deployed: permissions are
--  discovered from the controller attributes at startup, and qms_permission
--  carries a foreign key to qms_screen (see M31 for what happens without it).
--
--  supports_access_level = 0: the page is read-only.
--  Idempotent: safe to re-run.
-- ============================================================================

MERGE qms.qms_screen AS t
USING (VALUES
    ('Reports.Defects', N'Defect reports', 'Reports', 205, 0)
) AS s (screen_key, display_name, group_name, sort_order, supports_access_level)
    ON t.screen_key = s.screen_key
WHEN NOT MATCHED THEN
    INSERT (screen_key, display_name, group_name, sort_order, supports_access_level)
    VALUES (s.screen_key, s.display_name, s.group_name, s.sort_order, s.supports_access_level)
WHEN MATCHED THEN
    UPDATE SET display_name = s.display_name, group_name = s.group_name,
               sort_order = s.sort_order, supports_access_level = s.supports_access_level;
GO

SELECT 'defect_reports_screen' AS check_item,
       CASE WHEN EXISTS (SELECT 1 FROM qms.qms_screen WHERE screen_key = 'Reports.Defects')
            THEN 'present' ELSE 'MISSING' END AS value;
GO
