-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M22  -  quality-order finish notification
-- ============================================================================
--  Who gets an email when a quality order is finished.
--
--  One row per chosen user. Deliberately a list of USERS rather than free-text
--  addresses: the address then follows the person's profile, so an address that
--  changes in AD does not silently keep mailing the old one, and a user who is
--  deactivated stops receiving reports without anyone remembering to edit a
--  list. The join at send time filters to active users with an address.
--
--  No on/off flag: an empty list means nobody is notified, which is the same
--  thing and one fewer state to keep consistent with the list itself.
--
--  Idempotent: safe to re-run.
-- ============================================================================

IF OBJECT_ID('qms.qms_qo_notify_recipient', 'U') IS NULL
BEGIN
    CREATE TABLE qms.qms_qo_notify_recipient (
        user_id   INT           NOT NULL,
        added_at  DATETIME2(0)  NOT NULL CONSTRAINT DF_qo_notify_added_at DEFAULT SYSUTCDATETIME(),
        added_by  NVARCHAR(100) NOT NULL,
        CONSTRAINT PK_qms_qo_notify_recipient PRIMARY KEY (user_id)
    );
END
GO

-- The app reaches every qms_* table through a dbo synonym (see M05), so the
-- same code runs against the dev database and this one.
IF NOT EXISTS (SELECT 1 FROM sys.synonyms WHERE name = 'qms_qo_notify_recipient')
    CREATE SYNONYM dbo.qms_qo_notify_recipient FOR qms.qms_qo_notify_recipient;
GO

SELECT 'qo_notify_recipients' AS check_item, COUNT(*) AS value FROM qms.qms_qo_notify_recipient
UNION ALL
SELECT 'dbo_synonym_present', COUNT(*) FROM sys.synonyms WHERE name = 'qms_qo_notify_recipient';
GO

-- ---------------------------------------------------------------------------
--  The screen the settings page hangs off.
-- ---------------------------------------------------------------------------
--  qms_permission.screen_key is a FOREIGN KEY to qms_screen, and the catalogue
--  the application reconciles at startup inserts permissions, not screens. A
--  new screen therefore has to be seeded here first, or that reconcile fails
--  its whole transaction -- silently, because it is caught so a bad catalogue
--  can never stop the site from booting.
--
--  supports_access_level = 0: this page is all-or-nothing. There is no useful
--  "read the recipient list but not change it" role.
MERGE qms.qms_screen AS t
USING (VALUES
    ('Parameters.Notifications', N'Notifications', 'Parameters', 380, 0)
) AS s (screen_key, display_name, group_name, sort_order, supports_access_level)
ON t.screen_key = s.screen_key
WHEN MATCHED THEN UPDATE SET
    t.display_name          = s.display_name,
    t.group_name            = s.group_name,
    t.sort_order            = s.sort_order,
    t.supports_access_level = s.supports_access_level
WHEN NOT MATCHED THEN
    INSERT (screen_key, display_name, group_name, sort_order, supports_access_level)
    VALUES (s.screen_key, s.display_name, s.group_name, s.sort_order, s.supports_access_level);
GO

SELECT 'notifications_screen' AS check_item, COUNT(*) AS value
FROM   qms.qms_screen WHERE screen_key = 'Parameters.Notifications';
GO
