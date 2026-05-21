-- ============================================================================
--  SharbatlyQMS  -  V01  -  Pack-derived schema (auth, email, alerts)
-- ============================================================================
--  Adapted from user-management-pack, email-notification-pack, alert-pack.
--  Differences vs. the original packs:
--    * AD-related fields and config keys removed (QMS uses local accounts).
--    * Role set is QMS-specific: QCStaff | QCManager | SiteAdmin.
--    * TechnicianGroups -> EmailGroups (QMS uses these only for email fan-out
--      of alerts; ticket-permission columns kept but unused for now).
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 1) Site-wide key/value configuration (referenced by SMTP, alerts, QMS)
-- ---------------------------------------------------------------------------
CREATE TABLE SiteConfiguration (
    ConfigId    INT IDENTITY(1,1) PRIMARY KEY,
    ConfigKey   NVARCHAR(100) NOT NULL UNIQUE,
    ConfigValue NVARCHAR(MAX) NULL,
    UpdatedAt   DATETIME2 NULL,
    UpdatedBy   INT NULL
);

-- ---------------------------------------------------------------------------
-- 2) Users (3 QMS roles, local BCrypt only)
-- ---------------------------------------------------------------------------
CREATE TABLE Users (
    UserId          INT IDENTITY(1,1) PRIMARY KEY,
    EmployeeId      NVARCHAR(50)  NULL,
    Username        NVARCHAR(100) NOT NULL UNIQUE,           -- login key
    FullName        NVARCHAR(200) NOT NULL,
    Email           NVARCHAR(200) NULL,
    Department      NVARCHAR(200) NULL,
    ProfilePicture  NVARCHAR(300) NULL,                      -- /avatars/{userId}.{ext}
    PasswordHash    NVARCHAR(500) NOT NULL,                  -- BCrypt
    Role            NVARCHAR(50)  NOT NULL DEFAULT 'QCStaff',
        -- Allowed: QCStaff | QCManager | SiteAdmin
    IsActive        BIT      NOT NULL DEFAULT 1,
    IsOnline        BIT      NOT NULL DEFAULT 0,
    LastLogin       DATETIME2 NULL,
    LastSeen        DATETIME2 NULL,
    CreatedAt       DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CreatedBy       INT NULL REFERENCES Users(UserId),
    DisabledAt      DATETIME2 NULL,
    DisabledBy      INT NULL REFERENCES Users(UserId),
    CONSTRAINT CK_Users_Role CHECK (Role IN ('QCStaff','QCManager','SiteAdmin'))
);

-- ---------------------------------------------------------------------------
-- 3) Email groups (used by alerts for fan-out and by per-group SMTP override)
-- ---------------------------------------------------------------------------
CREATE TABLE EmailGroups (
    GroupId     INT IDENTITY(1,1) PRIMARY KEY,
    GroupName   NVARCHAR(100) NOT NULL,
    ShortCode   NVARCHAR(10)  NOT NULL,
    Description NVARCHAR(500) NULL,
    IsActive    BIT NOT NULL DEFAULT 1,
    CreatedAt   DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE EmailGroupMembers (
    MemberId        INT IDENTITY(1,1) PRIMARY KEY,
    UserId          INT NOT NULL REFERENCES Users(UserId),
    GroupId         INT NOT NULL REFERENCES EmailGroups(GroupId),
    AssignedAt      DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_EmailGroupMember UNIQUE (UserId, GroupId)
);

CREATE TABLE EmailGroupAddresses (
    AddressId    INT IDENTITY(1,1) PRIMARY KEY,
    GroupId      INT NOT NULL REFERENCES EmailGroups(GroupId) ON DELETE CASCADE,
    EmailAddress NVARCHAR(200) NOT NULL,
    Recipient    NVARCHAR(10)  NOT NULL DEFAULT 'To',     -- 'To' or 'CC'
    AddedAt      DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    AddedBy      INT NULL,
    CONSTRAINT UQ_EmailGroupAddress UNIQUE (GroupId, EmailAddress),
    CONSTRAINT CK_EmailGroupAddress_Recipient CHECK (Recipient IN ('To','CC'))
);
CREATE INDEX IX_EmailGroupAddresses_GroupId ON EmailGroupAddresses(GroupId);

-- ---------------------------------------------------------------------------
-- 4) Per-group SMTP override (from email-notification-pack)
-- ---------------------------------------------------------------------------
CREATE TABLE GroupMailConfig (
    ConfigId      INT IDENTITY(1,1) PRIMARY KEY,
    GroupId       INT NOT NULL UNIQUE REFERENCES EmailGroups(GroupId),
    SmtpHost      NVARCHAR(200) NOT NULL,
    SmtpPort      INT NOT NULL DEFAULT 587,
    SmtpUser      NVARCHAR(200) NOT NULL,
    SmtpPassword  NVARCHAR(500) NOT NULL,
    SmtpFromEmail NVARCHAR(200) NOT NULL,
    SmtpFromName  NVARCHAR(200) NOT NULL,
    SmtpEnableSsl BIT NOT NULL DEFAULT 1,
    IsEnabled     BIT NOT NULL DEFAULT 0,
    UpdatedAt     DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

-- ---------------------------------------------------------------------------
-- 5) Alert rules (from alert-pack)
-- ---------------------------------------------------------------------------
CREATE TABLE AlertRules (
    AlertId         INT IDENTITY(1,1) PRIMARY KEY,
    Name            NVARCHAR(150) NOT NULL,
    GroupId         INT NOT NULL REFERENCES EmailGroups(GroupId),
    PrimaryFilter   NVARCHAR(100) NOT NULL DEFAULT '',     -- e.g. "Apple,Citrus"
    SecondaryFilter NVARCHAR(100) NOT NULL DEFAULT '',     -- e.g. "StaleArrival,OpenQO,DefectThreshold"
    Severities      NVARCHAR(30)  NOT NULL DEFAULT '',     -- "Red,Yellow"
    Subject         NVARCHAR(300) NOT NULL DEFAULT 'QMS alert',
    Schedule        NVARCHAR(20)  NOT NULL DEFAULT 'Daily',-- Daily | EveryN | Once
    ScheduleN       INT           NOT NULL DEFAULT 7,
    IsActive        BIT           NOT NULL DEFAULT 1,
    LastSentAt      DATETIME2     NULL,
    LastSentDate    DATE          NULL,
    OnceSent        BIT           NOT NULL DEFAULT 0,
    CreatedAt       DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    CreatedBy       INT NULL,
    CONSTRAINT CK_AlertRules_Schedule CHECK (Schedule IN ('Daily','EveryN','Once'))
);
CREATE INDEX IX_AlertRules_GroupId  ON AlertRules(GroupId);
CREATE INDEX IX_AlertRules_IsActive ON AlertRules(IsActive);
