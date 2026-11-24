/* =====================================================================
   V59 -- Survey management, stage 1 (core). V-SERIES TWIN of
   app\db\mis\M44__survey_core.sql.

   The live system runs the M-series against Sharbatly_MIS (schema [qms]
   behind dbo synonyms). This file exists only so the older private
   SharbatlyQMS database can be brought level; that database is retired, so
   this is unapplied unless it is revived. Idempotent.
   ===================================================================== */

IF OBJECT_ID('seq_qms_survey_no') IS NULL
    CREATE SEQUENCE seq_qms_survey_no AS int START WITH 1 INCREMENT BY 1;
GO

IF OBJECT_ID(N'qms_survey', N'U') IS NULL
BEGIN
    CREATE TABLE qms_survey (
        survey_id          BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_qms_survey PRIMARY KEY,
        survey_no          VARCHAR(20)    NOT NULL CONSTRAINT UQ_qms_survey_no UNIQUE,
        arrival_id         BIGINT         NOT NULL
            CONSTRAINT FK_qms_survey_arrival REFERENCES qms_arrival (arrival_id),
        source_code        VARCHAR(20)    NOT NULL
            CONSTRAINT CK_qms_survey_source CHECK (source_code IN ('Arrival', 'QualityOrder')),
        source_qo_id       BIGINT         NULL
            CONSTRAINT FK_qms_survey_source_qo REFERENCES qms_quality_order (quality_order_id),
        -- 'RuleBased' is reserved for a later release; only 'Manual' is written today.
        method_code        VARCHAR(20)    NOT NULL CONSTRAINT DF_qms_survey_method DEFAULT ('Manual')
            CONSTRAINT CK_qms_survey_method CHECK (method_code IN ('Manual', 'RuleBased')),
        -- Exactly three. Scheduling, attendance and inspection are facts about a
        -- confirmed survey, never statuses of their own.
        status_code        VARCHAR(20)    NOT NULL CONSTRAINT DF_qms_survey_status DEFAULT ('Open')
            CONSTRAINT CK_qms_survey_status CHECK (status_code IN ('Open', 'Confirm', 'Pass')),
        request_reason     NVARCHAR(500)  NOT NULL,
        request_comments   NVARCHAR(2000) NULL,
        requested_at       DATETIME2(0)   NOT NULL CONSTRAINT DF_qms_survey_requested_at DEFAULT (SYSUTCDATETIME()),
        requested_by       NVARCHAR(80)   NOT NULL,
        decided_at         DATETIME2(0)   NULL,
        decided_by         NVARCHAR(80)   NULL,
        decision_comments  NVARCHAR(2000) NULL,
        -- A follow-up step (creating the quality order, raising the claim
        -- request) that failed after the decision was saved. The decision
        -- stands; this is what the Retry button clears.
        followup_error     NVARCHAR(1000) NULL,
        followup_failed_at DATETIME2(0)   NULL,
        updated_at         DATETIME2(0)   NOT NULL CONSTRAINT DF_qms_survey_updated_at DEFAULT (SYSUTCDATETIME()),
        updated_by         NVARCHAR(80)   NOT NULL,
        CONSTRAINT UQ_qms_survey_arrival UNIQUE (arrival_id)
    );
    CREATE INDEX IX_qms_survey_status ON qms_survey (status_code, requested_at DESC);
END;
GO

-- supports_access_level = 0: what a user may DO on a survey is decided action
-- by action (request, decide, ...), not by a read-only / edit switch. It also
-- means Surveys.Request works for an inspector who cannot open the page.
MERGE qms_screen AS t
USING (VALUES
    ('Surveys', N'Surveys', 'Records', 165, 0)
) AS s (screen_key, display_name, group_name, sort_order, supports_access_level)
    ON t.screen_key = s.screen_key
WHEN NOT MATCHED THEN
    INSERT (screen_key, display_name, group_name, sort_order, supports_access_level)
    VALUES (s.screen_key, s.display_name, s.group_name, s.sort_order, s.supports_access_level)
WHEN MATCHED THEN
    UPDATE SET display_name = s.display_name, group_name = s.group_name,
               sort_order = s.sort_order, supports_access_level = s.supports_access_level;
GO

SELECT 'qms_survey table' AS check_item,
       CASE WHEN OBJECT_ID(N'qms_survey', N'U') IS NOT NULL THEN 'present' ELSE 'MISSING' END AS value
UNION ALL SELECT 'seq_qms_survey_no',
       CASE WHEN OBJECT_ID('seq_qms_survey_no') IS NOT NULL THEN 'present' ELSE 'MISSING' END
UNION ALL SELECT 'Surveys screen',
       CASE WHEN EXISTS (SELECT 1 FROM qms_screen WHERE screen_key = 'Surveys') THEN 'present' ELSE 'MISSING' END;
GO
