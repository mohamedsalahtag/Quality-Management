/* =====================================================================
   V56 -- Log of quality reports e-mailed to suppliers. V-SERIES TWIN of
   app\db\mis\M41__qo_report_send.sql.

   The live system runs the M-series against Sharbatly_MIS (schema [qms]
   behind dbo synonyms). This file exists only so the older private
   SharbatlyQMS database can be brought level; that database is retired, so
   this is unapplied unless it is revived. Idempotent.
   ===================================================================== */

IF OBJECT_ID(N'qms_qo_report_send', N'U') IS NULL
BEGIN
    CREATE TABLE qms_qo_report_send (
        send_id          BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_qms_qo_report_send PRIMARY KEY,
        quality_order_id BIGINT        NOT NULL
            CONSTRAINT FK_qms_qo_report_send_qo REFERENCES qms_quality_order (quality_order_id) ON DELETE CASCADE,
        sent_at          DATETIME2(0)  NOT NULL CONSTRAINT DF_qms_qo_report_send_at DEFAULT (SYSUTCDATETIME()),
        sent_by          NVARCHAR(256) NULL,
        to_addresses     NVARCHAR(1000) NULL,
        cc_addresses     NVARCHAR(1000) NULL,
        attachment_count INT           NOT NULL CONSTRAINT DF_qms_qo_report_send_att DEFAULT (1)
    );
    CREATE INDEX IX_qms_qo_report_send_qo ON qms_qo_report_send (quality_order_id, sent_at DESC)
        INCLUDE (sent_by, to_addresses);
END;
GO
