/* =====================================================================
   V42 -- Potential-claim classification captured when a Quality Order is
   finished. V-SERIES TWIN of app\db\mis\M24__qo_potential_claim.sql.

   The live system runs the M-series against Sharbatly_MIS (schema [qms]
   behind dbo synonyms). This file exists ONLY so the older private
   SharbatlyQMS database on 192.168.3.10 can be brought level: without
   these columns the finish UPDATE in QualityOrderService.Transition fails
   with "Invalid column name 'potential_claim'", which would break
   finishing a Quality Order for anyone running the Development config.

   NOT APPLIED ANYWHERE as of 2026-08-30 -- that database is retired. Run
   it only if you revive it:
     dotnet run --project app\SharbatlyQMS.Migrate -- apply "<dev cs>" app\db\V42__qo_potential_claim.sql

   Unqualified table names (no [qms] schema) to match the rest of the
   V-series. Idempotent: safe to re-run.
   ===================================================================== */

IF COL_LENGTH('qms_quality_order', 'potential_claim') IS NULL
    ALTER TABLE qms_quality_order ADD potential_claim BIT NULL;
GO

IF COL_LENGTH('qms_quality_order', 'potential_claim_at') IS NULL
    ALTER TABLE qms_quality_order ADD potential_claim_at DATETIME2(0) NULL;
GO

IF COL_LENGTH('qms_quality_order', 'potential_claim_by') IS NULL
    ALTER TABLE qms_quality_order ADD potential_claim_by NVARCHAR(80) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_qms_quality_order_potential_claim'
                 AND object_id = OBJECT_ID('qms_quality_order'))
    CREATE INDEX IX_qms_quality_order_potential_claim
        ON qms_quality_order (potential_claim)
        INCLUDE (status_code, archived_at);
GO

SELECT 'potential_claim columns' AS check_item, COUNT(*) AS found
FROM   sys.columns
WHERE  object_id = OBJECT_ID('qms_quality_order')
  AND  name IN ('potential_claim', 'potential_claim_at', 'potential_claim_by');
GO
