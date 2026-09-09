-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M27  -  editable UI labels
-- ============================================================================
--  Every button, grid header, field label and list caption in the application
--  is rendered through ILabelService, which looks the text up here before
--  printing it. An administrator can rename anything from Admin -> Labels
--  without a code change or a deployment.
--
--  The KEY is the English text the code ships. That is deliberate:
--
--    * a view stays readable -- @Loc["Container"] says what it prints, where
--      @Loc["arrivals.grid.col3"] would send every reader to a lookup table;
--    * there is no key registry to keep in step with the views, so a label can
--      never point at a key nobody defined;
--    * "reset to default" is just clearing custom_text -- the default is the
--      key itself and cannot drift.
--
--  The cost is that changing the English in code starts a new key. That is the
--  right trade here: the old row stays, the admin sees the new one, and nothing
--  silently keeps an override that no longer matches what the screen says.
--
--
--  The key column is BIN2-collated on purpose. SQL Server's default collation
--  is case-INSENSITIVE, but the lookup in ILabelService is StringComparer
--  .Ordinal, and the views really do ship both "Created By" and "Created by"
--  (12 such pairs). Under the default collation those two collapse into one
--  row, so renaming one would silently rename the other -- and the seed
--  migration cannot even be inserted. BIN2 makes the database agree with the
--  code about what "the same label" means.
--
--    * label_key    -- the shipped English text (also the natural key)
--    * screen_key   -- where it was first seen, for grouping the admin page
--    * custom_text  -- the administrator's replacement; NULL = use the key
--    * last_seen_at -- refreshed when a page renders it, so labels that no
--                      longer exist can be told apart from live ones
--
--  Idempotent: safe to re-run.
-- ============================================================================

IF OBJECT_ID('qms.qms_ui_label') IS NULL
BEGIN
    CREATE TABLE qms.qms_ui_label (
        label_key    NVARCHAR(200)  COLLATE Latin1_General_BIN2 NOT NULL,
        screen_key   NVARCHAR(64)   NULL,
        custom_text  NVARCHAR(400)  NULL,
        last_seen_at DATETIME2      NOT NULL CONSTRAINT DF_qms_ui_label_seen  DEFAULT SYSUTCDATETIME(),
        updated_at   DATETIME2      NULL,
        updated_by   NVARCHAR(256)  NULL,
        CONSTRAINT PK_qms_ui_label PRIMARY KEY (label_key)
    );
END;
GO

-- A table created before the collation was pinned still has the server default,
-- which silently merges the case-only pairs. Correct it in place: drop the key,
-- re-collate, put the key back.
IF OBJECT_ID('qms.qms_ui_label') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('qms.qms_ui_label')
                 AND name = 'label_key'
                 AND collation_name <> 'Latin1_General_BIN2')
BEGIN
    IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'PK_qms_ui_label')
        ALTER TABLE qms.qms_ui_label DROP CONSTRAINT PK_qms_ui_label;
    ALTER TABLE qms.qms_ui_label
        ALTER COLUMN label_key NVARCHAR(200) COLLATE Latin1_General_BIN2 NOT NULL;
    ALTER TABLE qms.qms_ui_label
        ADD CONSTRAINT PK_qms_ui_label PRIMARY KEY (label_key);
END;
GO

-- The app writes unqualified SQL ("FROM qms_ui_label"), which resolves through
-- SAP_User's default schema, dbo. The real table lives in [qms], so it has to
-- be surfaced as a dbo synonym exactly like every other QMS object (M05) --
-- without this the label lookup fails with "Invalid object name".
IF NOT EXISTS (SELECT 1 FROM sys.synonyms WHERE name = 'qms_ui_label' AND SCHEMA_NAME(schema_id) = 'dbo')
    CREATE SYNONYM dbo.qms_ui_label FOR qms.qms_ui_label;
GO

-- The admin page groups by screen; the renderer only ever reads the overridden
-- rows, which are the minority.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_qms_ui_label_screen')
    CREATE INDEX IX_qms_ui_label_screen ON qms.qms_ui_label(screen_key) INCLUDE (custom_text);
GO

SELECT 'ui_label_table' AS check_item,
       CASE WHEN OBJECT_ID('qms.qms_ui_label') IS NULL THEN 'MISSING' ELSE 'present' END AS value
UNION ALL
SELECT 'label_rows',      CAST(COUNT(*) AS VARCHAR(20)) FROM qms.qms_ui_label
UNION ALL
SELECT 'overridden_rows', CAST(COUNT(*) AS VARCHAR(20)) FROM qms.qms_ui_label WHERE custom_text IS NOT NULL;
GO
