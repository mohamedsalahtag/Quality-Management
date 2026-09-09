-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M28  -  the Labels screen
-- ============================================================================
--  Admin -> Labels renames any caption in the application. Its permissions are
--  discovered from the controller attributes at startup like every other
--  screen's, but qms_permission.screen_key carries a FOREIGN KEY to qms_screen,
--  so the screen row itself has to exist first -- discovery cannot invent it.
--
--  Without this row the whole reconciliation transaction fails on the FK, no
--  permissions are written, and (because the resolver is refreshed at the end
--  of that same transaction) the application authorises against an EMPTY
--  snapshot: every screen denies for everybody. So this migration is not
--  optional dressing, it is what keeps the security model loading at all.
--
--  Idempotent: safe to re-run.
-- ============================================================================

MERGE qms.qms_screen AS t
USING (VALUES
    -- Not levelled (supports_access_level = 0): there is nothing to read
    -- half-way here. Either an administrator may rename captions or they may
    -- not, and that is what the Admin.Labels.Edit action decides.
    ('Admin.Labels', N'Labels', 'Admin', 440, 0)
) AS s (screen_key, display_name, group_name, sort_order, supports_access_level)
    ON t.screen_key = s.screen_key
WHEN NOT MATCHED THEN
    INSERT (screen_key, display_name, group_name, sort_order, supports_access_level)
    VALUES (s.screen_key, s.display_name, s.group_name, s.sort_order, s.supports_access_level)
WHEN MATCHED THEN
    UPDATE SET display_name = s.display_name, group_name = s.group_name,
               sort_order = s.sort_order, supports_access_level = s.supports_access_level;
GO

SELECT 'labels_screen' AS check_item,
       CASE WHEN EXISTS (SELECT 1 FROM qms.qms_screen WHERE screen_key = 'Admin.Labels')
            THEN 'present' ELSE 'MISSING' END AS value
UNION ALL
SELECT 'admin_screens', CAST(COUNT(*) AS VARCHAR(20))
FROM   qms.qms_screen WHERE group_name = 'Admin';
GO
