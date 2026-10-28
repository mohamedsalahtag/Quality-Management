/* =====================================================================
   M41 -- Log of quality reports e-mailed to suppliers.

   Until now a send left only a line in the server log, so no screen could
   say whether a supplier had received the report. One row per successful
   send (a resend is a new row): who sent it, when, and to whom. The Quality
   Orders list and Details show the latest one as a green envelope.

   Deleting a quality order removes its send log with it (ON DELETE CASCADE);
   QoCascade also deletes explicitly, like qms_report_log.

   Purely additive. Idempotent: safe to re-run (the migrate tool has no
   version table). Apply with:
     dotnet run --project app\SharbatlyQMS.Migrate -- apply "<cs>" app\db\mis\M41__qo_report_send.sql
   ===================================================================== */

IF OBJECT_ID(N'qms.qms_qo_report_send', N'U') IS NULL
BEGIN
    CREATE TABLE qms.qms_qo_report_send (
        send_id          BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_qms_qo_report_send PRIMARY KEY,
        quality_order_id BIGINT        NOT NULL
            CONSTRAINT FK_qms_qo_report_send_qo REFERENCES qms.qms_quality_order (quality_order_id) ON DELETE CASCADE,
        sent_at          DATETIME2(0)  NOT NULL CONSTRAINT DF_qms_qo_report_send_at DEFAULT (SYSUTCDATETIME()),
        sent_by          NVARCHAR(256) NULL,
        to_addresses     NVARCHAR(1000) NULL,
        cc_addresses     NVARCHAR(1000) NULL,
        attachment_count INT           NOT NULL CONSTRAINT DF_qms_qo_report_send_att DEFAULT (1)
    );
    CREATE INDEX IX_qms_qo_report_send_qo ON qms.qms_qo_report_send (quality_order_id, sent_at DESC)
        INCLUDE (sent_by, to_addresses);
END;
GO

-- dbo synonym so the app's unqualified queries resolve (M05 convention).
IF NOT EXISTS (SELECT 1 FROM sys.synonyms WHERE name = 'qms_qo_report_send' AND schema_id = SCHEMA_ID('dbo'))
    CREATE SYNONYM dbo.qms_qo_report_send FOR qms.qms_qo_report_send;
GO

SELECT 'report send log' AS check_item,
       (SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID('qms.qms_qo_report_send'))
     + (SELECT COUNT(*) FROM sys.synonyms WHERE name = 'qms_qo_report_send' AND schema_id = SCHEMA_ID('dbo')) AS found_of_2;
