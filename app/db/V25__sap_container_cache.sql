-- ===================================================================
-- V25  qms_sap_container_cache
--
-- Local mirror of the SAP ZQC_Data CDS view, populated incrementally by
-- ContainerPollingService. Used to render /Arrivals/Pending without
-- hammering SAP on every page load, and to surface containers that
-- have not yet been promoted to a QMS Arrival.
--
-- Grain: SAP row (one per PO line x container x BOL x material x
-- plant x storage x batch). The Pending page groups by (Container,
-- BOL, PO/Ebeln) triplet to match the existing Arrival creation grain.
--
-- Idempotent.
-- ===================================================================

IF OBJECT_ID(N'dbo.qms_sap_container_cache', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.qms_sap_container_cache (
        cache_id        BIGINT IDENTITY(1,1) PRIMARY KEY,
        container_no    VARCHAR(35) NOT NULL,
        bol_no          VARCHAR(35) NOT NULL,
        ebeln           VARCHAR(10) NOT NULL,
        ebelp           VARCHAR(5)  NOT NULL,
        material_no     VARCHAR(40) NOT NULL,
        plant           VARCHAR(4)  NOT NULL,
        storage_loc     VARCHAR(4)  NOT NULL,
        batch_no        VARCHAR(20) NULL,
        vendor_no       VARCHAR(10) NULL,
        vendor_name     NVARCHAR(80) NULL,
        material_desc   NVARCHAR(120) NULL,
        material_group  VARCHAR(9)  NULL,
        doc_date        DATE NULL,
        arrival_date    DATE NULL,
        receive_date    DATE NULL,
        quantity        DECIMAL(18,3) NULL,
        uom             VARCHAR(3)  NULL,
        payload_json    NVARCHAR(MAX) NULL,
        has_arrival     BIT NOT NULL CONSTRAINT DF_qms_sap_container_cache_has_arrival DEFAULT (0),
        arrival_id      BIGINT NULL REFERENCES dbo.qms_arrival(arrival_id),
        first_seen_at   DATETIME2 NOT NULL CONSTRAINT DF_qms_sap_container_cache_first_seen DEFAULT (SYSUTCDATETIME()),
        last_seen_at    DATETIME2 NOT NULL CONSTRAINT DF_qms_sap_container_cache_last_seen  DEFAULT (SYSUTCDATETIME())
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_qms_sap_container_cache_key'
                 AND object_id = OBJECT_ID(N'dbo.qms_sap_container_cache'))
BEGIN
    -- Natural identity at SAP-row grain. Composite key matches
    -- SapShipmentRow.SelectionKey so MERGE / UPSERT can target uniquely.
    -- batch_no can be NULL; coalesce to '' for index compatibility.
    CREATE UNIQUE INDEX UX_qms_sap_container_cache_key
        ON dbo.qms_sap_container_cache
           (container_no, bol_no, ebeln, ebelp, material_no, plant, storage_loc, batch_no);
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_qms_sap_container_cache_pending'
                 AND object_id = OBJECT_ID(N'dbo.qms_sap_container_cache'))
BEGIN
    -- Filtered index: the /Arrivals/Pending page scans only has_arrival = 0.
    CREATE INDEX IX_qms_sap_container_cache_pending
        ON dbo.qms_sap_container_cache (doc_date DESC, container_no, bol_no, ebeln)
        WHERE has_arrival = 0;
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_qms_sap_container_cache_triplet'
                 AND object_id = OBJECT_ID(N'dbo.qms_sap_container_cache'))
BEGIN
    -- Triplet lookup -- used by MarkArrivedAsync and ReconcileWithArrivalsAsync.
    CREATE INDEX IX_qms_sap_container_cache_triplet
        ON dbo.qms_sap_container_cache (container_no, bol_no, ebeln);
END;
