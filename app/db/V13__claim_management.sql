-- ===================================================================
-- V13  Claim Management workflow.
--
-- Adds the post-QC claim workflow that sits on top of every Closed
-- Quality Order:
--
--   1. Extend Users.Role CHECK constraint to allow a new 5th role
--      'ClaimManager' (commercial decision-maker). Mirrors the V12
--      pattern: drop the named CK_Users_Role, recreate with the new
--      five-value list. DF_Users_Role default stays 'Viewer'.
--
--   2. New table qms_claim -- one row per QO created lazily when the
--      Quality Manager first marks the QO as ClaimRequest or PassedQC.
--      The QO's own status_code is NOT changed; the claim is a
--      separate concern with its own lifecycle.
--
--   3. New table qms_claim_note -- append-only chat history. Both
--      managers see each other's notes. note_kind distinguishes
--      status-change justifications from plain commentary.
--
-- Status values (enforced by CHECK on qms_claim.claim_status):
--   * ClaimRequest         -- QM proposes a claim against the supplier
--   * PassedQC             -- QM clears the QO; no claim needed
--   * ClaimRequestApproved -- CM agrees with QM; commercial claim is on
--   * HoldClaim            -- CM disagrees / wants to hold the claim
-- ===================================================================

-- 1. Extend Users.Role CHECK.
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Users_Role')
    ALTER TABLE Users DROP CONSTRAINT CK_Users_Role;
GO
ALTER TABLE Users ADD CONSTRAINT CK_Users_Role
    CHECK (Role IN ('SiteAdmin','Manager','ClaimManager','Operator','Viewer'));
GO

-- 2. Claim header. One row per QO that has been touched by Claim Mgmt.
IF OBJECT_ID('qms_claim', 'U') IS NULL
BEGIN
    CREATE TABLE qms_claim (
        claim_id          BIGINT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_qms_claim PRIMARY KEY,
        quality_order_id  BIGINT       NOT NULL
            CONSTRAINT UQ_qms_claim_qo UNIQUE
            CONSTRAINT FK_qms_claim_qo FOREIGN KEY
                REFERENCES qms_quality_order(quality_order_id),
        claim_status      VARCHAR(40)  NOT NULL,
        created_at        DATETIME2(0) NOT NULL
            CONSTRAINT DF_qms_claim_created DEFAULT SYSUTCDATETIME(),
        created_by        NVARCHAR(80) NOT NULL,
        last_changed_at   DATETIME2(0) NOT NULL,
        last_changed_by   NVARCHAR(80) NOT NULL,
        decided_at        DATETIME2(0) NULL,
        decided_by        NVARCHAR(80) NULL,
        CONSTRAINT CK_qms_claim_status CHECK (
            claim_status IN ('ClaimRequest','PassedQC','ClaimRequestApproved','HoldClaim'))
    );

    CREATE INDEX IX_qms_claim_status
        ON qms_claim(claim_status) INCLUDE (quality_order_id);
END
GO

-- 3. Append-only chat history.
IF OBJECT_ID('qms_claim_note', 'U') IS NULL
BEGIN
    CREATE TABLE qms_claim_note (
        note_id        BIGINT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_qms_claim_note PRIMARY KEY,
        claim_id       BIGINT        NOT NULL
            CONSTRAINT FK_qms_claim_note_claim FOREIGN KEY
                REFERENCES qms_claim(claim_id) ON DELETE CASCADE,
        note_text      NVARCHAR(MAX) NOT NULL,
        note_kind      VARCHAR(20)   NOT NULL,
        status_at_post VARCHAR(40)   NULL,
        created_at     DATETIME2(0)  NOT NULL
            CONSTRAINT DF_qms_claim_note_created DEFAULT SYSUTCDATETIME(),
        created_by     NVARCHAR(80)  NOT NULL,
        author_role    VARCHAR(20)   NOT NULL,
        CONSTRAINT CK_qms_claim_note_kind CHECK (
            note_kind IN ('StatusChange','Comment'))
    );

    CREATE INDEX IX_qms_claim_note_claim
        ON qms_claim_note(claim_id, created_at);
END
GO

-- 4. Sanity report.
SELECT 'CK_Users_Role'   AS check_name,  definition
FROM   sys.check_constraints
WHERE  name = 'CK_Users_Role';

SELECT name FROM sys.tables WHERE name IN ('qms_claim','qms_claim_note') ORDER BY name;
GO
