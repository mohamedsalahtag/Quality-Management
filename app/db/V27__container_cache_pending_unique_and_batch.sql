-- ===================================================================
-- V27  Arrival duplicate-prevention + container cache batch_no clarity
--
-- Two related fixes:
--
-- 1. C4 / cache->arrival race
--    Two operators clicking "Create Arrival" on the same triplet (or
--    a manual Create racing the auto-reconciliation pass) can both
--    pass the application-level `has_arrival = 0` check and end up
--    creating two distinct qms_arrival rows for the same SAP
--    (Container, BOL, PO) triplet. A filtered unique index closes
--    this race at the DB layer: the second writer's INSERT raises
--    a 2627 duplicate-key error and rolls back cleanly. Cancelled
--    arrivals are excluded so a re-creation after cancel still works.
--
--    NOTE: this migration is safe to re-run. If qms_arrival already
--    contains duplicate active triplets (from races prior to this
--    fix) the CREATE INDEX would fail. We detect that first and
--    print a row listing each duplicate so the operator can resolve
--    them (typically by cancelling the older row) before re-applying.
--
-- 2. M10 / batch_no NULL semantics in qms_sap_container_cache
--    SQL Server's standard UNIQUE INDEX treats every NULL as DISTINCT,
--    so multiple rows with the same natural key but batch_no IS NULL
--    slip through the existing index UX_qms_sap_container_cache_key.
--    The MERGE in ContainerCacheService.UpsertAsync handles this with
--    explicit `T.batch_no IS NULL AND S.batch_no IS NULL` matching, so
--    no duplicate rows actually appear in practice. The extra filtered
--    UNIQUE index documents the constraint and lets the planner seek
--    straight to the single candidate row.
--
-- Idempotent.
-- ===================================================================

-- ---- C4: prevent duplicate active arrivals per SAP triplet ----------
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_qms_arrival_active_triplet'
                 AND object_id = OBJECT_ID(N'dbo.qms_arrival'))
BEGIN
    -- Detect existing duplicates first so the operator gets a clear
    -- diagnostic instead of a generic 2627 message. If any are found
    -- we skip the index creation and print the offending triplets;
    -- re-run after cancelling the older duplicate of each.
    IF EXISTS (
        SELECT 1
        FROM   dbo.qms_arrival
        WHERE  status_code <> 'Cancelled'
          AND  container_no IS NOT NULL
          AND  bol_no       IS NOT NULL
          AND  ebeln        IS NOT NULL
        GROUP BY container_no, bol_no, ebeln
        HAVING COUNT(*) > 1)
    BEGIN
        PRINT 'V27 SKIP: qms_arrival already contains duplicate active triplets. ' +
              'Resolve them (typically cancel the older arrival_no) then re-apply V27.';
        -- Pre-2017 SQL Server is in use here, so we don't STRING_AGG.
        SELECT container_no, bol_no, ebeln,
               COUNT(*)              AS duplicate_count
        FROM   dbo.qms_arrival
        WHERE  status_code <> 'Cancelled'
          AND  container_no IS NOT NULL
          AND  bol_no       IS NOT NULL
          AND  ebeln        IS NOT NULL
        GROUP BY container_no, bol_no, ebeln
        HAVING COUNT(*) > 1;
    END
    ELSE
    BEGIN
        CREATE UNIQUE INDEX UX_qms_arrival_active_triplet
            ON dbo.qms_arrival (container_no, bol_no, ebeln)
            WHERE status_code <> 'Cancelled'
              AND container_no IS NOT NULL
              AND bol_no       IS NOT NULL
              AND ebeln        IS NOT NULL;
    END
END;

-- ---- M10: explicit batch_no NULL handling for container cache ------
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_qms_sap_container_cache_key_nullbatch'
                 AND object_id = OBJECT_ID(N'dbo.qms_sap_container_cache'))
BEGIN
    CREATE UNIQUE INDEX UX_qms_sap_container_cache_key_nullbatch
        ON dbo.qms_sap_container_cache
           (container_no, bol_no, ebeln, ebelp, material_no, plant, storage_loc)
        WHERE batch_no IS NULL;
END;
