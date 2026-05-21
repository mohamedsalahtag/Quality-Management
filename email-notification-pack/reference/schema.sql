-- ============================================================
-- Email Notification Pack - Storage
-- ============================================================
-- Two storage surfaces:
--   1) SiteConfiguration  - global SMTP keys (key/value rows)
--   2) GroupMailConfig    - per-support-group SMTP overrides
-- ============================================================

-- 1) Global SMTP keys live as rows in your existing SiteConfiguration
--    key/value table. If you don't have one yet, here is the minimum:
CREATE TABLE SiteConfiguration (
    ConfigId    INT IDENTITY(1,1) PRIMARY KEY,
    ConfigKey   NVARCHAR(100) NOT NULL UNIQUE,
    ConfigValue NVARCHAR(MAX),
    UpdatedAt   DATETIME,
    UpdatedBy   INT NULL
);

-- Seed (and on every install, ensure these keys exist; do not overwrite values
-- that admins may have customized).
INSERT INTO SiteConfiguration (ConfigKey, ConfigValue) VALUES
    ('SmtpHost',           'smtp.sendgrid.net'),
    ('SmtpPort',           '587'),
    ('SmtpUser',           'apikey'),
    ('SmtpPassword',       ''),
    ('SmtpFromEmail',      'noreply@yourdomain.com'),
    ('SmtpEnableSsl',      'true'),
    ('GeneralTicketEmail', ''),
    ('SiteName',           'IT HelpDesk'),
    ('SiteUrl',            'http://localhost:5000');

-- 2) Per-group SMTP override
CREATE TABLE GroupMailConfig (
    ConfigId      INT IDENTITY(1,1) PRIMARY KEY,
    GroupId       INT NOT NULL UNIQUE
                  REFERENCES TechnicianGroups(GroupId),
    SmtpHost      NVARCHAR(200) NOT NULL,
    SmtpPort      INT NOT NULL DEFAULT 587,
    SmtpUser      NVARCHAR(200) NOT NULL,
    SmtpPassword  NVARCHAR(500) NOT NULL,
    SmtpFromEmail NVARCHAR(200) NOT NULL,
    SmtpFromName  NVARCHAR(200) NOT NULL,
    SmtpEnableSsl BIT NOT NULL DEFAULT 1,
    IsEnabled     BIT NOT NULL DEFAULT 0,
    UpdatedAt     DATETIME NOT NULL DEFAULT GETDATE()
);

-- (Optional) helper view: which groups currently have a working override
CREATE OR ALTER VIEW vw_GroupMailEnabled AS
SELECT  g.GroupId, g.GroupName, m.SmtpHost, m.SmtpFromEmail, m.IsEnabled
FROM    TechnicianGroups g
LEFT JOIN GroupMailConfig m ON m.GroupId = g.GroupId
WHERE   m.IsEnabled = 1
  AND   m.SmtpHost      <> ''
  AND   m.SmtpUser      <> ''
  AND   m.SmtpPassword  <> '';
