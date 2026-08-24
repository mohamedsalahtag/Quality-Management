-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M23  -  per-recipient notification scope
-- ============================================================================
--  Who gets the "quality order finished" mail is already stored (M22). This
--  adds WHICH orders each of them cares about: any number of plants, and any
--  number of procurement types.
--
--  Child tables rather than columns because both are many-per-user, and a
--  comma-separated column would have to be parsed on every send.
--
--  EMPTY MEANS ALL. A recipient with no plant rows is notified for every
--  plant, which is exactly how the existing recipients behave today -- so this
--  migration cannot silently narrow anyone's notifications. Restricting is an
--  explicit act: tick the plants you want.
--
--  Idempotent: safe to re-run.
-- ============================================================================

IF OBJECT_ID('qms.qms_qo_notify_plant', 'U') IS NULL
BEGIN
    CREATE TABLE qms.qms_qo_notify_plant (
        user_id INT          NOT NULL,
        plant   VARCHAR(10)  NOT NULL,
        CONSTRAINT PK_qms_qo_notify_plant PRIMARY KEY (user_id, plant)
    );
END
GO

IF OBJECT_ID('qms.qms_qo_notify_potype', 'U') IS NULL
BEGIN
    CREATE TABLE qms.qms_qo_notify_potype (
        user_id INT          NOT NULL,
        po_type VARCHAR(20)  NOT NULL,
        CONSTRAINT PK_qms_qo_notify_potype PRIMARY KEY (user_id, po_type)
    );
END
GO

-- The app reaches every qms_* table through a dbo synonym (see M05).
IF NOT EXISTS (SELECT 1 FROM sys.synonyms WHERE name = 'qms_qo_notify_plant')
    CREATE SYNONYM dbo.qms_qo_notify_plant FOR qms.qms_qo_notify_plant;
GO
IF NOT EXISTS (SELECT 1 FROM sys.synonyms WHERE name = 'qms_qo_notify_potype')
    CREATE SYNONYM dbo.qms_qo_notify_potype FOR qms.qms_qo_notify_potype;
GO

SELECT 'notify_plant_rows'  AS check_item, COUNT(*) AS value FROM qms.qms_qo_notify_plant
UNION ALL
SELECT 'notify_potype_rows', COUNT(*) FROM qms.qms_qo_notify_potype
UNION ALL
SELECT 'synonyms_present',   COUNT(*) FROM sys.synonyms
       WHERE name IN ('qms_qo_notify_plant', 'qms_qo_notify_potype');
GO
