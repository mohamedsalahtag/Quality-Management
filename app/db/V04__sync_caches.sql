-- ============================================================================
--  SharbatlyQMS  -  V04  -  SAP sync cache tables + sync log
-- ============================================================================
--  Domain rule (per business stakeholder):
--    * Material Master and Vendor Master are MASS-SYNC targets (cache locally
--      so lookups are fast, refreshed on a schedule).
--    * PO / Shipment / Container data are SEARCH-ONLY: the inspector queries
--      SAP live when creating an arrival checklist; on save, the relevant
--      slice is snapshotted into qms_arrival_item / qms_shipment_snapshot /
--      qms_arrival_sap_snapshot.  Those URLs are never bulk-synced.
--
--  Each cache table stores the natural key + the raw OData payload (JSON) so
--  we don't have to commit to a fixed column set up-front; downstream code
--  reads whichever fields the live CDS view returns.
-- ============================================================================

CREATE TABLE qms_sap_material_cache (
    material_no   VARCHAR(40)    NOT NULL PRIMARY KEY,
    payload_json  NVARCHAR(MAX)  NOT NULL,
    source_id     NVARCHAR(120)  NULL,
    synced_at     DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_qms_sap_material_cache_synced ON qms_sap_material_cache(synced_at);

CREATE TABLE qms_sap_vendor_cache (
    vendor_no     VARCHAR(20)    NOT NULL PRIMARY KEY,
    payload_json  NVARCHAR(MAX)  NOT NULL,
    source_id     NVARCHAR(120)  NULL,
    synced_at     DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_qms_sap_vendor_cache_synced ON qms_sap_vendor_cache(synced_at);

CREATE TABLE qms_sap_sync_log (
    sync_log_id   BIGINT IDENTITY(1,1) PRIMARY KEY,
    endpoint_key  VARCHAR(40)   NOT NULL,    -- MaterialMaster | VendorMaster
    started_at    DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    completed_at  DATETIME2     NULL,
    success       BIT           NULL,
    rows_synced   INT           NULL,
    message       NVARCHAR(2000) NULL,
    triggered_by  NVARCHAR(80)  NOT NULL,
    trigger_source VARCHAR(20)  NOT NULL DEFAULT 'Manual',  -- Manual | Auto
    CONSTRAINT CK_qms_sap_sync_log_source CHECK (trigger_source IN ('Manual','Auto'))
);
CREATE INDEX IX_qms_sap_sync_log_endpoint ON qms_sap_sync_log(endpoint_key, started_at DESC);

-- ----------------------------------------------------------------------------
-- Seed per-endpoint settings keys (default disabled, no schedule).
-- These replace the older global Sync.Auto.* keys (those stay in the table
-- for backward compatibility but are no longer read by the application).
-- ----------------------------------------------------------------------------
INSERT INTO SiteConfiguration (ConfigKey, ConfigValue) VALUES
    ('Sap.Sync.MaterialMaster.Enabled',     'false'),
    ('Sap.Sync.MaterialMaster.Hours',       ''),
    ('Sap.Sync.MaterialMaster.LastRunUtc',  ''),
    ('Sap.Sync.MaterialMaster.LastResult',  ''),
    ('Sap.Sync.MaterialMaster.LastRowCount',''),
    ('Sap.Sync.VendorMaster.Enabled',       'false'),
    ('Sap.Sync.VendorMaster.Hours',         ''),
    ('Sap.Sync.VendorMaster.LastRunUtc',    ''),
    ('Sap.Sync.VendorMaster.LastResult',    ''),
    ('Sap.Sync.VendorMaster.LastRowCount',  '');
