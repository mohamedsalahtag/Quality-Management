-- ============================================================
-- Alert + Email-Group Pack — Storage
-- ============================================================
-- Two tables:
--   AlertRules           — admin-configured triggers
--   EmailGroupAddresses  — addresses inside a group (with To/CC role)
--
-- Both reference an existing "groups" table (e.g. TechnicianGroups from
-- user-management-pack). Replace the FK target if your groups live elsewhere.
-- ============================================================

CREATE TABLE AlertRules (
    AlertId         INT IDENTITY(1,1) PRIMARY KEY,
    Name            NVARCHAR(150) NOT NULL,
    GroupId         INT NOT NULL REFERENCES TechnicianGroups(GroupId),
    -- Three CSV "axes" the data source matcher uses to filter records.
    -- Naming is generic — consumers map these to their own domain in the
    -- IAlertDataSource implementation. Empty string = no filter on that axis.
    PrimaryFilter   NVARCHAR(100) NOT NULL DEFAULT '',   -- e.g. "Passenger,Trucks"
    SecondaryFilter NVARCHAR(100) NOT NULL DEFAULT '',   -- e.g. "MVPI,Registration,OperationCards"
    Severities      NVARCHAR(30)  NOT NULL DEFAULT '',   -- "Red,Yellow"
    Subject         NVARCHAR(300) NOT NULL DEFAULT 'Expiry alert',
    Schedule        NVARCHAR(20)  NOT NULL DEFAULT 'Daily',  -- Daily | EveryN | Once
    ScheduleN       INT           NOT NULL DEFAULT 7,
    IsActive        BIT           NOT NULL DEFAULT 1,
    LastSentAt      DATETIME      NULL,
    LastSentDate    DATE          NULL,
    OnceSent        BIT           NOT NULL DEFAULT 0,
    CreatedAt       DATETIME      NOT NULL DEFAULT GETDATE(),
    CreatedBy       INT           NULL
);
CREATE INDEX IX_AlertRules_GroupId  ON AlertRules(GroupId);
CREATE INDEX IX_AlertRules_IsActive ON AlertRules(IsActive);

CREATE TABLE EmailGroupAddresses (
    AddressId    INT IDENTITY(1,1) PRIMARY KEY,
    GroupId      INT NOT NULL REFERENCES TechnicianGroups(GroupId) ON DELETE CASCADE,
    EmailAddress NVARCHAR(200) NOT NULL,
    Recipient    NVARCHAR(10)  NOT NULL DEFAULT 'To',  -- 'To' or 'CC'
    AddedAt      DATETIME      NOT NULL DEFAULT GETDATE(),
    AddedBy      INT           NULL,
    CONSTRAINT UQ_EmailGroupAddress UNIQUE (GroupId, EmailAddress)
);
CREATE INDEX IX_EmailGroupAddresses_GroupId ON EmailGroupAddresses(GroupId);
