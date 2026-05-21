-- ============================================================================
--  SharbatlyQMS  -  V05  -  Arrival checklist photo flags
-- ============================================================================
-- The arrival-checklist UI carries a "photo taken" flag against each photo
-- category the inspector is expected to capture. These are pure booleans (no
-- image URL); the actual image storage lives in qms_image_asset /
-- qms_image_link, but the checklist record itself remembers what the
-- inspector confirmed they captured.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('dbo.qms_arrival_checklist') AND name='display_temp_photo_taken')
    ALTER TABLE qms_arrival_checklist ADD display_temp_photo_taken BIT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('dbo.qms_arrival_checklist') AND name='internal_inspection_photo_taken')
    ALTER TABLE qms_arrival_checklist ADD internal_inspection_photo_taken BIT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('dbo.qms_arrival_checklist') AND name='pulp_temp_photo_taken')
    ALTER TABLE qms_arrival_checklist ADD pulp_temp_photo_taken BIT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('dbo.qms_arrival_checklist') AND name='container_seal_photo_taken')
    ALTER TABLE qms_arrival_checklist ADD container_seal_photo_taken BIT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('dbo.qms_arrival_checklist') AND name='external_container_photo_taken')
    ALTER TABLE qms_arrival_checklist ADD external_container_photo_taken BIT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('dbo.qms_arrival_checklist') AND name='external_damage_photo_taken')
    ALTER TABLE qms_arrival_checklist ADD external_damage_photo_taken BIT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('dbo.qms_arrival_checklist') AND name='first_view_cargo_photo_taken')
    ALTER TABLE qms_arrival_checklist ADD first_view_cargo_photo_taken BIT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('dbo.qms_arrival_checklist') AND name='internal_damage_photo_taken')
    ALTER TABLE qms_arrival_checklist ADD internal_damage_photo_taken BIT NULL;
