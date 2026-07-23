-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M02  -  QMS domain tables
-- ============================================================================
--  GENERATED from the live SharbatlyQMS database (post-V39 state) - do not
--  hand-edit; regenerate with scratchpad/gen-ddl.ps1 if the source changes.
--
--  Table names are unchanged so the app's unqualified SQL (FROM qms_arrival)
--  resolves via the QMS_App login's DEFAULT_SCHEMA = qms.
--
--  NOT created here (deliberate):
--    qms_sap_material_cache -> view qms.MaterialCatalog over dbo.Mara   (M03)
--    qms_sap_vendor_cache   -> view qms.VendorCatalog over dbo.SAP_Vendors (M03)
--    Users, SiteConfiguration -> portal.User / portal.SystemSetting
--    EmailGroups, EmailGroupMembers, EmailGroupAddresses, GroupMailConfig,
--    AlertRules -> dropped, zero code references.
-- ============================================================================

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_arrival (
    [arrival_id] bigint IDENTITY(1,1) NOT NULL,
    [arrival_no] varchar(20) NOT NULL,
    [source_system] varchar(20) DEFAULT ('S4HANA') NOT NULL,
    [bol_no] varchar(35) NULL,
    [container_no] varchar(35) NULL,
    [ebeln] varchar(10) NULL,
    [bukrs] varchar(4) NULL,
    [vendor_no] varchar(10) NULL,
    [vendor_name] nvarchar(80) NULL,
    [status_code] varchar(20) DEFAULT ('Draft') NOT NULL,
    [created_at] datetime2(7) DEFAULT (sysutcdatetime()) NOT NULL,
    [created_by] nvarchar(80) NOT NULL,
    [completed_at] datetime2(7) NULL,
    [completed_by] nvarchar(80) NULL,
    [row_version] timestamp NOT NULL,
    [plant] varchar(4) NULL,
    CONSTRAINT [PK_qms_arrival] PRIMARY KEY ([arrival_id]),
    CONSTRAINT [UQ_qms_arrival_arrival_no] UNIQUE ([arrival_no]),
    CONSTRAINT [CK_qms_arrival_status] CHECK ([status_code]='Cancelled' OR [status_code]='Completed' OR [status_code]='Draft')
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_arrival_checklist (
    [checklist_id] bigint IDENTITY(1,1) NOT NULL,
    [arrival_id] bigint NOT NULL,
    [seal_no] nvarchar(200) NULL,
    [carrier_name] nvarchar(80) NULL,
    [seal_intact] bit NULL,
    [seal_matches_documents] bit NULL,
    [external_damage_exists] bit NULL,
    [set_temperature] decimal(6,2) NULL,
    [display_temperature] decimal(6,2) NULL,
    [cargo_smell_normal] bit NULL,
    [visual_cargo_acceptable] bit NULL,
    [cargo_shifted_collapsed_water] bit NULL,
    [pulp_temp_front] decimal(6,2) NULL,
    [pulp_temp_middle] decimal(6,2) NULL,
    [pulp_temp_back] decimal(6,2) NULL,
    [data_logger_located] bit NULL,
    [data_logger_serial] nvarchar(300) NULL,
    [data_logger_photo_taken] bit NULL,
    [logger_handed_over] bit NULL,
    [logger_active_data_available] bit NULL,
    [logger_temperature] decimal(6,2) NULL,
    [notes] nvarchar(max) NULL,
    [updated_at] datetime2(7) NULL,
    [updated_by] nvarchar(80) NULL,
    [display_temp_photo_taken] bit NULL,
    [internal_inspection_photo_taken] bit NULL,
    [pulp_temp_photo_taken] bit NULL,
    [container_seal_photo_taken] bit NULL,
    [external_container_photo_taken] bit NULL,
    [external_damage_photo_taken] bit NULL,
    [first_view_cargo_photo_taken] bit NULL,
    [internal_damage_photo_taken] bit NULL,
    CONSTRAINT [PK_qms_arrival_checklist] PRIMARY KEY ([checklist_id]),
    CONSTRAINT [UQ_qms_arrival_checklist_arrival_id] UNIQUE ([arrival_id])
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_arrival_field (
    [field_id] int IDENTITY(1,1) NOT NULL,
    [field_name] nvarchar(80) NOT NULL,
    [value_kind] varchar(10) DEFAULT ('Text') NOT NULL,
    [material_group] varchar(40) NOT NULL,
    [sort_order] int DEFAULT ((500)) NOT NULL,
    [is_active] bit DEFAULT ((1)) NOT NULL,
    [created_at] datetime2(7) DEFAULT (sysutcdatetime()) NOT NULL,
    [created_by] nvarchar(100) NULL,
    CONSTRAINT [PK_qms_arrival_field] PRIMARY KEY ([field_id]),
    CONSTRAINT [UQ_qms_arrival_field_name_group] UNIQUE ([field_name],[material_group]),
    CONSTRAINT [CK_qms_arrival_field_kind] CHECK ([value_kind]='YesNo' OR [value_kind]='Date' OR [value_kind]='Numeric' OR [value_kind]='Text')
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_arrival_field_value (
    [value_id] bigint IDENTITY(1,1) NOT NULL,
    [arrival_id] bigint NOT NULL,
    [field_id] int NOT NULL,
    [text_value] nvarchar(400) NULL,
    [numeric_value] decimal(18,4) NULL,
    [date_value] date NULL,
    [updated_at] datetime2(7) DEFAULT (sysutcdatetime()) NOT NULL,
    [updated_by] nvarchar(100) NULL,
    CONSTRAINT [PK_qms_arrival_field_value] PRIMARY KEY ([value_id]),
    CONSTRAINT [UQ_qms_arrival_field_value] UNIQUE ([arrival_id],[field_id])
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_arrival_item (
    [arrival_item_id] bigint IDENTITY(1,1) NOT NULL,
    [arrival_id] bigint NOT NULL,
    [ebeln] varchar(10) NOT NULL,
    [ebelp] varchar(5) NOT NULL,
    [material_no] varchar(40) NOT NULL,
    [material_desc] nvarchar(120) NULL,
    [plant] varchar(4) NULL,
    [storage_location] varchar(4) NULL,
    [batch_no] varchar(20) NULL,
    [quantity] decimal(18,3) NULL,
    [uom] varchar(3) NULL,
    [material_group] varchar(9) NULL,
    [material_group_desc] nvarchar(80) NULL,
    [major_category] nvarchar(80) NULL,
    [row_version] timestamp NOT NULL,
    CONSTRAINT [PK_qms_arrival_item] PRIMARY KEY ([arrival_item_id])
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_arrival_sap_snapshot (
    [snapshot_id] bigint IDENTITY(1,1) NOT NULL,
    [arrival_id] bigint NOT NULL,
    [odata_service_name] nvarchar(120) NOT NULL,
    [odata_query_hash] char(64) NOT NULL,
    [payload_json] nvarchar(max) NOT NULL,
    [captured_at] datetime2(7) DEFAULT (sysutcdatetime()) NOT NULL,
    [captured_by] nvarchar(80) NOT NULL,
    CONSTRAINT [PK_qms_arrival_sap_snapshot] PRIMARY KEY ([snapshot_id])
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_audit_log (
    [audit_id] bigint IDENTITY(1,1) NOT NULL,
    [entity_type] varchar(40) NOT NULL,
    [entity_id] bigint NOT NULL,
    [action_code] varchar(40) NOT NULL,
    [old_values_json] nvarchar(max) NULL,
    [new_values_json] nvarchar(max) NULL,
    [changed_at] datetime2(7) DEFAULT (sysutcdatetime()) NOT NULL,
    [changed_by] nvarchar(80) NOT NULL,
    [source_ip] varchar(45) NULL,
    [source_user_agent] nvarchar(500) NULL,
    [source_device_name] nvarchar(255) NULL,
    CONSTRAINT [PK_qms_audit_log] PRIMARY KEY ([audit_id])
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_claim (
    [claim_id] bigint IDENTITY(1,1) NOT NULL,
    [quality_order_id] bigint NOT NULL,
    [claim_status] varchar(40) NOT NULL,
    [created_at] datetime2(0) DEFAULT (sysutcdatetime()) NOT NULL,
    [created_by] nvarchar(80) NOT NULL,
    [last_changed_at] datetime2(0) NOT NULL,
    [last_changed_by] nvarchar(80) NOT NULL,
    [decided_at] datetime2(0) NULL,
    [decided_by] nvarchar(80) NULL,
    CONSTRAINT [PK_qms_claim] PRIMARY KEY ([claim_id]),
    CONSTRAINT [UQ_qms_claim_qo] UNIQUE ([quality_order_id]),
    CONSTRAINT [CK_qms_claim_status] CHECK ([claim_status]='HoldClaim' OR [claim_status]='ClaimRequestApproved' OR [claim_status]='PassedQC' OR [claim_status]='ClaimRequest')
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_claim_note (
    [note_id] bigint IDENTITY(1,1) NOT NULL,
    [claim_id] bigint NOT NULL,
    [note_text] nvarchar(max) NOT NULL,
    [note_kind] varchar(20) NOT NULL,
    [status_at_post] varchar(40) NULL,
    [created_at] datetime2(0) DEFAULT (sysutcdatetime()) NOT NULL,
    [created_by] nvarchar(80) NOT NULL,
    [author_role] varchar(20) NOT NULL,
    CONSTRAINT [PK_qms_claim_note] PRIMARY KEY ([note_id]),
    CONSTRAINT [CK_qms_claim_note_kind] CHECK ([note_kind]='Comment' OR [note_kind]='StatusChange')
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_claim_read_marker (
    [user_name] nvarchar(80) NOT NULL,
    [claim_id] bigint NOT NULL,
    [last_seen_at] datetime2(0) NOT NULL,
    CONSTRAINT [PK_qms_claim_read_marker] PRIMARY KEY ([user_name],[claim_id])
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_defect_catalog (
    [defect_id] int IDENTITY(1,1) NOT NULL,
    [defect_code] varchar(50) NOT NULL,
    [defect_name] nvarchar(100) NOT NULL,
    [defect_category] varchar(20) DEFAULT ('Minor') NOT NULL,
    [is_active] bit DEFAULT ((1)) NOT NULL,
    [sort_order] int DEFAULT ((0)) NOT NULL,
    [material_group] varchar(9) NOT NULL,
    [value_type] varchar(20) DEFAULT ('Number') NOT NULL,
    CONSTRAINT [PK_qms_defect_catalog] PRIMARY KEY ([defect_id]),
    CONSTRAINT [UQ_qms_defect_catalog_group_code] UNIQUE ([material_group],[defect_code]),
    CONSTRAINT [CK_qms_defect_catalog_value_type] CHECK ([value_type]='Decimal' OR [value_type]='Number')
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_defect_category (
    [category_id] int IDENTITY(1,1) NOT NULL,
    [category_name] varchar(20) NOT NULL,
    [sort_order] int DEFAULT ((500)) NOT NULL,
    [color_hex] varchar(7) NULL,
    [is_active] bit DEFAULT ((1)) NOT NULL,
    CONSTRAINT [PK_qms_defect_category] PRIMARY KEY ([category_id]),
    CONSTRAINT [UQ_qms_defect_category_name] UNIQUE ([category_name])
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_document (
    [document_id] bigint IDENTITY(1,1) NOT NULL,
    [owner_type] varchar(30) NOT NULL,
    [owner_id] bigint NOT NULL,
    [category] varchar(40) DEFAULT ('') NOT NULL,
    [original_file_name] nvarchar(255) NOT NULL,
    [content_type] varchar(100) NOT NULL,
    [file_size_bytes] bigint NOT NULL,
    [storage_path] nvarchar(1000) NOT NULL,
    [checksum_sha256] char(64) NULL,
    [caption] nvarchar(255) NULL,
    [uploaded_at] datetime2(7) DEFAULT (sysutcdatetime()) NOT NULL,
    [uploaded_by] nvarchar(80) NOT NULL,
    [is_deleted] bit DEFAULT ((0)) NOT NULL,
    CONSTRAINT [PK_qms_document] PRIMARY KEY ([document_id]),
    CONSTRAINT [CK_qms_document_owner_type] CHECK ([owner_type]='Sample' OR [owner_type]='QualityOrder' OR [owner_type]='Arrival')
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_image_asset (
    [image_id] bigint IDENTITY(1,1) NOT NULL,
    [storage_provider] varchar(30) DEFAULT ('Local') NOT NULL,
    [original_file_name] nvarchar(255) NOT NULL,
    [content_type] varchar(100) NOT NULL,
    [file_size_bytes] bigint NOT NULL,
    [storage_url] nvarchar(1000) NOT NULL,
    [thumbnail_url] nvarchar(1000) NULL,
    [checksum_sha256] char(64) NULL,
    [width_px] int NULL,
    [height_px] int NULL,
    [uploaded_at] datetime2(7) DEFAULT (sysutcdatetime()) NOT NULL,
    [uploaded_by] nvarchar(80) NOT NULL,
    [is_deleted] bit DEFAULT ((0)) NOT NULL,
    CONSTRAINT [PK_qms_image_asset] PRIMARY KEY ([image_id])
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_image_link (
    [image_link_id] bigint IDENTITY(1,1) NOT NULL,
    [image_id] bigint NOT NULL,
    [owner_type] varchar(30) NOT NULL,
    [owner_id] bigint NOT NULL,
    [image_category] varchar(40) NOT NULL,
    [display_order] int DEFAULT ((0)) NOT NULL,
    [caption] nvarchar(255) NULL,
    [include_in_report] bit DEFAULT ((1)) NOT NULL,
    [created_at] datetime2(7) DEFAULT (sysutcdatetime()) NOT NULL,
    [created_by] nvarchar(80) NOT NULL,
    CONSTRAINT [PK_qms_image_link] PRIMARY KEY ([image_link_id]),
    CONSTRAINT [CK_qms_image_link_owner_type] CHECK ([owner_type]='Sample' OR [owner_type]='QualityOrderMaterial' OR [owner_type]='QualityOrder' OR [owner_type]='ArrivalChecklist' OR [owner_type]='Arrival')
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_material_group_defect (
    [material_group_defect_id] int IDENTITY(1,1) NOT NULL,
    [material_group] varchar(9) NOT NULL,
    [major_category] nvarchar(80) NULL,
    [defect_id] int NOT NULL,
    [is_required] bit DEFAULT ((0)) NOT NULL,
    [is_active] bit DEFAULT ((1)) NOT NULL,
    [display_section] varchar(20) DEFAULT ('Minor') NOT NULL,
    [sort_order] int DEFAULT ((0)) NOT NULL,
    CONSTRAINT [PK_qms_material_group_defect] PRIMARY KEY ([material_group_defect_id]),
    CONSTRAINT [CK_qms_material_group_defect_section] CHECK ([display_section]='Readings' OR [display_section]='Minor' OR [display_section]='Major')
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_material_group_reading (
    [material_group_reading_id] int IDENTITY(1,1) NOT NULL,
    [material_group] varchar(9) NOT NULL,
    [major_category] nvarchar(80) NULL,
    [reading_type_id] int NOT NULL,
    [is_required] bit DEFAULT ((0)) NOT NULL,
    [is_active] bit DEFAULT ((1)) NOT NULL,
    [sort_order] int DEFAULT ((0)) NOT NULL,
    CONSTRAINT [PK_qms_material_group_reading] PRIMARY KEY ([material_group_reading_id])
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_perspective (
    [perspective_id] bigint IDENTITY(1,1) NOT NULL,
    [report_key] varchar(60) NOT NULL,
    [name] nvarchar(150) NOT NULL,
    [owner_username] nvarchar(80) NOT NULL,
    [scope] varchar(10) NOT NULL,
    [is_default] bit DEFAULT ((0)) NOT NULL,
    [config_json] nvarchar(max) NOT NULL,
    [created_at] datetime2(0) DEFAULT (sysutcdatetime()) NOT NULL,
    [created_by] nvarchar(80) NOT NULL,
    [updated_at] datetime2(0) NULL,
    [updated_by] nvarchar(80) NULL,
    CONSTRAINT [PK_qms_perspective] PRIMARY KEY ([perspective_id]),
    CONSTRAINT [CK_qms_perspective_scope] CHECK ([scope]='shared' OR [scope]='private')
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_qo_material_header_value (
    [qo_material_id] bigint NOT NULL,
    [field_id] int NOT NULL,
    [text_value] nvarchar(255) NULL,
    [numeric_value] decimal(18,4) NULL,
    [date_value] date NULL,
    CONSTRAINT [PK_qms_qo_material_header_value] PRIMARY KEY ([qo_material_id],[field_id])
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_quality_order (
    [quality_order_id] bigint IDENTITY(1,1) NOT NULL,
    [quality_order_no] varchar(20) NOT NULL,
    [arrival_id] bigint NOT NULL,
    [status_code] varchar(20) DEFAULT ('Initial') NOT NULL,
    [opened_at] datetime2(7) NULL,
    [opened_by] nvarchar(80) NULL,
    [closed_at] datetime2(7) NULL,
    [closed_by] nvarchar(80) NULL,
    [close_reason] nvarchar(500) NULL,
    [reopened_at] datetime2(7) NULL,
    [reopened_by] nvarchar(80) NULL,
    [reopen_reason] nvarchar(500) NULL,
    [created_at] datetime2(7) DEFAULT (sysutcdatetime()) NOT NULL,
    [created_by] nvarchar(80) NOT NULL,
    [row_version] timestamp NOT NULL,
    CONSTRAINT [PK_qms_quality_order] PRIMARY KEY ([quality_order_id]),
    CONSTRAINT [UQ_qms_quality_order_quality_order_no] UNIQUE ([quality_order_no]),
    CONSTRAINT [CK_qms_quality_order_status] CHECK ([status_code]='Cancelled' OR [status_code]='Reopened' OR [status_code]='Closed' OR [status_code]='Submitted' OR [status_code]='Open' OR [status_code]='Initial')
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_quality_order_material (
    [qo_material_id] bigint IDENTITY(1,1) NOT NULL,
    [quality_order_id] bigint NOT NULL,
    [arrival_item_id] bigint NOT NULL,
    [material_no] varchar(40) NOT NULL,
    [material_desc] nvarchar(120) NULL,
    [origin] nvarchar(60) NULL,
    [variety] nvarchar(80) NULL,
    [material_class] nvarchar(80) NULL,
    [net_weight] decimal(18,3) NULL,
    [material_size] nvarchar(20) NULL,
    [material_group] varchar(9) NULL,
    [material_group_desc] nvarchar(80) NULL,
    [major_category] nvarchar(80) NULL,
    [brand] nvarchar(80) NULL,
    [pack_type] nvarchar(80) NULL,
    [size_overridden] bit DEFAULT ((0)) NOT NULL,
    [original_material_size] nvarchar(20) NULL,
    [override_material_size] nvarchar(20) NULL,
    [override_reason] nvarchar(500) NULL,
    [override_approved_by] nvarchar(80) NULL,
    [override_approved_at] datetime2(7) NULL,
    [sample_size] smallint NULL,
    CONSTRAINT [PK_qms_quality_order_material] PRIMARY KEY ([qo_material_id])
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_reading_type (
    [reading_type_id] int IDENTITY(1,1) NOT NULL,
    [reading_type_code] varchar(40) NOT NULL,
    [reading_name] nvarchar(100) NOT NULL,
    [value_kind] varchar(20) DEFAULT ('Numeric') NOT NULL,
    [default_unit] varchar(20) NULL,
    [is_active] bit DEFAULT ((1)) NOT NULL,
    [sort_order] int DEFAULT ((0)) NOT NULL,
    [material_group] varchar(20) NULL,
    [is_mandatory] bit DEFAULT ((0)) NOT NULL,
    [display_mode] varchar(20) DEFAULT ('sum') NOT NULL,
    CONSTRAINT [PK_qms_reading_type] PRIMARY KEY ([reading_type_id]),
    CONSTRAINT [CK_qms_reading_type_display_mode] CHECK ([display_mode]='formula' OR [display_mode]='avg' OR [display_mode]='sum_over_size' OR [display_mode]='sum' OR [display_mode]='count' OR [display_mode]='text'),
    CONSTRAINT [CK_qms_reading_type_kind] CHECK ([value_kind]='Text' OR [value_kind]='Numeric')
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_report_log (
    [report_log_id] bigint IDENTITY(1,1) NOT NULL,
    [quality_order_id] bigint NOT NULL,
    [archive_path] nvarchar(1000) NOT NULL,
    [calculation_ver] varchar(20) DEFAULT ('v1') NOT NULL,
    [page_count] int NULL,
    [file_size_bytes] bigint NULL,
    [generated_at] datetime2(7) DEFAULT (sysutcdatetime()) NOT NULL,
    [generated_by] nvarchar(80) NOT NULL,
    CONSTRAINT [PK_qms_report_log] PRIMARY KEY ([report_log_id])
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_sample (
    [sample_id] bigint IDENTITY(1,1) NOT NULL,
    [quality_order_id] bigint NOT NULL,
    [qo_material_id] bigint NOT NULL,
    [sample_no] int NOT NULL,
    [carton_count] smallint NULL,
    [carton_identifier] nvarchar(50) NULL,
    [sample_scope] varchar(20) DEFAULT ('OneCarton') NOT NULL,
    [sample_size] smallint NULL,
    [grower] nvarchar(80) NULL,
    [pallet_no] nvarchar(50) NULL,
    [grower_pallet] nvarchar(50) NULL,
    [pack_code] nvarchar(80) NULL,
    [date_code] nvarchar(80) NULL,
    [label_value] nvarchar(80) NULL,
    [lot_no] nvarchar(80) NULL,
    [packaging_material] nvarchar(80) NULL,
    [created_at] datetime2(7) DEFAULT (sysutcdatetime()) NOT NULL,
    [created_by] nvarchar(80) NOT NULL,
    [updated_at] datetime2(7) NULL,
    [updated_by] nvarchar(80) NULL,
    [is_deleted] bit DEFAULT ((0)) NOT NULL,
    [deleted_at] datetime2(7) NULL,
    [deleted_by] nvarchar(80) NULL,
    [row_version] timestamp NOT NULL,
    [size_overridden] bit DEFAULT ((0)) NOT NULL,
    CONSTRAINT [PK_qms_sample] PRIMARY KEY ([sample_id]),
    CONSTRAINT [CK_qms_sample_scope] CHECK ([sample_scope]='Lot' OR [sample_scope]='Pallet' OR [sample_scope]='MultiCarton' OR [sample_scope]='OneCarton')
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_sample_defect (
    [sample_defect_id] bigint IDENTITY(1,1) NOT NULL,
    [sample_id] bigint NOT NULL,
    [defect_id] int NOT NULL,
    [defect_value] decimal(18,4) NULL,
    [defect_percentage] decimal(9,4) NULL,
    [severity_code] varchar(20) NULL,
    [comment] nvarchar(500) NULL,
    [is_within_tolerance] bit NULL,
    [created_at] datetime2(7) DEFAULT (sysutcdatetime()) NOT NULL,
    [created_by] nvarchar(80) NOT NULL,
    CONSTRAINT [PK_qms_sample_defect] PRIMARY KEY ([sample_defect_id])
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_sample_header_field (
    [field_id] int IDENTITY(1,1) NOT NULL,
    [field_code] varchar(40) NOT NULL,
    [field_name] nvarchar(100) NOT NULL,
    [value_kind] varchar(20) DEFAULT ('Text') NOT NULL,
    [default_unit] varchar(20) NULL,
    [is_active] bit DEFAULT ((1)) NOT NULL,
    [is_mandatory] bit DEFAULT ((0)) NOT NULL,
    [sort_order] int DEFAULT ((500)) NOT NULL,
    [scope] varchar(10) DEFAULT ('Sample') NOT NULL,
    CONSTRAINT [PK_qms_sample_header_field] PRIMARY KEY ([field_id]),
    CONSTRAINT [UQ_qms_sample_header_field_code] UNIQUE ([field_code]),
    CONSTRAINT [CK_qms_sample_header_field_kind] CHECK ([value_kind]='Date' OR [value_kind]='Numeric' OR [value_kind]='Text'),
    CONSTRAINT [CK_qms_sample_header_field_scope] CHECK ([scope]='Material' OR [scope]='Sample')
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_sample_header_value (
    [sample_id] bigint NOT NULL,
    [field_id] int NOT NULL,
    [text_value] nvarchar(255) NULL,
    [numeric_value] decimal(18,4) NULL,
    [date_value] date NULL,
    CONSTRAINT [PK_qms_sample_header_value] PRIMARY KEY ([sample_id],[field_id])
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_sample_observation (
    [observation_id] bigint IDENTITY(1,1) NOT NULL,
    [sample_id] bigint NOT NULL,
    [observation_type_code] varchar(40) NOT NULL,
    [observation_text] nvarchar(max) NOT NULL,
    [severity_code] varchar(20) NULL,
    [created_at] datetime2(7) DEFAULT (sysutcdatetime()) NOT NULL,
    [created_by] nvarchar(80) NOT NULL,
    CONSTRAINT [PK_qms_sample_observation] PRIMARY KEY ([observation_id])
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_sample_reading (
    [reading_id] bigint IDENTITY(1,1) NOT NULL,
    [sample_id] bigint NOT NULL,
    [reading_type_code] varchar(40) NOT NULL,
    [numeric_value] decimal(18,4) NULL,
    [text_value] nvarchar(100) NULL,
    [unit_code] varchar(20) NULL,
    [is_within_spec] bit NULL,
    [reading_sequence] int DEFAULT ((0)) NOT NULL,
    [created_at] datetime2(7) DEFAULT (sysutcdatetime()) NOT NULL,
    [created_by] nvarchar(80) NOT NULL,
    CONSTRAINT [PK_qms_sample_reading] PRIMARY KEY ([reading_id])
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_sap_container_cache (
    [cache_id] bigint IDENTITY(1,1) NOT NULL,
    [container_no] varchar(35) NOT NULL,
    [bol_no] varchar(35) NOT NULL,
    [ebeln] varchar(10) NOT NULL,
    [ebelp] varchar(5) NOT NULL,
    [material_no] varchar(40) NOT NULL,
    [plant] varchar(4) NOT NULL,
    [storage_loc] varchar(4) NOT NULL,
    [batch_no] varchar(20) NULL,
    [vendor_no] varchar(10) NULL,
    [vendor_name] nvarchar(80) NULL,
    [material_desc] nvarchar(120) NULL,
    [material_group] varchar(9) NULL,
    [doc_date] date NULL,
    [arrival_date] date NULL,
    [receive_date] date NULL,
    [quantity] decimal(18,3) NULL,
    [uom] varchar(3) NULL,
    [payload_json] nvarchar(max) NULL,
    [has_arrival] bit DEFAULT ((0)) NOT NULL,
    [arrival_id] bigint NULL,
    [first_seen_at] datetime2(7) DEFAULT (sysutcdatetime()) NOT NULL,
    [last_seen_at] datetime2(7) DEFAULT (sysutcdatetime()) NOT NULL,
    [po_type] varchar(4) NULL,
    [sto] varchar(10) NULL,
    CONSTRAINT [PK_qms_sap_container_cache] PRIMARY KEY ([cache_id])
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_sap_sync_log (
    [sync_log_id] bigint IDENTITY(1,1) NOT NULL,
    [endpoint_key] varchar(40) NOT NULL,
    [started_at] datetime2(7) DEFAULT (sysutcdatetime()) NOT NULL,
    [completed_at] datetime2(7) NULL,
    [success] bit NULL,
    [rows_synced] int NULL,
    [message] nvarchar(2000) NULL,
    [triggered_by] nvarchar(80) NOT NULL,
    [trigger_source] varchar(20) DEFAULT ('Manual') NOT NULL,
    CONSTRAINT [PK_qms_sap_sync_log] PRIMARY KEY ([sync_log_id]),
    CONSTRAINT [CK_qms_sap_sync_log_source] CHECK ([trigger_source]='Auto' OR [trigger_source]='Manual')
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_shipment_snapshot (
    [shipment_snapshot_id] bigint IDENTITY(1,1) NOT NULL,
    [arrival_id] bigint NOT NULL,
    [internal_shipment_no] varchar(20) NOT NULL,
    [loading_date] date NULL,
    [sailing_date] date NULL,
    [examination_date] date NULL,
    [arrival_date] date NULL,
    [unloading_date] date NULL,
    [inspection_date] date NULL,
    [transit_days] smallint NULL,
    [time_bar] smallint NULL,
    [loading_port] nvarchar(60) NULL,
    [loading_country] nvarchar(60) NULL,
    [arrival_place] nvarchar(80) NULL,
    [vessel_name] nvarchar(80) NULL,
    [voyage_number] nvarchar(50) NULL,
    [pullout_date] date NULL,
    [receive_date] date NULL,
    [time_bar_exceeded] bit NULL,
    [inspection_point] nvarchar(60) NULL,
    [joint_survey] bit NULL,
    [status_code] varchar(20) DEFAULT ('Draft') NOT NULL,
    CONSTRAINT [PK_qms_shipment_snapshot] PRIMARY KEY ([shipment_snapshot_id]),
    CONSTRAINT [UQ_qms_shipment_snapshot_internal_shipment_no] UNIQUE ([internal_shipment_no]),
    CONSTRAINT [UQ_qms_shipment_snapshot_arrival_id] UNIQUE ([arrival_id]),
    CONSTRAINT [CK_qms_shipment_snapshot_status] CHECK ([status_code]='Confirmed' OR [status_code]='Draft')
);

-- ---------------------------------------------------------------------------
CREATE TABLE qms.qms_status_history (
    [status_history_id] bigint IDENTITY(1,1) NOT NULL,
    [entity_type] varchar(40) NOT NULL,
    [entity_id] bigint NOT NULL,
    [old_status] varchar(30) NULL,
    [new_status] varchar(30) NOT NULL,
    [reason] nvarchar(500) NULL,
    [changed_at] datetime2(7) DEFAULT (sysutcdatetime()) NOT NULL,
    [changed_by] nvarchar(80) NOT NULL,
    CONSTRAINT [PK_qms_status_history] PRIMARY KEY ([status_history_id])
);

-- ---------------------------------------------------------------------------
-- Foreign keys
--   FKs that pointed at the old local Users table are dropped: identity now
--   lives in portal.User. Columns holding a username stay as plain strings.
-- ---------------------------------------------------------------------------

ALTER TABLE qms.qms_arrival_checklist ADD CONSTRAINT [FK_qms_arrival_checklist_arrival_id] FOREIGN KEY ([arrival_id]) REFERENCES qms.qms_arrival ([arrival_id]);
ALTER TABLE qms.qms_arrival_field_value ADD CONSTRAINT [FK_qms_arrival_field_value_arrival] FOREIGN KEY ([arrival_id]) REFERENCES qms.qms_arrival ([arrival_id]) ON DELETE CASCADE;
ALTER TABLE qms.qms_arrival_field_value ADD CONSTRAINT [FK_qms_arrival_field_value_field] FOREIGN KEY ([field_id]) REFERENCES qms.qms_arrival_field ([field_id]);
ALTER TABLE qms.qms_arrival_item ADD CONSTRAINT [FK_qms_arrival_item_arrival_id] FOREIGN KEY ([arrival_id]) REFERENCES qms.qms_arrival ([arrival_id]);
ALTER TABLE qms.qms_arrival_sap_snapshot ADD CONSTRAINT [FK_qms_arrival_sap_snapshot_arrival_id] FOREIGN KEY ([arrival_id]) REFERENCES qms.qms_arrival ([arrival_id]);
ALTER TABLE qms.qms_claim ADD CONSTRAINT [FK_qms_claim_qo] FOREIGN KEY ([quality_order_id]) REFERENCES qms.qms_quality_order ([quality_order_id]);
ALTER TABLE qms.qms_claim_note ADD CONSTRAINT [FK_qms_claim_note_claim] FOREIGN KEY ([claim_id]) REFERENCES qms.qms_claim ([claim_id]) ON DELETE CASCADE;
ALTER TABLE qms.qms_claim_read_marker ADD CONSTRAINT [FK_qms_claim_read_marker_claim] FOREIGN KEY ([claim_id]) REFERENCES qms.qms_claim ([claim_id]) ON DELETE CASCADE;
ALTER TABLE qms.qms_defect_catalog ADD CONSTRAINT [FK_qms_defect_catalog_category] FOREIGN KEY ([defect_category]) REFERENCES qms.qms_defect_category ([category_name]) ON UPDATE CASCADE;
ALTER TABLE qms.qms_image_link ADD CONSTRAINT [FK_qms_image_link_image_id] FOREIGN KEY ([image_id]) REFERENCES qms.qms_image_asset ([image_id]);
ALTER TABLE qms.qms_material_group_defect ADD CONSTRAINT [FK_qms_material_group_defect_defect_id] FOREIGN KEY ([defect_id]) REFERENCES qms.qms_defect_catalog ([defect_id]);
ALTER TABLE qms.qms_material_group_reading ADD CONSTRAINT [FK_qms_material_group_reading_reading_type_id] FOREIGN KEY ([reading_type_id]) REFERENCES qms.qms_reading_type ([reading_type_id]);
ALTER TABLE qms.qms_qo_material_header_value ADD CONSTRAINT [FK_qms_qmhv_field] FOREIGN KEY ([field_id]) REFERENCES qms.qms_sample_header_field ([field_id]);
ALTER TABLE qms.qms_qo_material_header_value ADD CONSTRAINT [FK_qms_qmhv_material] FOREIGN KEY ([qo_material_id]) REFERENCES qms.qms_quality_order_material ([qo_material_id]) ON DELETE CASCADE;
ALTER TABLE qms.qms_quality_order ADD CONSTRAINT [FK_qms_quality_order_arrival_id] FOREIGN KEY ([arrival_id]) REFERENCES qms.qms_arrival ([arrival_id]);
ALTER TABLE qms.qms_quality_order_material ADD CONSTRAINT [FK_qms_quality_order_material_arrival_item_id] FOREIGN KEY ([arrival_item_id]) REFERENCES qms.qms_arrival_item ([arrival_item_id]);
ALTER TABLE qms.qms_quality_order_material ADD CONSTRAINT [FK_qms_quality_order_material_quality_order_id] FOREIGN KEY ([quality_order_id]) REFERENCES qms.qms_quality_order ([quality_order_id]);
ALTER TABLE qms.qms_report_log ADD CONSTRAINT [FK_qms_report_log_quality_order_id] FOREIGN KEY ([quality_order_id]) REFERENCES qms.qms_quality_order ([quality_order_id]);
ALTER TABLE qms.qms_sample ADD CONSTRAINT [FK_qms_sample_qo_material_id] FOREIGN KEY ([qo_material_id]) REFERENCES qms.qms_quality_order_material ([qo_material_id]);
ALTER TABLE qms.qms_sample ADD CONSTRAINT [FK_qms_sample_quality_order_id] FOREIGN KEY ([quality_order_id]) REFERENCES qms.qms_quality_order ([quality_order_id]);
ALTER TABLE qms.qms_sample_defect ADD CONSTRAINT [FK_qms_sample_defect_defect_id] FOREIGN KEY ([defect_id]) REFERENCES qms.qms_defect_catalog ([defect_id]);
ALTER TABLE qms.qms_sample_defect ADD CONSTRAINT [FK_qms_sample_defect_sample_id] FOREIGN KEY ([sample_id]) REFERENCES qms.qms_sample ([sample_id]);
ALTER TABLE qms.qms_sample_header_value ADD CONSTRAINT [FK_qms_sample_header_value_field] FOREIGN KEY ([field_id]) REFERENCES qms.qms_sample_header_field ([field_id]);
ALTER TABLE qms.qms_sample_header_value ADD CONSTRAINT [FK_qms_sample_header_value_sample] FOREIGN KEY ([sample_id]) REFERENCES qms.qms_sample ([sample_id]) ON DELETE CASCADE;
ALTER TABLE qms.qms_sample_observation ADD CONSTRAINT [FK_qms_sample_observation_sample_id] FOREIGN KEY ([sample_id]) REFERENCES qms.qms_sample ([sample_id]);
ALTER TABLE qms.qms_sample_reading ADD CONSTRAINT [FK_qms_sample_reading_sample_id] FOREIGN KEY ([sample_id]) REFERENCES qms.qms_sample ([sample_id]);
ALTER TABLE qms.qms_sap_container_cache ADD CONSTRAINT [FK_qms_sap_container_cache_arrival_id] FOREIGN KEY ([arrival_id]) REFERENCES qms.qms_arrival ([arrival_id]);
ALTER TABLE qms.qms_shipment_snapshot ADD CONSTRAINT [FK_qms_shipment_snapshot_arrival_id] FOREIGN KEY ([arrival_id]) REFERENCES qms.qms_arrival ([arrival_id]);

-- ---------------------------------------------------------------------------
-- Indexes
-- ---------------------------------------------------------------------------

CREATE INDEX [IX_qms_arrival_bol] ON qms.qms_arrival ([bol_no]);
CREATE INDEX [IX_qms_arrival_container] ON qms.qms_arrival ([container_no]);
CREATE INDEX [IX_qms_arrival_plant_active] ON qms.qms_arrival ([plant],[created_at] DESC) WHERE ([status_code]<>'Cancelled');
CREATE INDEX [IX_qms_arrival_status] ON qms.qms_arrival ([status_code]);
CREATE INDEX [IX_qms_arrival_field_value_field] ON qms.qms_arrival_field_value ([field_id]);
CREATE UNIQUE INDEX [UX_qms_arrival_item_natural] ON qms.qms_arrival_item ([arrival_id],[ebeln],[ebelp],[material_no],[plant],[storage_location],[batch_no]);
CREATE INDEX [IX_qms_arrival_sap_snapshot_arrival] ON qms.qms_arrival_sap_snapshot ([arrival_id]);
CREATE INDEX [IX_qms_audit_log_actor] ON qms.qms_audit_log ([changed_by],[changed_at] DESC);
CREATE INDEX [IX_qms_audit_log_entity] ON qms.qms_audit_log ([entity_type],[entity_id]);
CREATE INDEX [IX_qms_audit_log_entity_type] ON qms.qms_audit_log ([entity_type],[changed_at] DESC);
CREATE INDEX [IX_qms_audit_log_filter] ON qms.qms_audit_log ([changed_at] DESC) INCLUDE ([entity_type],[action_code],[changed_by]);
CREATE INDEX [IX_qms_claim_status] ON qms.qms_claim ([claim_status]) INCLUDE ([quality_order_id]);
CREATE INDEX [IX_qms_claim_note_claim] ON qms.qms_claim_note ([claim_id],[created_at]);
CREATE INDEX [IX_qms_document_owner] ON qms.qms_document ([owner_type],[owner_id]) WHERE ([is_deleted]=(0));
CREATE INDEX [IX_qms_image_link_owner] ON qms.qms_image_link ([owner_type],[owner_id]);
CREATE INDEX [IX_qms_material_group_defect_lookup] ON qms.qms_material_group_defect ([material_group],[major_category],[is_active]);
CREATE INDEX [IX_qms_material_group_reading_lookup] ON qms.qms_material_group_reading ([material_group],[major_category],[is_active]);
CREATE INDEX [IX_qms_perspective_owner_report] ON qms.qms_perspective ([owner_username],[report_key]);
CREATE INDEX [IX_qms_perspective_shared] ON qms.qms_perspective ([report_key]) WHERE ([scope]='shared');
CREATE UNIQUE INDEX [UX_qms_perspective_default] ON qms.qms_perspective ([owner_username],[report_key]) WHERE ([is_default]=(1));
CREATE INDEX [IX_qms_qo_material_header_value_mat] ON qms.qms_qo_material_header_value ([qo_material_id]);
CREATE INDEX [IX_qms_quality_order_status] ON qms.qms_quality_order ([status_code]);
CREATE UNIQUE INDEX [UX_qms_quality_order_active_per_arrival] ON qms.qms_quality_order ([arrival_id]) WHERE ([status_code]<>'Cancelled');
CREATE INDEX [IX_qms_quality_order_material_mat] ON qms.qms_quality_order_material ([material_no]);
CREATE INDEX [IX_qms_quality_order_material_qo] ON qms.qms_quality_order_material ([quality_order_id]);
CREATE INDEX [IX_qms_reading_type_group] ON qms.qms_reading_type ([material_group]) INCLUDE ([is_active],[sort_order]);
CREATE UNIQUE INDEX [UQ_qms_reading_type_global_code] ON qms.qms_reading_type ([reading_type_code]) WHERE ([material_group] IS NULL);
CREATE UNIQUE INDEX [UQ_qms_reading_type_per_group_code] ON qms.qms_reading_type ([material_group],[reading_type_code]) WHERE ([material_group] IS NOT NULL);
CREATE INDEX [IX_qms_report_log_qo] ON qms.qms_report_log ([quality_order_id]);
CREATE INDEX [IX_qms_sample_created_at_active] ON qms.qms_sample ([created_at] DESC) INCLUDE ([sample_id],[qo_material_id],[quality_order_id],[sample_no],[sample_size]) WHERE ([is_deleted]=(0));
CREATE INDEX [IX_qms_sample_qo] ON qms.qms_sample ([quality_order_id]) WHERE ([is_deleted]=(0));
CREATE UNIQUE INDEX [UX_qms_sample_active] ON qms.qms_sample ([qo_material_id],[sample_no]) WHERE ([is_deleted]=(0));
CREATE UNIQUE INDEX [UX_qms_sample_defect_one_per_sample] ON qms.qms_sample_defect ([sample_id],[defect_id]);
CREATE INDEX [IX_qms_sample_header_value_sample] ON qms.qms_sample_header_value ([sample_id]);
CREATE INDEX [IX_qms_sample_observation_sample] ON qms.qms_sample_observation ([sample_id]);
CREATE INDEX [IX_qms_sample_reading_sample] ON qms.qms_sample_reading ([sample_id]);
CREATE INDEX [IX_qms_sap_container_cache_pending] ON qms.qms_sap_container_cache ([doc_date] DESC,[container_no],[bol_no],[ebeln]) WHERE ([has_arrival]=(0));
CREATE INDEX [IX_qms_sap_container_cache_triplet] ON qms.qms_sap_container_cache ([container_no],[bol_no],[ebeln]);
CREATE UNIQUE INDEX [UX_qms_sap_container_cache_key] ON qms.qms_sap_container_cache ([container_no],[bol_no],[ebeln],[ebelp],[material_no],[plant],[storage_loc],[batch_no]);
CREATE UNIQUE INDEX [UX_qms_sap_container_cache_key_nullbatch] ON qms.qms_sap_container_cache ([container_no],[bol_no],[ebeln],[ebelp],[material_no],[plant],[storage_loc]) WHERE ([batch_no] IS NULL);
CREATE INDEX [IX_qms_sap_sync_log_endpoint] ON qms.qms_sap_sync_log ([endpoint_key],[started_at] DESC);
CREATE INDEX [IX_qms_status_history_entity] ON qms.qms_status_history ([entity_type],[entity_id]);

