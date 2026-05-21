-- ===================================================================
-- V14  Per-user read markers for the Claim Management chat.
--
-- Lets the Claim Management list show "unread" badges per row:
-- when a user opens a claim's Details we upsert their last_seen_at to
-- NOW; the list query counts notes added after that timestamp (and
-- not authored by the user themselves -- you don't owe yourself a
-- "new" badge for notes you typed).
-- ===================================================================

IF OBJECT_ID('qms_claim_read_marker', 'U') IS NULL
BEGIN
    CREATE TABLE qms_claim_read_marker (
        user_name    NVARCHAR(80) NOT NULL,
        claim_id     BIGINT       NOT NULL
            CONSTRAINT FK_qms_claim_read_marker_claim FOREIGN KEY
                REFERENCES qms_claim(claim_id) ON DELETE CASCADE,
        last_seen_at DATETIME2(0) NOT NULL,
        CONSTRAINT PK_qms_claim_read_marker PRIMARY KEY (user_name, claim_id)
    );
END
GO

SELECT name FROM sys.tables WHERE name = 'qms_claim_read_marker';
GO
