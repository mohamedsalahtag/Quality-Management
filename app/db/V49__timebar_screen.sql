-- ===================================================================
-- V49  The Time Bar screen
--
-- Time Bar lists every container SAP sent with the days between its
-- arrival and the moment QC finished.
--
-- qms_permission.screen_key has a FOREIGN KEY to qms_screen and
-- permission discovery cannot invent the row, so this must be applied
-- before the code that declares Screens.TimeBar is deployed. Without it
-- the reconciliation transaction rolls back and the resolver refresh at
-- the end of it never runs, leaving an empty permission snapshot.
--
-- Idempotent, and a no-op on a database that predates the security
-- tables (the dev lineage still does).
-- ===================================================================

IF OBJECT_ID(N'dbo.qms_screen') IS NOT NULL
    MERGE dbo.qms_screen AS t
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
