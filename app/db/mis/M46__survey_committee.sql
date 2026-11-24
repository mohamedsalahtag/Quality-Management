-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M46  -  Survey management, stage 3
-- ============================================================================
--  The committee and what it does once a survey is CONFIRMED: who is on it,
--  when the surveyor is due, who turned up, and the QC officer's confirmation
--  that the shipment really was inspected.
--
--  None of this is a survey status. A confirmed survey stays "Confirm" through
--  scheduling, attendance and inspection; these are facts recorded about it.
--
--  Configuration
--    qms_survey_committee          one committee per branch, plus a default
--                                  (plant NULL) for branches without their own
--    qms_survey_committee_member   internal (an application user) or external
--                                  (typed name, organisation, e-mail, phone)
--
--  Per survey
--    qms_survey (new columns)      which committee was assigned, and the
--                                  inspection confirmation
--    qms_survey_member             the members COPIED onto the survey when it
--                                  is confirmed, so a later change to the
--                                  configuration never rewrites history
--    qms_survey_appointment        one row per appointment; a reschedule adds
--                                  a row and marks the old one superseded, so
--                                  the rows ARE the appointment history. Each
--                                  carries the attendance of that visit.
--    qms_survey_attendance         per member per appointment: NULL = not
--                                  recorded, 0 = absent, 1 = present
--
--  Attachments: qms_document accepts owner type 'Survey'. Survey files are
--  served only through the Surveys controller, never the general one.
--
--  The screen row must exist BEFORE the binaries are deployed (see M31).
--  Additive only. Idempotent: safe to re-run.
-- ============================================================================

IF OBJECT_ID(N'qms.qms_survey_committee', N'U') IS NULL
BEGIN
    CREATE TABLE qms.qms_survey_committee (
        committee_id   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_qms_survey_committee PRIMARY KEY,
        committee_name NVARCHAR(120) NOT NULL,
        description    NVARCHAR(500) NULL,
        -- NULL = the default committee, used by every branch with none of its own.
        plant          VARCHAR(4)    NULL,
        -- So "one active committee per branch, and one active default" can be
        -- a single unique index (a filtered index cannot key on NULL).
        plant_key      AS ISNULL(plant, '*') PERSISTED,
        is_active      BIT           NOT NULL CONSTRAINT DF_qms_survey_committee_active DEFAULT (1),
        created_at     DATETIME2(0)  NOT NULL CONSTRAINT DF_qms_survey_committee_created DEFAULT (SYSUTCDATETIME()),
        created_by     NVARCHAR(80)  NOT NULL,
        updated_at     DATETIME2(0)  NOT NULL CONSTRAINT DF_qms_survey_committee_updated DEFAULT (SYSUTCDATETIME()),
        updated_by     NVARCHAR(80)  NOT NULL
    );
    CREATE UNIQUE INDEX UX_qms_survey_committee_plant
        ON qms.qms_survey_committee (plant_key) WHERE is_active = 1;
END;
GO

IF OBJECT_ID(N'qms.qms_survey_committee_member', N'U') IS NULL
BEGIN
    CREATE TABLE qms.qms_survey_committee_member (
        member_id           INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_qms_survey_committee_member PRIMARY KEY,
        committee_id        INT           NOT NULL
            CONSTRAINT FK_qms_survey_committee_member_committee
            REFERENCES qms.qms_survey_committee (committee_id) ON DELETE CASCADE,
        participant_type    VARCHAR(10)   NOT NULL
            CONSTRAINT CK_qms_survey_committee_member_type CHECK (participant_type IN ('Internal', 'External')),
        -- portal.[User].UserId for an internal member. No foreign key: the
        -- portal schema belongs to another application.
        user_id             INT           NULL,
        member_name         NVARCHAR(120) NOT NULL,
        department          NVARCHAR(120) NULL,
        organisation        NVARCHAR(120) NULL,
        committee_role      VARCHAR(20)   NOT NULL
            CONSTRAINT CK_qms_survey_committee_member_role
            CHECK (committee_role IN ('Insurance', 'Claims', 'QC', 'Surveyor', 'Other')),
        email               NVARCHAR(256) NULL,
        phone               NVARCHAR(40)  NULL,
        notify_in_app       BIT           NOT NULL CONSTRAINT DF_qms_survey_committee_member_bell DEFAULT (1),
        notify_email        BIT           NOT NULL CONSTRAINT DF_qms_survey_committee_member_mail DEFAULT (1),
        expected_attendance BIT           NOT NULL CONSTRAINT DF_qms_survey_committee_member_exp DEFAULT (1),
        is_active           BIT           NOT NULL CONSTRAINT DF_qms_survey_committee_member_active DEFAULT (1),
        sort_order          INT           NOT NULL CONSTRAINT DF_qms_survey_committee_member_sort DEFAULT (0),
        -- An internal member is a user; an external one is not.
        CONSTRAINT CK_qms_survey_committee_member_user CHECK
            ((participant_type = 'Internal' AND user_id IS NOT NULL)
          OR (participant_type = 'External' AND user_id IS NULL))
    );
    CREATE INDEX IX_qms_survey_committee_member_committee
        ON qms.qms_survey_committee_member (committee_id, sort_order, member_id);
END;
GO

-- ---- The survey: assigned committee and inspection confirmation ----
IF COL_LENGTH('qms.qms_survey', 'committee_id') IS NULL
    ALTER TABLE qms.qms_survey ADD
        committee_id            INT            NULL,
        committee_name          NVARCHAR(120)  NULL,
        committee_assigned_at   DATETIME2(0)   NULL,
        committee_assigned_by   NVARCHAR(80)   NULL,
        surveyor_attended       BIT            NULL,
        shipment_inspected      BIT            NULL,
        inspection_date         DATE           NULL,
        inspection_start_time   TIME(0)        NULL,
        inspection_end_time     TIME(0)        NULL,
        inspection_observations NVARCHAR(2000) NULL,
        inspection_findings     NVARCHAR(2000) NULL,
        inspection_damage       NVARCHAR(2000) NULL,
        inspection_comments     NVARCHAR(2000) NULL,
        inspection_confirmed_at DATETIME2(0)   NULL,
        inspection_confirmed_by NVARCHAR(80)   NULL;
GO

IF OBJECT_ID(N'qms.qms_survey_member', N'U') IS NULL
BEGIN
    CREATE TABLE qms.qms_survey_member (
        survey_member_id    BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_qms_survey_member PRIMARY KEY,
        survey_id           BIGINT        NOT NULL
            CONSTRAINT FK_qms_survey_member_survey REFERENCES qms.qms_survey (survey_id) ON DELETE CASCADE,
        -- Where the copy came from. Deliberately not a foreign key: the
        -- configured member may be edited or removed; this row may not change.
        source_member_id    INT           NULL,
        participant_type    VARCHAR(10)   NOT NULL,
        user_id             INT           NULL,
        member_name         NVARCHAR(120) NOT NULL,
        department          NVARCHAR(120) NULL,
        organisation        NVARCHAR(120) NULL,
        committee_role      VARCHAR(20)   NOT NULL,
        email               NVARCHAR(256) NULL,
        phone               NVARCHAR(40)  NULL,
        notify_in_app       BIT           NOT NULL,
        notify_email        BIT           NOT NULL,
        expected_attendance BIT           NOT NULL,
        sort_order          INT           NOT NULL CONSTRAINT DF_qms_survey_member_sort DEFAULT (0)
    );
    CREATE INDEX IX_qms_survey_member_survey ON qms.qms_survey_member (survey_id, sort_order, survey_member_id);
END;
GO

IF OBJECT_ID(N'qms.qms_survey_appointment', N'U') IS NULL
BEGIN
    CREATE TABLE qms.qms_survey_appointment (
        appointment_id          BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_qms_survey_appointment PRIMARY KEY,
        survey_id               BIGINT         NOT NULL
            CONSTRAINT FK_qms_survey_appointment_survey REFERENCES qms.qms_survey (survey_id) ON DELETE CASCADE,
        seq_no                  INT            NOT NULL,
        -- Local wall-clock time, as typed: an appointment is "Tuesday at 10",
        -- not an instant to be converted.
        planned_at              DATETIME2(0)   NOT NULL,
        location                NVARCHAR(200)  NULL,
        survey_company          NVARCHAR(120)  NULL,
        surveyor_name           NVARCHAR(120)  NULL,
        surveyor_phone          NVARCHAR(40)   NULL,
        surveyor_email          NVARCHAR(256)  NULL,
        insurance_ref           NVARCHAR(80)   NULL,
        -- instructions may be sent to participants, external ones included;
        -- internal_comments never leave the application.
        instructions            NVARCHAR(1000) NULL,
        internal_comments       NVARCHAR(1000) NULL,
        schedule_confirmed_at   DATETIME2(0)   NULL,
        schedule_confirmed_by   NVARCHAR(80)   NULL,
        -- Set when the appointment is rescheduled; the replacement is a new row.
        superseded_at           DATETIME2(0)   NULL,
        superseded_by           NVARCHAR(80)   NULL,
        supersede_reason        NVARCHAR(500)  NULL,
        -- What actually happened at THIS appointment.
        actual_at               DATETIME2(0)   NULL,
        actual_location         NVARCHAR(200)  NULL,
        surveyor_attended       BIT            NULL,
        actual_surveyor_name    NVARCHAR(120)  NULL,
        attendance_details      NVARCHAR(1000) NULL,
        attendance_comments     NVARCHAR(1000) NULL,
        attendance_entered_at   DATETIME2(0)   NULL,
        attendance_entered_by   NVARCHAR(80)   NULL,
        attendance_confirmed_at DATETIME2(0)   NULL,
        attendance_confirmed_by NVARCHAR(80)   NULL,
        created_at              DATETIME2(0)   NOT NULL CONSTRAINT DF_qms_survey_appointment_created DEFAULT (SYSUTCDATETIME()),
        created_by              NVARCHAR(80)   NOT NULL,
        updated_at              DATETIME2(0)   NOT NULL CONSTRAINT DF_qms_survey_appointment_updated DEFAULT (SYSUTCDATETIME()),
        updated_by              NVARCHAR(80)   NOT NULL,
        CONSTRAINT UQ_qms_survey_appointment_seq UNIQUE (survey_id, seq_no)
    );
    -- At most one appointment that still stands, per survey.
    CREATE UNIQUE INDEX UX_qms_survey_appointment_current
        ON qms.qms_survey_appointment (survey_id) WHERE superseded_at IS NULL;
END;
GO

IF OBJECT_ID(N'qms.qms_survey_attendance', N'U') IS NULL
BEGIN
    CREATE TABLE qms.qms_survey_attendance (
        attendance_id    BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_qms_survey_attendance PRIMARY KEY,
        appointment_id   BIGINT        NOT NULL
            CONSTRAINT FK_qms_survey_attendance_appointment
            REFERENCES qms.qms_survey_appointment (appointment_id) ON DELETE CASCADE,
        -- NO ACTION: the survey already cascades to this table through the
        -- appointment, and SQL Server refuses a second cascade path.
        survey_member_id BIGINT        NOT NULL
            CONSTRAINT FK_qms_survey_attendance_member REFERENCES qms.qms_survey_member (survey_member_id),
        -- NULL = not recorded. That is different from 0 = recorded as absent.
        attended         BIT           NULL,
        attended_at      DATETIME2(0)  NULL,
        comments         NVARCHAR(500) NULL,
        CONSTRAINT UQ_qms_survey_attendance UNIQUE (appointment_id, survey_member_id)
    );
END;
GO

-- ---- Synonyms ----
DECLARE @t sysname, @sql nvarchar(400);
DECLARE c CURSOR LOCAL FAST_FORWARD FOR
    SELECT v FROM (VALUES ('qms_survey_committee'), ('qms_survey_committee_member'), ('qms_survey_member'),
                          ('qms_survey_appointment'), ('qms_survey_attendance')) AS x(v);
OPEN c;
FETCH NEXT FROM c INTO @t;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.synonyms WHERE name = @t AND schema_id = SCHEMA_ID('dbo'))
    BEGIN
        SET @sql = N'CREATE SYNONYM dbo.' + QUOTENAME(@t) + N' FOR qms.' + QUOTENAME(@t) + N';';
        EXEC sys.sp_executesql @sql;
    END;
    FETCH NEXT FROM c INTO @t;
END;
CLOSE c; DEALLOCATE c;
GO

-- ---- Attachments: a survey can own documents ----
IF EXISTS (SELECT 1 FROM sys.check_constraints
           WHERE name = 'CK_qms_document_owner_type' AND parent_object_id = OBJECT_ID('qms.qms_document')
             AND definition NOT LIKE '%Survey%')
BEGIN
    ALTER TABLE qms.qms_document DROP CONSTRAINT CK_qms_document_owner_type;
    ALTER TABLE qms.qms_document WITH CHECK ADD CONSTRAINT CK_qms_document_owner_type
        CHECK (owner_type IN ('Sample', 'QualityOrder', 'Arrival', 'Survey'));
END;
GO

MERGE qms.qms_screen AS t
USING (VALUES
    ('Parameters.SurveyCommittees', N'Survey committees', 'Parameters', 395, 0)
) AS s (screen_key, display_name, group_name, sort_order, supports_access_level)
    ON t.screen_key = s.screen_key
WHEN NOT MATCHED THEN
    INSERT (screen_key, display_name, group_name, sort_order, supports_access_level)
    VALUES (s.screen_key, s.display_name, s.group_name, s.sort_order, s.supports_access_level)
WHEN MATCHED THEN
    UPDATE SET display_name = s.display_name, group_name = s.group_name,
               sort_order = s.sort_order, supports_access_level = s.supports_access_level;
GO

SELECT x.v AS check_item,
       CASE WHEN OBJECT_ID(N'qms.' + x.v, N'U') IS NOT NULL
             AND EXISTS (SELECT 1 FROM sys.synonyms WHERE name = x.v AND schema_id = SCHEMA_ID('dbo'))
            THEN 'present' ELSE 'MISSING' END AS value
FROM (VALUES ('qms_survey_committee'), ('qms_survey_committee_member'), ('qms_survey_member'),
             ('qms_survey_appointment'), ('qms_survey_attendance')) AS x(v)
UNION ALL SELECT 'qms_survey.committee_id',
       CASE WHEN COL_LENGTH('qms.qms_survey', 'committee_id') IS NOT NULL THEN 'present' ELSE 'MISSING' END
UNION ALL SELECT 'qms_survey.inspection_confirmed_at',
       CASE WHEN COL_LENGTH('qms.qms_survey', 'inspection_confirmed_at') IS NOT NULL THEN 'present' ELSE 'MISSING' END
UNION ALL SELECT 'document owner Survey',
       CASE WHEN EXISTS (SELECT 1 FROM sys.check_constraints
                         WHERE name = 'CK_qms_document_owner_type' AND definition LIKE '%Survey%')
            THEN 'present' ELSE 'MISSING' END
UNION ALL SELECT 'Survey committees screen',
       CASE WHEN EXISTS (SELECT 1 FROM qms.qms_screen WHERE screen_key = 'Parameters.SurveyCommittees')
            THEN 'present' ELSE 'MISSING' END;
GO
