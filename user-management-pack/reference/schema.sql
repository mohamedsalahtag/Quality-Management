-- ============================================================
-- User Management Pack - Storage
-- ============================================================
-- Three tables + AD config keys in SiteConfiguration.
-- Roles enforced as a string column (no enum) so the set is configurable.
-- ============================================================

-- 1) Users
CREATE TABLE Users (
    UserId          INT IDENTITY(1,1) PRIMARY KEY,
    EmployeeId      NVARCHAR(50),
    AdUsername      NVARCHAR(100) NOT NULL UNIQUE,    -- login key
    FullName        NVARCHAR(200) NOT NULL,
    Email           NVARCHAR(200),
    Department      NVARCHAR(200),
    ProfilePicture  NVARCHAR(300) NULL,               -- /avatars/{userId}.{ext}
    PasswordHash    NVARCHAR(500) NOT NULL,           -- BCrypt local fallback
    Role            NVARCHAR(50)  NOT NULL DEFAULT 'Requester',
        -- Roles: Requester | Technician | FirstLevelSupport | SiteAdmin
    IsActive        BIT NOT NULL DEFAULT 1,
    IsOnline        BIT NOT NULL DEFAULT 0,
    LastLogin       DATETIME NULL,
    LastSeen        DATETIME NULL,
    CreatedAt       DATETIME NOT NULL DEFAULT GETDATE(),
    CreatedBy       INT NULL REFERENCES Users(UserId),
    DisabledAt      DATETIME NULL,
    DisabledBy      INT NULL REFERENCES Users(UserId)
);

-- 2) Support groups (kept for tickets & permissions; remove if not needed)
CREATE TABLE TechnicianGroups (
    GroupId     INT IDENTITY(1,1) PRIMARY KEY,
    GroupName   NVARCHAR(100) NOT NULL,
    ShortCode   NVARCHAR(10)  NOT NULL,             -- 3-letter prefix
    Description NVARCHAR(500),
    IsActive    BIT NOT NULL DEFAULT 1,
    CreatedAt   DATETIME NOT NULL DEFAULT GETDATE()
);

-- 3) Per-user-per-group permissions
CREATE TABLE TechnicianGroupMembers (
    MemberId        INT IDENTITY(1,1) PRIMARY KEY,
    UserId          INT NOT NULL REFERENCES Users(UserId),
    GroupId         INT NOT NULL REFERENCES TechnicianGroups(GroupId),
    CanDelete       BIT NOT NULL DEFAULT 0,
    CanAssign       BIT NOT NULL DEFAULT 0,
    CanPickupOthers BIT NOT NULL DEFAULT 0,
    AssignedAt      DATETIME NOT NULL DEFAULT GETDATE(),
    CONSTRAINT UQ_TechGroupMember UNIQUE (UserId, GroupId)
);

-- 4) AD config keys (rows in your existing SiteConfiguration K/V table).
--    INSERT only if not present; admins customize values via the UI.
INSERT INTO SiteConfiguration (ConfigKey, ConfigValue) VALUES
    ('AdDomain',          ''),
    ('AdLdapPath',        ''),
    ('AdServiceUser',     ''),
    ('AdServicePassword', ''),
    ('AutoCreateAdUsers', 'false');

-- 5) Seed admin (password 'Admin@123', BCrypt). The /Account/Setup endpoint
--    re-hashes this on first real login (the placeholder hash below is one
--    of the well-known seed hashes the bootstrap path recognizes).
INSERT INTO Users
    (EmployeeId, AdUsername, FullName, Email, Department, PasswordHash, Role, IsActive)
VALUES
    ('admin', 'admin', 'System Administrator', 'admin@company.com', 'IT Department',
     '$2a$11$K/YL1mZMBJaEBFwP1OQnCOkMmLLqJOwv8QqKAynaqxaFGgOGVJBSa',
     'SiteAdmin', 1);
