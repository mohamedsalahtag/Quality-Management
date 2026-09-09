-- ===================================================================
-- V46  The Labels screen
--
-- Admin -> Labels renames any caption in the application. Its
-- permissions are discovered from the controller attributes at
-- startup, but qms_permission.screen_key has a FOREIGN KEY to
-- qms_screen, so the screen row must exist first -- discovery cannot
-- invent it. Without it the reconciliation transaction fails on the
-- FK, the resolver refresh at the end of that transaction never runs,
-- and the application authorises against an empty snapshot.
--
-- Idempotent, and a no-op on a database that predates the security
-- tables (the dev lineage still does).
-- ===================================================================

IF OBJECT_ID(N'dbo.qms_screen') IS NOT NULL
    MERGE dbo.qms_screen AS t
    USING (VALUES
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
