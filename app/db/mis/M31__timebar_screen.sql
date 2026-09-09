-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M31  -  the Time Bar screen
-- ============================================================================
--  Time Bar lists every container SAP sent with the days between its arrival
--  and the moment QC finished, because the claim window is tight and a
--  container nobody has inspected is the one that costs money.
--
--  Its permissions are discovered from the controller attributes at startup
--  like every other screen's, but qms_permission.screen_key carries a FOREIGN
--  KEY to qms_screen, and discovery cannot invent the screen row. Without it
--  the whole reconciliation transaction fails on the FK, no permissions are
--  written, and -- because the resolver is refreshed at the END of that same
--  transaction -- the application authorises against an EMPTY snapshot: every
--  screen denies for everybody. So this migration is not dressing, it is what
--  keeps the security model loading. Apply it BEFORE deploying the binaries.
--
--  group_name 'Records' rather than 'Admin': _Layout computes "can this user
--  see the Admin menu" from Screens.AdminGroup, so filing it under Admin would
--  reveal an Admin dropdown containing one item to any manager granted the
--  page. Seed.AdminOnly on the controller is what makes it admin-by-default;
--  the group only decides where it renders.
--
--  supports_access_level = 0: the page is read-only, there is no half-way to
--  read it.
--
--  Idempotent: safe to re-run.
-- ============================================================================

MERGE qms.qms_screen AS t
USING (VALUES
    ('TimeBar', N'Time Bar', 'Records', 170, 0)
) AS s (screen_key, display_name, group_name, sort_order, supports_access_level)
    ON t.screen_key = s.screen_key
WHEN NOT MATCHED THEN
    INSERT (screen_key, display_name, group_name, sort_order, supports_access_level)
    VALUES (s.screen_key, s.display_name, s.group_name, s.sort_order, s.supports_access_level)
WHEN MATCHED THEN
    UPDATE SET display_name = s.display_name, group_name = s.group_name,
               sort_order = s.sort_order, supports_access_level = s.supports_access_level;
GO

SELECT 'timebar_screen' AS check_item,
       CASE WHEN EXISTS (SELECT 1 FROM qms.qms_screen WHERE screen_key = 'TimeBar')
            THEN 'present' ELSE 'MISSING' END AS value
UNION ALL
SELECT 'records_screens', CAST(COUNT(*) AS VARCHAR(20))
FROM   qms.qms_screen WHERE group_name = 'Records';
GO
