-- ===================================================================
-- V45  Editable UI labels
--
-- Every button, grid header, field label and list caption is rendered
-- through ILabelService, which looks the text up here first. An
-- administrator renames anything from Admin -> Labels with no code
-- change and no deployment.
--
-- The key is the English text the code ships: the views stay readable,
-- there is no key registry to keep in step, and "reset to default" is
-- simply clearing custom_text.
--
-- Idempotent.
-- ===================================================================

IF OBJECT_ID(N'dbo.qms_ui_label') IS NULL
BEGIN
    CREATE TABLE dbo.qms_ui_label (
        -- BIN2: the lookup in ILabelService is StringComparer.Ordinal, and the
        -- views ship both "Created By" and "Created by". Under the server's
        -- default case-insensitive collation those collapse into one row.
        label_key    NVARCHAR(200)  COLLATE Latin1_General_BIN2 NOT NULL,
        screen_key   NVARCHAR(64)   NULL,
        custom_text  NVARCHAR(400)  NULL,
        last_seen_at DATETIME2      NOT NULL CONSTRAINT DF_qms_ui_label_seen DEFAULT SYSUTCDATETIME(),
        updated_at   DATETIME2      NULL,
        updated_by   NVARCHAR(256)  NULL,
        CONSTRAINT PK_qms_ui_label PRIMARY KEY (label_key)
    );
END;
GO

IF OBJECT_ID(N'dbo.qms_ui_label') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.qms_ui_label')
                 AND name = N'label_key'
                 AND collation_name <> N'Latin1_General_BIN2')
BEGIN
    IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = N'PK_qms_ui_label')
        ALTER TABLE dbo.qms_ui_label DROP CONSTRAINT PK_qms_ui_label;
    ALTER TABLE dbo.qms_ui_label
        ALTER COLUMN label_key NVARCHAR(200) COLLATE Latin1_General_BIN2 NOT NULL;
    ALTER TABLE dbo.qms_ui_label
        ADD CONSTRAINT PK_qms_ui_label PRIMARY KEY (label_key);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_qms_ui_label_screen')
    CREATE INDEX IX_qms_ui_label_screen ON dbo.qms_ui_label(screen_key) INCLUDE (custom_text);
GO
