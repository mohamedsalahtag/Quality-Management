# Quality Management System - Extended Assessment and Execution Plan

> **AI agents — read `PROJECT_STATE.md` first.** This plan is the original (2026-05-04) strategic spec — table layouts, status codes, the 16-week phase order. The **live state** of the deployed app (build path, Windows Service config, current role model, AD-first auth, View-as feature, decisions log) lives in `PROJECT_STATE.md` at the repo root. Where the two disagree, the live-state doc is correct and this plan is historical context.
>
> Notable departures from this plan, recorded in `PROJECT_STATE.md §8`:
> - Auth was originally specced as local-only BCrypt with roles `QCStaff` / `QCManager` / `SiteAdmin`. **Live system** uses AD bind + 4 roles (`Viewer` / `Operator` / `Manager` / `SiteAdmin`) — see `V12__role_overhaul.sql`.
> - `/Account/Setup` seed-admin bootstrap was removed once AD started working.
> - View-site-as role impersonation was added to support security testing.

**Project context**  
This plan reassesses the Quality Management System using the provided business input document, Miro outputs, SAP-style table structure workbook, and screen mockups. It applies the latest design decision: **all upstream SAP data before Customer Arrival will be read from existing SAP S/4HANA CDS Views exposed through OData**, and **all arrival checklist, quality order, sample, observation, reading, defect, image, audit, and reporting data will be stored in SQL Server**. The application will **not interfere with SAP standard Quality Management**.

**Primary recommendation**  
Build the system as an **external quality execution and reporting application**. SAP remains the upstream system of record for PO, BOL, container, vendor, shipment, material, plant, storage location, quantities, and master classification attributes. SQL Server becomes the system of record only from the **Customer Arrival / Arrival Overview** stage forward.

---

## 1. Reconfirmed Business Scope

The source process begins after SAP purchasing and logistics work is already performed. Purchase orders are created in SAP S/4HANA for imported fruits and vegetables; logistics updates shipment details, especially BOL number; receiving splits quantities by container; and then the quality process starts by creating a preliminary inspection called Arrival Overview. The input also states that the user searches by container and, if the same container appears under older BOLs, the system must force BOL selection to avoid inspecting the wrong container.

The core functional chain is:

1. SAP S/4HANA creates and maintains PO, vendor, material, BOL, shipment, and container-related data.
2. External QMS reads these details through CDS/OData.
3. User creates Customer Arrival / Arrival Overview in QMS.
4. QMS generates an internal shipment or arrival reference for downstream navigation.
5. User opens a quality order for one arrival and selected materials.
6. User creates one or more samples per material.
7. Each sample captures sample attributes, readings, observations, and defects.
8. User uploads arrival and inspection images.
9. User closes quality order.
10. System generates PDF quality control report with shipment data, material data, samples, defects, calculations, and image thumbnails.

---

## 2. Corrected System Boundary

### In scope for SAP CDS/OData

- Purchase order header and item data.
- BOL number.
- Container number.
- Vendor/supplier identity and name.
- Material number, material description, material group, material category, variety, origin, class, brand, pack type, net weight, material size, and UoM where available.
- Plant, storage location, quantity, batch, and receiving context.
- Shipment attributes such as loading date, sailing date, examination date, arrival date, transit days, loading country, loading port, arrival place, vessel name, voyage number, transport mode, truck number/type, seal number, estimated arrival date, and other logistics attributes exposed by CDS.

### In scope for SQL Server

- User-selected arrival instance.
- Snapshot of SAP data used at arrival creation time.
- Arrival checklist answers.
- Quality order header and lifecycle.
- Quality order material rows.
- Samples.
- Sample readings.
- Sample observations.
- Defects captured per sample.
- Material-size override, approval, and reason.
- Images and thumbnails.
- Report generation logs.
- Status history.
- Audit trail.
- System configuration.

### Explicitly out of scope

- No posting to SAP QM inspection lots.
- No SAP usage decision.
- No change to SAP material master from this application.
- No direct SQL read against SAP tables.
- No custom update of SAP standard purchasing/logistics tables.

---

## 3. Deep Assessment of Current Design

### 3.1 Strong points

- The process starts at the right operational point: after logistics/receiving have enough container and BOL data.
- The user requirement correctly identifies container ambiguity across BOLs.
- The design separates arrival checklist from quality order, which is good because not every arrival must immediately become a completed inspection.
- The report concept is clear: shipment details, material details, sample summary, defects, and images.
- The image requirement is practical: arrival images and quality inspection images must be separate.
- Admin-controlled thumbnail sizing is useful for both screen performance and PDF report layout.

### 3.2 Main gaps to fix

| Area | Gap | Required correction |
|---|---|---|
| Data ownership | Earlier design mixed SAP upstream data and QMS transactional data. | Treat SAP CDS/OData as upstream read model; SQL Server stores QMS execution from arrival onward. |
| Normalization | Defects are currently represented as fixed fields in line tables. | Store defects as rows in `sample_defect`, linked to `defect_catalog`. |
| Sample model | Sample attributes, readings, observations, and defects are mixed. | Split sample header, reading rows, observation rows, defect rows, and optional sample image links. |
| Container identity | Container alone is not unique. | Use `container_no + bol_no + purchase_order + purchase_order_item + material_no + batch/plant` where applicable. |
| Material size | Override exists but lacks governance. | Add override flag, reason, old value, new value, approver, timestamp, audit row. |
| Images | Image flags exist as CHAR fields, but image metadata is not normalized. | Store image assets separately and link them to arrival, quality order, material, or sample. |
| Status | Status text exists but no controlled lifecycle. | Use status codes, status history, allowed transitions, and editability matrix. |
| Report | PDF expected but calculation logic is not formalized. | Store report snapshot and calculation version to make reports reproducible. |
| Concurrency | Shipment number/sample number generation can collide. | Use SQL sequences/identity and unique filtered indexes. |

---

## 4. Target Architecture

### 4.1 Components

1. **Frontend Web App**
   - Arrival search and arrival checklist.
   - Quality order cockpit.
   - Material tab.
   - Sample entry panel.
   - Dynamic defect panels by material group.
   - Image gallery with thumbnails.
   - PDF preview and generation.
   - Admin screens for configuration.

2. **Backend API**
   - .NET Core Web API is recommended because it integrates strongly with SQL Server, background jobs, authentication, and PDF/image pipelines.
   - Responsible for SAP OData consumption, QMS transactions, validation, file upload, report generation, and audit.

3. **SAP Integration Layer**
   - Reads already-built CDS Views via OData.
   - Provides search by container, BOL, PO, material, vendor, and shipment.
   - Handles pagination, filtering, retries, token renewal, error mapping, and snapshot creation.

4. **SQL Server Database**
   - Stores QMS-owned transactions and SAP snapshots at time of arrival/order creation.
   - Enforces keys, foreign keys, status constraints, audit, and soft delete rules.

5. **File/Object Storage**
   - Stores original images and derived thumbnails.
   - SQL Server stores metadata, ownership, display sequence, checksum, upload user, and usage scope.

6. **Background Workers**
   - Thumbnail generation.
   - PDF generation.
   - SAP material cache refresh if needed.
   - Cleanup of orphaned uploads.
   - Report archive generation.

---

## 5. SAP CDS/OData Read Strategy

### 5.1 Assumption

All CDS Views already exist and are published as OData services. The application must act as a consumer only.

### 5.2 Required OData read models

| Logical read model | Purpose | Minimum fields |
|---|---|---|
| Container search | Find containers and disambiguate by BOL/PO. | container_no, bol_no, ebeln, ebelp, material_no, quantity, plant, storage_location, batch, vendor, vendor_name |
| Arrival shipment details | Populate arrival and shipment snapshot. | vessel, voyage, carrier, loading/sailing/examination/arrival/unloading dates, transit days, loading port/country, arrival place, seal, truck, transport mode |
| Material details | Populate material tab and sample defaults. | material_no, description, group, group description, major category, origin, variety, class, net weight, material size, brand, pack type, UoM |
| Quantity and item context | Validate received quantity and material rows. | ebeln, ebelp, menge, meins, batch, plant, storage location |
| Vendor/supplier | Report display and filtering. | supplier id, name, country, optional region |

### 5.3 Search behavior

Search must support these scenarios:

1. User enters only **container number**.
   - If exactly one active SAP row is found, show it.
   - If multiple rows exist across BOLs or POs, force user to select BOL/PO.
   - If no row is found, show a controlled error and allow search by BOL or PO.

2. User enters **container + BOL**.
   - Return rows for that exact pair.
   - If more than one PO/material row exists, show all rows in the grid.

3. User enters **PO**.
   - Return all related container/material rows for inspection selection.

4. User opens existing Arrival Overview.
   - Read from SQL snapshot, not SAP, unless user presses an authorized refresh action before quality order opening.

### 5.4 Snapshot rule

At the moment Arrival Overview is saved, write a **SAP data snapshot** into SQL Server. This is not master-data ownership; it is an audit snapshot showing exactly what the inspector saw when the process started.

Recommended rule:

- Before arrival save: data is live from SAP OData.
- After arrival save: data is read from SQL snapshot.
- Authorized refresh allowed only while arrival status is Draft and no quality order has been opened.
- Once quality order is Open or Closed, snapshot is locked.

---

## 6. Corrected Normalized SQL Server Design

The current spreadsheet has four broad groups: Arrival, Shipment, Header, and Lines. The design should be normalized into the following tables.

### 6.1 Core identity tables

#### `qms_arrival`
Stores the Customer Arrival / Arrival Overview header.

| Column | Type suggestion | Notes |
|---|---:|---|
| arrival_id | bigint identity PK | Internal technical key. |
| arrival_no | varchar(20) unique | User-facing number. |
| source_system | varchar(20) | Example: S4HANA. |
| bol_no | varchar(35) | From SAP OData snapshot. |
| container_no | varchar(35) | From SAP OData snapshot. |
| ebeln | varchar(10) | SAP PO number. |
| bukrs | varchar(4) | Company code. |
| vendor_no | varchar(10) | Supplier. |
| vendor_name | nvarchar(80) | Snapshot. |
| status_code | varchar(20) | Draft, Completed, Cancelled. |
| created_at | datetime2 | Use UTC. |
| created_by | nvarchar(80) | Application user. |
| completed_at | datetime2 null | Set when arrival checklist completed. |
| row_version | rowversion | Optimistic locking. |

#### `qms_arrival_sap_snapshot`
Stores exact SAP/OData values used at arrival save.

| Column | Type suggestion | Notes |
|---|---:|---|
| snapshot_id | bigint identity PK |  |
| arrival_id | bigint FK | Parent arrival. |
| odata_service_name | nvarchar(120) | Which CDS/OData service returned the data. |
| odata_query_hash | char(64) | Hash of query/filter. |
| payload_json | nvarchar(max) | Full immutable source payload. |
| captured_at | datetime2 | UTC. |
| captured_by | nvarchar(80) | User or system. |

#### `qms_arrival_item`
One row per SAP material/item/container line selected for arrival.

| Column | Type suggestion | Notes |
|---|---:|---|
| arrival_item_id | bigint identity PK |  |
| arrival_id | bigint FK |  |
| ebeln | varchar(10) | PO. |
| ebelp | varchar(5) | PO item. |
| material_no | varchar(40) | MATNR compatible. |
| material_desc | nvarchar(120) | Snapshot. |
| plant | varchar(4) | WERKS. |
| storage_location | varchar(4) | LGORT. |
| batch_no | varchar(20) null | CHARG. |
| quantity | decimal(18,3) | SAP quantity. |
| uom | varchar(3) | MEINS. |
| material_group | varchar(9) | MATKL. |
| material_group_desc | nvarchar(80) | Snapshot. |
| major_category | nvarchar(80) | Example: Apples. |
| row_version | rowversion |  |

**Unique index:** `(arrival_id, ebeln, ebelp, material_no, plant, storage_location, batch_no)`.

---

### 6.2 Arrival checklist tables

#### `qms_arrival_checklist`
Stores checklist-level answers, not image files.

| Column | Type suggestion | Notes |
|---|---:|---|
| checklist_id | bigint identity PK |  |
| arrival_id | bigint unique FK | One checklist per arrival. |
| seal_no | varchar(30) null | From SAP or user check. |
| carrier_name | nvarchar(80) null | Snapshot or user-entered. |
| seal_intact | bit null | External inspection. |
| seal_matches_documents | bit null | External inspection. |
| external_damage_exists | bit null | External inspection. |
| set_temperature | decimal(6,2) null | Temperature unit reading. |
| display_temperature | decimal(6,2) null | Temperature unit reading. |
| cargo_smell_normal | bit null | Internal inspection. |
| visual_cargo_acceptable | bit null | Internal inspection. |
| cargo_shifted_collapsed_water | bit null | Internal inspection. |
| pulp_temp_front | decimal(6,2) null | Current workbook has multiple temp fields. |
| pulp_temp_middle | decimal(6,2) null | Normalize spelling. |
| pulp_temp_back | decimal(6,2) null |  |
| data_logger_located | bit null |  |
| data_logger_serial | nvarchar(50) null |  |
| data_logger_photo_taken | bit null | Flag only; actual image in image table. |
| logger_handed_over | bit null |  |
| logger_active_data_available | bit null |  |
| logger_temperature | decimal(6,2) null |  |
| notes | nvarchar(max) null | Replaces generic logger_note when needed. |

#### `qms_shipment_snapshot`
Stores shipment details shown in the quality order shipment tab and report.

| Column | Type suggestion | Notes |
|---|---:|---|
| shipment_snapshot_id | bigint identity PK |  |
| arrival_id | bigint unique FK | One shipment snapshot per arrival. |
| internal_shipment_no | varchar(20) unique | Generated by QMS if needed. |
| loading_date | date null |  |
| sailing_date | date null |  |
| examination_date | date null |  |
| arrival_date | date null |  |
| unloading_date | date null |  |
| inspection_date | date null |  |
| transit_days | smallint null |  |
| time_bar | smallint null |  |
| loading_port | nvarchar(60) null |  |
| loading_country | nvarchar(60) null |  |
| arrival_place | nvarchar(80) null |  |
| vessel_name | nvarchar(80) null |  |
| voyage_number | nvarchar(50) null |  |
| pullout_date | date null |  |
| receive_date | date null |  |
| time_bar_exceeded | bit null |  |
| inspection_point | nvarchar(60) null |  |
| joint_survey | bit null |  |
| status_code | varchar(20) | Draft/Confirmed. |

---

### 6.3 Quality order tables

#### `qms_quality_order`
Header table for a quality order created after Arrival Overview completion.

| Column | Type suggestion | Notes |
|---|---:|---|
| quality_order_id | bigint identity PK |  |
| quality_order_no | varchar(20) unique | User-facing number, replaces QCNO. |
| arrival_id | bigint FK | Quality order requires completed arrival. |
| status_code | varchar(20) | Initial, Open, Closed, Reopened, Cancelled. |
| opened_at | datetime2 null | Set by Open Quality Order. |
| opened_by | nvarchar(80) null |  |
| closed_at | datetime2 null | Set by Close Quality Order. |
| closed_by | nvarchar(80) null |  |
| close_reason | nvarchar(500) null |  |
| reopened_at | datetime2 null | Last reopen. Full history is in status table. |
| reopened_by | nvarchar(80) null | Site Admin only. |
| reopen_reason | nvarchar(500) null | Mandatory. |
| row_version | rowversion |  |

**Constraint:** only one active quality order per arrival unless business later approves multi-order scenarios.

#### `qms_quality_order_material`
Materials selected inside a quality order.

| Column | Type suggestion | Notes |
|---|---:|---|
| qo_material_id | bigint identity PK |  |
| quality_order_id | bigint FK |  |
| arrival_item_id | bigint FK | Material must come from arrival item. |
| material_no | varchar(40) | Snapshot copy for performance/report. |
| material_desc | nvarchar(120) | Snapshot. |
| origin | nvarchar(60) null |  |
| variety | nvarchar(80) null |  |
| class | nvarchar(80) null | Avoid reserved word in SQL; use material_class if preferred. |
| net_weight | decimal(18,3) null |  |
| material_size | nvarchar(20) null | Carton count/size. |
| material_group | varchar(9) |  |
| material_group_desc | nvarchar(80) |  |
| major_category | nvarchar(80) |  |
| brand | nvarchar(80) null |  |
| pack_type | nvarchar(80) null |  |
| size_overridden | bit default 0 |  |
| original_material_size | nvarchar(20) null |  |
| override_material_size | nvarchar(20) null |  |
| override_reason | nvarchar(500) null | Mandatory if overridden. |
| override_approved_by | nvarchar(80) null | Optional approval. |
| override_approved_at | datetime2 null |  |

---

### 6.4 Sample normalization

The sample should not be one wide table containing all possible defects and readings. A sample is a parent record with several child collections.

#### `qms_sample`
One inspection sample for one quality order material.

| Column | Type suggestion | Notes |
|---|---:|---|
| sample_id | bigint identity PK |  |
| quality_order_id | bigint FK | Useful for fast query. |
| qo_material_id | bigint FK | Parent material. |
| sample_no | int | Sequential within material or order. |
| carton_count | smallint null | Current workbook field. |
| carton_identifier | nvarchar(50) null | If inspecting specific carton(s). |
| sample_scope | varchar(20) | OneCarton, MultiCarton, Pallet, Lot. |
| sample_size | smallint null | Default from material size or manual. |
| grower | nvarchar(80) null |  |
| pallet_no | nvarchar(50) null |  |
| grower_pallet | nvarchar(50) null | Current field should be clarified. |
| pack_code | nvarchar(80) null |  |
| date_code | nvarchar(80) null |  |
| label_value | nvarchar(80) null | Current LABLE spelling corrected. |
| lot_no | nvarchar(80) null |  |
| packaging_material | nvarchar(80) null |  |
| created_at | datetime2 |  |
| created_by | nvarchar(80) |  |
| updated_at | datetime2 null |  |
| updated_by | nvarchar(80) null |  |
| is_deleted | bit default 0 | Soft delete. |
| deleted_at | datetime2 null |  |
| deleted_by | nvarchar(80) null |  |
| row_version | rowversion |  |

**Unique index:** `(qo_material_id, sample_no)` where `is_deleted = 0`.

#### `qms_sample_reading`
Generic numeric or text readings per sample.

| Column | Type suggestion | Notes |
|---|---:|---|
| reading_id | bigint identity PK |  |
| sample_id | bigint FK |  |
| reading_type_code | varchar(40) | BRIX, PUC, PHC, STICKER, FIRMNESS, GROSS_WEIGHT, NET_WEIGHT, TARA, COLOUR, WAXING, DOWNGRADE. |
| numeric_value | decimal(18,4) null | For measurable readings. |
| text_value | nvarchar(100) null | For GOOD/BAD or descriptive readings. |
| unit_code | varchar(20) null | Kg, %, count, etc. |
| is_within_spec | bit null | Calculated or entered. |
| reading_sequence | int | Display order. |
| created_at | datetime2 |  |
| created_by | nvarchar(80) |  |

This table fixes the workbook problem where readings such as BRIX, PUC, PHC, STICKER, FIRMNESS, WAXING, DOWNGRADE, GROSS_WEIGHT, NET_WEIGHT, and TARA were placed as fixed columns in Lines.

#### `qms_sample_observation`
Captures narrative or coded observations that are not defects.

| Column | Type suggestion | Notes |
|---|---:|---|
| observation_id | bigint identity PK |  |
| sample_id | bigint FK |  |
| observation_type_code | varchar(40) | Appearance, Color, Packaging, Temperature, InspectorNote, Other. |
| observation_text | nvarchar(max) |  |
| severity_code | varchar(20) null | Info, Warning, Critical. |
| created_at | datetime2 |  |
| created_by | nvarchar(80) |  |

#### `qms_defect_catalog`
Master catalog of all possible defects.

| Column | Type suggestion | Notes |
|---|---:|---|
| defect_id | int identity PK |  |
| defect_code | varchar(50) unique | WASTE_DECAY, SCALD, BRUISING, CRACK, etc. |
| defect_name | nvarchar(100) | Display name. |
| defect_category | varchar(20) | Major, Minor, Critical, Other. |
| default_unit | varchar(20) | Count, %, Score. |
| is_active | bit |  |
| sort_order | int | UI/report order. |

#### `qms_material_group_defect`
Controls which defects appear for which material group/category.

| Column | Type suggestion | Notes |
|---|---:|---|
| material_group_defect_id | int identity PK |  |
| material_group | varchar(9) | From SAP material group. |
| major_category | nvarchar(80) null | Optional if material group is too broad. |
| defect_id | int FK |  |
| is_required | bit | Whether UI must show/capture. |
| is_active | bit |  |
| display_section | varchar(20) | Major, Minor, Readings. |
| sort_order | int |  |

#### `qms_sample_defect`
Stores actual defect values per sample.

| Column | Type suggestion | Notes |
|---|---:|---|
| sample_defect_id | bigint identity PK |  |
| sample_id | bigint FK |  |
| defect_id | int FK |  |
| defect_value | decimal(18,4) null | Count/score/percentage. |
| defect_percentage | decimal(9,4) null | Calculated using sample size or carton count. |
| severity_code | varchar(20) | Major/Minor/Critical inherited but overridable if allowed. |
| comment | nvarchar(500) null |  |
| is_within_tolerance | bit null | Calculated. |
| created_at | datetime2 |  |
| created_by | nvarchar(80) |  |

**Unique index:** `(sample_id, defect_id)` unless duplicate observations per defect are intentionally allowed.

---

### 6.5 Image model

#### `qms_image_asset`
Stores the physical image metadata once.

| Column | Type suggestion | Notes |
|---|---:|---|
| image_id | bigint identity PK |  |
| storage_provider | varchar(30) | Local, AzureBlob, AWS_S3. |
| original_file_name | nvarchar(255) |  |
| content_type | varchar(100) | image/jpeg, image/png. |
| file_size_bytes | bigint |  |
| storage_url | nvarchar(1000) | Private object key or URL. |
| thumbnail_url | nvarchar(1000) null | Derived thumbnail. |
| checksum_sha256 | char(64) | Duplicate detection/integrity. |
| width_px | int null |  |
| height_px | int null |  |
| uploaded_at | datetime2 |  |
| uploaded_by | nvarchar(80) |  |
| is_deleted | bit default 0 | Soft delete. |

#### `qms_image_link`
Links one image to a business object.

| Column | Type suggestion | Notes |
|---|---:|---|
| image_link_id | bigint identity PK |  |
| image_id | bigint FK |  |
| owner_type | varchar(30) | Arrival, ArrivalChecklist, QualityOrder, QualityOrderMaterial, Sample. |
| owner_id | bigint | ID of owner. |
| image_category | varchar(40) | ArrivalExternal, ArrivalInternal, TemperatureDisplay, DataLogger, QualityInspection, SampleDefect, ReportOnly. |
| display_order | int |  |
| caption | nvarchar(255) null |  |
| include_in_report | bit default 1 |  |
| created_at | datetime2 |  |
| created_by | nvarchar(80) |  |

This replaces image URL text fields and photo flags as the actual image store. Existing checklist flags remain only as yes/no checklist answers.

---

### 6.6 Status, audit, and configuration

#### `qms_status_history`
| Column | Type suggestion | Notes |
|---|---:|---|
| status_history_id | bigint identity PK |  |
| entity_type | varchar(40) | Arrival, QualityOrder, Sample. |
| entity_id | bigint |  |
| old_status | varchar(30) null |  |
| new_status | varchar(30) |  |
| reason | nvarchar(500) null | Mandatory for close/reopen/cancel. |
| changed_at | datetime2 |  |
| changed_by | nvarchar(80) |  |

#### `qms_audit_log`
| Column | Type suggestion | Notes |
|---|---:|---|
| audit_id | bigint identity PK |  |
| entity_type | varchar(40) |  |
| entity_id | bigint |  |
| action_code | varchar(40) | Insert, Update, Delete, Open, Close, Reopen, Override. |
| old_values_json | nvarchar(max) null |  |
| new_values_json | nvarchar(max) null |  |
| changed_at | datetime2 |  |
| changed_by | nvarchar(80) |  |
| source_ip | varchar(45) null |  |

#### `qms_system_config`
| Column | Type suggestion | Notes |
|---|---:|---|
| config_key | varchar(100) PK | Example: thumbnail_size_screen. |
| config_value | nvarchar(500) |  |
| data_type | varchar(20) | Int, Decimal, String, Bool, Json. |
| updated_at | datetime2 |  |
| updated_by | nvarchar(80) |  |

---

## 7. Status Lifecycle and Editability Matrix

### 7.1 Arrival status

| Status | Meaning | Allowed actions |
|---|---|---|
| Draft | SAP data selected, checklist not complete. | Edit checklist, refresh SAP snapshot, upload arrival images, cancel. |
| Completed | Checklist complete and locked for quality order. | Create quality order, view, report arrival checklist. |
| Cancelled | Arrival abandoned. | View only. |

### 7.2 Quality order status

| Status | Meaning | Allowed actions |
|---|---|---|
| Initial | Quality order created but not opened. | Open, view, cancel if no samples. |
| Open | Active inspection. | Add/edit/delete samples, readings, observations, defects, images. |
| Closed | Finished. | View/report only. No edits. |
| Reopened | Closed order reopened by Site Admin. | Limited edit depending on reason. |
| Cancelled | Invalid order. | View only. |

### 7.3 Editability rules

- Arrival must be **Completed** before creating a quality order.
- Quality order must be **Open** before creating samples.
- Sample save is blocked if required fields for the material group are missing.
- Closed quality orders are read-only.
- Reopen requires Site Admin role and mandatory reason.
- Material size override requires reason and audit.
- Sample delete should be soft delete only.
- Image delete should be soft delete, not physical delete, until retention job runs.

---

## 8. API Execution Plan

### 8.1 SAP integration APIs

| API | Method | Purpose |
|---|---|---|
| `/api/sap/containers/search` | GET | Search by container, BOL, PO. |
| `/api/sap/containers/{selectionKey}` | GET | Get selected SAP container/material details. |
| `/api/sap/materials/{materialNo}` | GET | Get material details. |
| `/api/sap/health` | GET | Check OData availability. |

### 8.2 Arrival APIs

| API | Method | Purpose |
|---|---|---|
| `/api/arrivals` | POST | Create arrival from selected SAP OData rows. |
| `/api/arrivals/{id}` | GET | Get arrival, checklist, shipment snapshot, items, images. |
| `/api/arrivals/{id}/checklist` | PUT | Save checklist. |
| `/api/arrivals/{id}/complete` | POST | Complete arrival. |
| `/api/arrivals/{id}/images` | POST | Upload arrival images. |
| `/api/arrivals/{id}/report` | GET | Generate arrival checklist report. |

### 8.3 Quality order APIs

| API | Method | Purpose |
|---|---|---|
| `/api/quality-orders` | POST | Create quality order from completed arrival. |
| `/api/quality-orders/{id}` | GET | Full quality order cockpit. |
| `/api/quality-orders/{id}/open` | POST | Open order. |
| `/api/quality-orders/{id}/close` | POST | Close order. |
| `/api/quality-orders/{id}/reopen` | POST | Site Admin only. |
| `/api/quality-orders/{id}/materials` | GET | Materials in order. |
| `/api/quality-orders/{id}/report` | POST | Generate PDF report. |

### 8.4 Sample APIs

| API | Method | Purpose |
|---|---|---|
| `/api/qomaterials/{qoMaterialId}/samples` | POST | Create sample. |
| `/api/samples/{id}` | PUT | Update sample header. |
| `/api/samples/{id}/readings` | PUT | Replace/save readings. |
| `/api/samples/{id}/observations` | POST | Add observation. |
| `/api/samples/{id}/defects` | PUT | Save defect values. |
| `/api/samples/{id}/images` | POST | Upload sample/inspection images. |
| `/api/samples/{id}` | DELETE | Soft delete sample. |

### 8.5 Admin APIs

| API | Method | Purpose |
|---|---|---|
| `/api/admin/defects` | CRUD | Maintain defect catalog. |
| `/api/admin/material-groups/{group}/defects` | CRUD | Link defects to material group. |
| `/api/admin/readings` | CRUD | Maintain reading types. |
| `/api/admin/config` | GET/PUT | Thumbnail sizes, report settings, material sync schedule. |
| `/api/admin/audit` | GET | Audit query. |

---

## 9. Detailed Execution Plan

### Phase 0 - Alignment and Freeze Decisions

**Duration:** 3-5 working days

**Decisions to finalize:**

1. Confirm that SAP CDS/OData views are read-only and available.
2. Confirm which fields are mandatory for arrival creation.
3. Confirm whether one arrival can have one quality order only.
4. Confirm whether one sample is one carton, multi-carton, pallet, or configurable.
5. Confirm exact defect formulas and acceptance thresholds.
6. Confirm image storage provider.
7. Confirm roles: Standard user, Quality Inspector, Admin, Site Admin.

**Deliverables:**

- Signed field mapping from SAP OData to QMS arrival snapshot.
- Approved SQL Server normalized ERD.
- Approved status lifecycle.
- Approved report mockup version 1.

---

### Phase 1 - Database Foundation

**Duration:** 1-2 weeks

**Tasks:**

1. Create SQL Server schema `qms`.
2. Create identity/sequences for `arrival_no`, `quality_order_no`, and sample numbering.
3. Create normalized tables listed in section 6.
4. Add FK constraints with `ON DELETE NO ACTION` or restrict behavior.
5. Add filtered unique indexes for active records.
6. Add rowversion columns for concurrency.
7. Add audit triggers or application-level audit interceptor.
8. Seed defect catalog for apple defects currently shown in the screen:
   - WASTE_DECAY, SCALD, BITTER_PIT_PLARA, LENTICELS_BREAKDOWN.
   - STEM_INJURY, BRUISING, CRACK, RUSSETING, SHRIVELLING, SUNBURN, WAX_RESIDUE, RED_BLUSH, INSECT_DAMAGE, CHEMICAL_RESIDUE, CALYX_MOLDS, MECHANICAL_INJURY.
9. Seed reading types:
   - BRIX, PUC, PHC, STICKER, FIRMNESS, GROSS_WEIGHT, NET_WEIGHT, TARA, COLOUR, WAXING, DOWNGRADE, PACKAGING_MATERIAL.
10. Seed system configuration:
   - thumbnail_size_screen_height.
   - thumbnail_size_screen_width.
   - thumbnail_size_pdf_height.
   - thumbnail_size_pdf_width.
   - image_fit_mode: Cover/Contain.

**Acceptance criteria:**

- DB deployment script runs repeatedly in dev.
- Foreign keys prevent orphan samples, defects, and images.
- Defects are rows, not columns.
- Sample readings are rows, not columns.
- Quality order cannot exist without arrival.
- Sample cannot exist without quality order material.

---

### Phase 2 - SAP OData Consumer Layer

**Duration:** 1-2 weeks

**Tasks:**

1. Implement typed client for each SAP OData read model.
2. Implement request filtering:
   - container number.
   - BOL number.
   - PO number.
   - material number.
3. Implement container ambiguity handling.
4. Implement retry policy:
   - 3 retries.
   - exponential backoff.
   - no retry on authorization errors.
5. Implement OData error mapper.
6. Implement response hash to detect if SAP payload changed before arrival save.
7. Implement optional memory/Redis cache for search results before save only.
8. Store raw OData payload JSON when creating arrival snapshot.

**Acceptance criteria:**

- Searching by container returns all possible BOL/PO combinations.
- User cannot proceed when container is ambiguous until BOL/PO is selected.
- Arrival save stores both normalized columns and raw payload JSON.
- No direct SQL read from SAP is used.

---

### Phase 3 - Arrival Overview Module

**Duration:** 2 weeks

**Tasks:**

1. Build arrival search screen.
2. Build SAP result grid.
3. Build arrival checklist form sections:
   - Arrival header details.
   - External inspection.
   - Temperature unit reading.
   - Internal inspection.
   - Pulp temperature check.
   - Data logger inspection.
   - Notes/observations.
   - Attachments checklist.
4. Implement save draft.
5. Implement complete arrival.
6. Implement image upload for arrival categories.
7. Implement arrival report preview.
8. Implement validation before completion.

**Key validations:**

- Container, BOL, PO, and at least one material row are required.
- If `external_damage_exists = true`, an external damage note or image is required.
- If data logger is located, serial number should be required.
- If temperature display photo flag is true, at least one matching image link should exist.
- Arrival cannot be completed if mandatory checklist answers are missing.

**Acceptance criteria:**

- Arrival Overview can be created from SAP OData rows.
- Completed Arrival cannot be changed except by authorized correction workflow.
- Arrival images are separated from quality order inspection images.

---

### Phase 4 - Quality Order Module

**Duration:** 2 weeks

**Tasks:**

1. Create quality order from completed arrival.
2. Load shipment snapshot into Shipment Details tab.
3. Load material rows into Container Details and Material Details tabs.
4. Implement Open Quality Order button.
5. Implement Close Quality Order button.
6. Implement Reopen Quality Order for Site Admin only.
7. Implement status banner and editability locking.
8. Implement material size override with reason and audit.

**Acceptance criteria:**

- User cannot create quality order before arrival completion.
- User cannot create samples unless quality order is Open.
- Closed quality order is read-only.
- Reopen requires Site Admin and reason.
- Material override creates audit log.

---

### Phase 5 - Sample, Reading, Observation, and Defect Module

**Duration:** 3 weeks

**Tasks:**

1. Build dynamic sample form based on material group.
2. Create sample header fields:
   - carton count.
   - carton identifier.
   - sample scope.
   - sample size.
   - grower.
   - pallet.
   - pack code.
   - date code.
   - lot.
   - label.
3. Build readings panel from reading type configuration.
4. Build observations panel.
5. Build dynamic defects panel from `qms_material_group_defect`.
6. Implement create, save, edit, soft delete sample.
7. Implement defect percentage calculations.
8. Implement required fields and threshold validations.

**Recommended calculation approach:**

- Store raw defect count/value.
- Store calculated percentage per sample.
- Store calculation version.
- Recalculate on sample save.
- Preserve report output using a report snapshot when PDF is generated.

**Example defect percentage:**

`defect_percentage = defect_value / effective_sample_size * 100`

Where `effective_sample_size` is:

1. sample size if maintained;
2. otherwise carton count multiplied by material size, if both available;
3. otherwise manually entered effective sample size with reason.

**Acceptance criteria:**

- Apple defects are configurable, not hardcoded columns.
- Adding a future fruit family does not require changing sample table structure.
- One material can have many samples.
- One sample can have many readings, observations, defects, and images.

---

### Phase 6 - Image Management

**Duration:** 1-2 weeks

**Tasks:**

1. Implement upload API with content-type and size validation.
2. Store original image in file/blob storage.
3. Generate thumbnail asynchronously.
4. Create image metadata and business link rows.
5. Support image categories:
   - Arrival external.
   - Arrival internal.
   - Temperature display.
   - Data logger.
   - Quality inspection.
   - Sample image.
   - Report only.
6. Implement reorder images.
7. Implement soft delete.
8. Implement admin-controlled thumbnail settings for screen and PDF.

**Acceptance criteria:**

- Arrival images do not leak into unrelated quality orders.
- Inspection images are linked to correct quality order/sample.
- PDF can include both image sections separately.
- Deleted images disappear from UI but remain auditable until retention cleanup.

---

### Phase 7 - PDF Reporting

**Duration:** 2 weeks

**Report sections:**

1. Header:
   - company name/logo.
   - report title.
   - page number.
   - quality order number.
   - report date.
2. Shipment details:
   - BOL, container, vessel, voyage, arrival date, supplier, loading/origin details.
3. Material table:
   - material, description, origin, material group, quantity, UoM.
4. Sample sections:
   - sample header.
   - readings.
   - major defects.
   - minor defects.
   - observations.
   - acceptance result.
5. Image sections:
   - arrival images.
   - quality inspection images.
   - Images are pre-resized to 2x the configured *PDF width x PDF height* and JPEG-compressed at quality 80 at PDF-generation time, then embedded inline as bytes. No URLs, no hyperlinks, no duplicate enlarged-page copy. This keeps the PDF self-contained (safe to email to suppliers who have no QMS access), small (~<=1 MB for a typical 8-photo report), and identical across all PDF readers (Adobe, Chrome, Edge, mobile). Same pipeline is used in both the Arrival Checklist PDF and the Quality Control Report PDF.
6. Audit footer:
   - generated by.
   - generated at.
   - calculation version.

**Tasks:**

1. Build report data query/stored procedure.
2. Build calculation service.
3. Store report generation log.
4. Store generated PDF archive path.
5. Implement report regeneration rules.
6. Implement PDF preview and download.

**Acceptance criteria:**

- Report is reproducible for a closed order.
- Report shows separate image groups.
- Defect rows and readings expand dynamically by material group.
- Report generation does not block user screen for large image sets.
- Generated PDF contains no references to QMS server URLs (verifiable by `strings file.pdf | grep -i http` returning only the footer line), so PDFs forwarded to suppliers remain readable without QMS access.

---

### Phase 8 - Admin and Configuration

**Duration:** 1-2 weeks

**Tasks:**

1. Defect catalog maintenance.
2. Material group to defect mapping.
3. Reading type configuration.
4. Required field configuration by material group.
5. Thumbnail screen/PDF settings.
6. Role and permission matrix.
7. Audit log viewer.
8. OData health/check page.

**Acceptance criteria:**

- Admin can add future fruit defects without schema change.
- Site Admin can reopen closed orders with full audit.
- Thumbnail settings affect UI and PDF output.

---

### Phase 9 - Testing and UAT

**Duration:** 2 weeks

**Test scenarios:**

1. Container appears under one BOL.
2. Container appears under multiple BOLs.
3. SAP OData unavailable before arrival save.
4. SAP OData unavailable after arrival save.
5. Arrival checklist incomplete.
6. Arrival completed, create quality order.
7. Quality order opened, samples created.
8. Multiple samples for one material.
9. Material size missing, override with reason.
10. Closed order cannot be edited.
11. Site Admin reopens order.
12. Image upload and thumbnail generation.
13. PDF report generation with many samples and images.
14. Future fruit material group with different defects.
15. Concurrent users creating samples for the same quality order.

**Acceptance criteria:**

- No orphan data.
- No duplicate sample numbers.
- No wrong BOL/container selection.
- No edit allowed after close unless reopened.
- PDF matches business report requirements.

---

### Phase 10 - Deployment

**Duration:** 1 week

**Tasks:**

1. Prepare environments: Dev, Test, Production.
2. Configure SAP OData destinations and credentials.
3. Configure SQL Server connection and migration user.
4. Configure file/blob storage.
5. Configure background workers.
6. Configure backups and retention.
7. Configure monitoring and logging.
8. Run smoke tests.
9. Train key users.
10. Go-live with hypercare.

**Acceptance criteria:**

- Production configuration is documented.
- Backup and restore tested.
- SAP OData connectivity tested.
- User roles tested.
- Rollback plan approved.

---

## 10. Recommended Timeline

| Week | Workstream |
|---:|---|
| 1 | Freeze scope, SAP field mapping, DB model approval. |
| 2 | SQL Server schema, seed master data, audit framework. |
| 3 | SAP OData consumer and container/BOL search. |
| 4 | Arrival Overview save, complete, snapshot, images. |
| 5 | Arrival checklist validation and arrival report. |
| 6 | Quality order header, material rows, status lifecycle. |
| 7 | Open/close/reopen rules and material size override. |
| 8 | Sample header and dynamic reading model. |
| 9 | Defect catalog, material group mapping, sample defects. |
| 10 | Image management, thumbnails, reorder, soft delete. |
| 11 | PDF report data query and calculation service. |
| 12 | PDF layout, image sections, report archive. |
| 13 | Admin configuration screens. |
| 14 | Security, audit viewer, performance tuning. |
| 15 | SIT/UAT fixes. |
| 16 | Deployment, training, go-live readiness. |

---

## 11. Field Mapping Corrections from Workbook

### Arrival fields

Keep these in arrival checklist or arrival header:

- BOL_NO -> `qms_arrival.bol_no`.
- CONTAINER -> `qms_arrival.container_no`.
- EBELN -> `qms_arrival.ebeln`.
- SHIPMENTNO -> should become generated `arrival_no` or `internal_shipment_no`, not the only primary key.
- COMPANYCODE -> `bukrs`.
- CREATED_ON / CREATED_BY -> use datetime2 and user id.
- Seal, cargo, temperature, logger, and photo flags -> checklist columns.

### Shipment fields

Move to `qms_shipment_snapshot`:

- LOADING_DATE, SAILING_DATE, EXAMINATION_DATE, ARRIVAL_DATE, UNLOADING_DATE, INSPECTION_DATE.
- TRANSIT_DAYS, TIME_BAR, LOADING_PORT, LOADING_COUNTRY, ARRIVAL_PLACE.
- VESSEL_NAME, VOYAGE_NUMBER, PULLOUT_DATA, RECEIVING_DATE.
- TIME_BAR_EXCEED, LOGGER_SERIAL, INSPECTION_POINT, JOINT_SURVEY.

### Header fields

Move to quality order/material rows:

- QCNO -> `quality_order_no`.
- MATERIAL -> `quality_order_material.material_no`.
- MATERIAL_DESC, ORIGIN, CLASS, WEIGHT, SIZE, MAT_GROUP, MAT_GROUP_DESC, MAJOR_CATEGORY -> `qms_quality_order_material` snapshot.
- MENGE / MEINS / WERKS / LGORT / BUKRS -> `qms_arrival_item` and copied into report snapshot as needed.

### Lines fields

Split as follows:

- GROWER, PALLET, SAMPLE_SIZE, CARTON_COUNT, PACK_CODE, DATE_CODE, LOT, LABEL -> `qms_sample`.
- BRIX, PUC, PHC, STICKER, FIRMNESS, GROSS_WEIGHT, NET_WEIGHT, TARA, COLOUR, WAXING, DOWNGRADE, PACKAGING_MAT -> `qms_sample_reading`.
- WASTE_DECAY, SCALD, BITTER_PIT_PLARA, LENTICELS_BREAKDOWN, CALYX_MOLDS, MECHANICAL_INJURY, STEM_INJURY, SHRIVELLING, SUNBURN, CRACK, WAX_RESIDUE, RED_BLUSH, INSECT_DAMAGE, RUSSETING, CHEMICAL_RESIDUE, BRUISING -> `qms_sample_defect` using `qms_defect_catalog`.

---

## 12. Key Risks and Fixes

| Risk | Impact | Fix |
|---|---|---|
| Container reused across BOLs | Wrong inspection context. | Mandatory BOL/PO disambiguation. |
| SAP OData changes after arrival | Inconsistent reports. | Save immutable arrival snapshot. |
| Defects stored as columns | New fruit requires schema change. | Defect catalog + sample_defect rows. |
| Readings stored as columns | Future readings require schema change. | Reading type + sample_reading rows. |
| Closed orders edited | Audit/compliance issue. | Status locks and reopen workflow. |
| Image ownership unclear | Images appear in wrong report. | Image asset + image link with owner type. |
| Missing material size | Wrong calculations. | Override workflow with reason and approval. |
| Concurrent sample creation | Duplicate sample numbers. | DB sequence/transaction and unique index. |
| Long PDF generation | Poor UX. | Background report job and archive. |
| OData downtime | Users blocked before arrival. | Clear error before save; SQL snapshot after save. |

---

## 13. Immediate Next Actions

1. Approve the normalized database model.
2. Convert the current workbook structure into the proposed tables.
3. Confirm OData field names and keys from built CDS views.
4. Define first fruit family configuration: Apple.
5. Finalize defect threshold formulas.
6. Decide sample scope default: one carton or multi-carton.
7. Build database scripts first, then OData consumer, then Arrival Overview.
8. Avoid building the PDF first; reporting should come after sample/defect data is stable.

---

## 14. Final Assessment

The corrected design is strong if treated as a **QMS execution layer after Customer Arrival**, not as a replacement for SAP QM and not as a direct SAP database reader. The highest-value fix is normalization: sample data, readings, observations, and defects must be independent child entities. That design lets the system support apples now and other fruit families later without redesigning the database.

The most important technical rule is this:

> Before Arrival save, read from SAP CDS/OData. After Arrival save, read QMS data and locked SAP snapshot from SQL Server.

This protects auditability, performance, report consistency, and user trust.
