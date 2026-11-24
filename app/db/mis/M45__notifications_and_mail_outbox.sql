-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M45  -  in-app notifications, mail outbox
-- ============================================================================
--  Survey management, stage 2. Two things QMS did not have:
--
--  qms_notification  The bell in the top bar. One row per recipient per event.
--                    Recipients are application users (portal.[User].UserId);
--                    an external committee member never has a row here.
--
--  qms_mail_outbox   Every survey e-mail, written in the SAME transaction as
--                    the event that causes it, then sent by a background
--                    worker. This is what makes a notification reliable: the
--                    business change never waits for the mail server, a
--                    message is never lost because the server was down, and a
--                    failure is recorded and retried instead of vanishing.
--                    It is also the delivery log: who was mailed, when, and
--                    what happened.
--
--  Both carry a dedupe key under a unique constraint, so an event that is
--  processed twice (a retry, a repeated click) notifies once.
--
--  Existing mail (the QC report to suppliers, the finish notice) is unchanged
--  and does not use the outbox.
--
--  Additive only. Idempotent: safe to re-run.
-- ============================================================================

IF OBJECT_ID(N'qms.qms_notification', N'U') IS NULL
BEGIN
    CREATE TABLE qms.qms_notification (
        notification_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_qms_notification PRIMARY KEY,
        user_id         INT           NOT NULL,
        event_code      VARCHAR(40)   NOT NULL,
        title           NVARCHAR(200) NOT NULL,
        body            NVARCHAR(500) NULL,
        -- Application-relative ("/Surveys/Details/12"). The page it leads to
        -- applies its own permission check; the link grants nothing.
        link_url        NVARCHAR(300) NULL,
        entity_type     VARCHAR(40)   NULL,
        entity_id       BIGINT        NULL,
        dedupe_key      VARCHAR(120)  NOT NULL,
        created_at      DATETIME2(0)  NOT NULL CONSTRAINT DF_qms_notification_created DEFAULT (SYSUTCDATETIME()),
        read_at         DATETIME2(0)  NULL,
        CONSTRAINT UQ_qms_notification_dedupe UNIQUE (user_id, dedupe_key)
    );
    -- The bell's two questions: how many unread, and the newest few.
    CREATE INDEX IX_qms_notification_user ON qms.qms_notification (user_id, read_at, created_at DESC);
    CREATE INDEX IX_qms_notification_entity ON qms.qms_notification (entity_type, entity_id);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.synonyms WHERE name = 'qms_notification' AND schema_id = SCHEMA_ID('dbo'))
    CREATE SYNONYM dbo.qms_notification FOR qms.qms_notification;
GO

IF OBJECT_ID(N'qms.qms_mail_outbox', N'U') IS NULL
BEGIN
    CREATE TABLE qms.qms_mail_outbox (
        outbox_id       BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_qms_mail_outbox PRIMARY KEY,
        event_code      VARCHAR(40)    NOT NULL,
        entity_type     VARCHAR(40)    NULL,
        entity_id       BIGINT         NULL,
        -- One message per recipient, so one address per row.
        to_address      NVARCHAR(256)  NOT NULL,
        to_name         NVARCHAR(200)  NULL,
        subject         NVARCHAR(300)  NOT NULL,
        body_html       NVARCHAR(MAX)  NOT NULL,
        status_code     VARCHAR(20)    NOT NULL CONSTRAINT DF_qms_mail_outbox_status DEFAULT ('Pending')
            CONSTRAINT CK_qms_mail_outbox_status CHECK (status_code IN ('Pending', 'Sent', 'Failed')),
        attempts        INT            NOT NULL CONSTRAINT DF_qms_mail_outbox_attempts DEFAULT (0),
        last_error      NVARCHAR(1000) NULL,
        dedupe_key      VARCHAR(200)   NOT NULL CONSTRAINT UQ_qms_mail_outbox_dedupe UNIQUE,
        created_at      DATETIME2(0)   NOT NULL CONSTRAINT DF_qms_mail_outbox_created DEFAULT (SYSUTCDATETIME()),
        -- The worker sends a Pending row once this time has passed. Each
        -- failed attempt pushes it further out; after the last one the row
        -- becomes Failed and waits for a person to press Resend.
        next_attempt_at DATETIME2(0)   NOT NULL CONSTRAINT DF_qms_mail_outbox_next DEFAULT (SYSUTCDATETIME()),
        sent_at         DATETIME2(0)   NULL
    );
    CREATE INDEX IX_qms_mail_outbox_due ON qms.qms_mail_outbox (status_code, next_attempt_at);
    CREATE INDEX IX_qms_mail_outbox_entity ON qms.qms_mail_outbox (entity_type, entity_id, created_at DESC);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.synonyms WHERE name = 'qms_mail_outbox' AND schema_id = SCHEMA_ID('dbo'))
    CREATE SYNONYM dbo.qms_mail_outbox FOR qms.qms_mail_outbox;
GO

SELECT 'qms_notification' AS check_item,
       CASE WHEN OBJECT_ID(N'qms.qms_notification', N'U') IS NOT NULL
             AND EXISTS (SELECT 1 FROM sys.synonyms WHERE name = 'qms_notification' AND schema_id = SCHEMA_ID('dbo'))
            THEN 'present' ELSE 'MISSING' END AS value
UNION ALL SELECT 'qms_mail_outbox',
       CASE WHEN OBJECT_ID(N'qms.qms_mail_outbox', N'U') IS NOT NULL
             AND EXISTS (SELECT 1 FROM sys.synonyms WHERE name = 'qms_mail_outbox' AND schema_id = SCHEMA_ID('dbo'))
            THEN 'present' ELSE 'MISSING' END;
GO
