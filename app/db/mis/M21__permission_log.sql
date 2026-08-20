-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M21  -  dedicated permission-change log
-- ============================================================================
--  Permission changes are recorded in qms_audit_log today, but not in a form
--  anybody can read. SecurityAdminService.SaveGrantsAsync writes the WHOLE
--  grant map as one JSON property called "grants", and the audit UI's differ
--  only descends one level -- so toggling one permission out of ~200 produces
--  a single diff row whose Old and New are two 4 KB JSON strings, truncated to
--  80 characters on screen. Worse, the map is serialised in dictionary order,
--  so a save that changed nothing still shows old != new (audit rows 22967-9
--  on 2026-08-04 are exactly this: identical content, different key order).
--
--  This table stores ONE ROW PER ACTUAL CHANGE: which permission, on which
--  role or user, from what level to what level, by whom, when. No JSON, no
--  diffing at read time, and a save that changes nothing writes nothing.
--
--  It also covers three things that had no audit row at all:
--    * role rename / activate / deactivate / plant-scoping (UpdateRoleAsync)
--    * the grant set of a DELETED role (previously irrecoverable)
--    * per-plant access grants and revocations, as individual rows
--
--  qms_audit_log is unchanged and still receives its rows -- this is an
--  additional, readable record, not a replacement.
--
--  Idempotent: safe to re-run.
-- ============================================================================

IF OBJECT_ID('qms.qms_permission_log', 'U') IS NULL
BEGIN
    CREATE TABLE qms.qms_permission_log (
        log_id        BIGINT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_qms_permission_log PRIMARY KEY,

        changed_at    DATETIME2(0)  NOT NULL
            CONSTRAINT DF_qms_permission_log_at DEFAULT SYSUTCDATETIME(),
        changed_by    NVARCHAR(80)  NOT NULL,

        -- Who or what the change was made TO.
        --   'Role' -> subject_key is the role code
        --   'User' -> subject_key is the username
        subject_type  VARCHAR(10)   NOT NULL,
        subject_key   NVARCHAR(100) NOT NULL,
        subject_name  NVARCHAR(200) NULL,        -- display name, denormalised for reading

        -- What KIND of change: 'Permission' | 'Plant' | 'RoleAssignment'
        --                    | 'RoleCreated' | 'RoleDeleted' | 'RoleSetting' | 'Account'
        change_type   VARCHAR(20)   NOT NULL,

        -- The thing that changed within that kind: a permission code, a plant
        -- code, or a role-setting field name. NULL for whole-subject changes.
        item_code     NVARCHAR(120) NULL,
        item_name     NVARCHAR(250) NULL,        -- human label for item_code

        -- Already human-readable ('None' / 'Read' / 'Edit', a plant name, a
        -- role display name). Never JSON -- that is the whole point.
        old_value     NVARCHAR(200) NULL,
        new_value     NVARCHAR(200) NULL,

        source_ip     VARCHAR(45)   NULL
    );

    -- The page lists newest-first and filters by subject; both are covered.
    CREATE INDEX IX_qms_permission_log_at
        ON qms.qms_permission_log(changed_at DESC)
        INCLUDE (subject_type, subject_key, change_type, changed_by);

    CREATE INDEX IX_qms_permission_log_subject
        ON qms.qms_permission_log(subject_type, subject_key, changed_at DESC);
END
GO

-- The app addresses tables by bare name (see M05), so it needs the dbo synonym.
IF NOT EXISTS (SELECT 1 FROM sys.synonyms WHERE name = 'qms_permission_log' AND schema_id = SCHEMA_ID('dbo'))
    CREATE SYNONYM dbo.qms_permission_log FOR qms.qms_permission_log;
GO

SELECT 'permission_log_rows' AS check_item, COUNT(*) AS value FROM qms.qms_permission_log
UNION ALL
SELECT 'dbo_synonym_present', COUNT(*) FROM sys.synonyms WHERE name = 'qms_permission_log';
GO
