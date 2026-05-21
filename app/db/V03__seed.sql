-- ============================================================================
--  SharbatlyQMS  -  V03  -  Seed data
-- ============================================================================
--  * SiteConfiguration: SMTP keys + QMS thumbnail/image settings.
--  * Apple defects (plan §1.1, phase 1.8) and shared reading types (phase 1.9).
--  * Material-group bindings for Apple (group code 'FRSH-APP' is a placeholder
--    -- replace with the real SAP material group when the CDS view is wired).
-- ============================================================================

-- ---------------------------------------------------------------------------
-- SiteConfiguration: SMTP + QMS settings
-- ---------------------------------------------------------------------------
INSERT INTO SiteConfiguration (ConfigKey, ConfigValue) VALUES
    ('SmtpHost',                 'smtp.sendgrid.net'),
    ('SmtpPort',                 '587'),
    ('SmtpUser',                 'apikey'),
    ('SmtpPassword',             ''),
    ('SmtpFromEmail',            'noreply@sharbatlyfruit.com'),
    ('SmtpFromName',             'Sharbatly QMS'),
    ('SmtpEnableSsl',            'true'),
    ('SiteName',                 'Sharbatly Quality Management'),
    ('SiteUrl',                  'http://localhost:5000'),
    ('thumbnail_size_screen_w',  '160'),
    ('thumbnail_size_screen_h',  '120'),
    ('thumbnail_size_pdf_w',     '120'),
    ('thumbnail_size_pdf_h',     '90'),
    ('image_fit_mode',           'Cover'),
    ('alert_stale_arrival_days', '3'),
    ('alert_open_qo_days',       '7'),
    ('alert_defect_pct_red',     '10'),
    ('alert_defect_pct_yellow',  '5');

-- ---------------------------------------------------------------------------
-- Apple defect catalog (plan §1.1 phase 1.8)
-- ---------------------------------------------------------------------------
INSERT INTO qms_defect_catalog (defect_code, defect_name, defect_category, default_unit, sort_order) VALUES
    ('WASTE_DECAY',         'Waste / Decay',          'Critical', '%',     10),
    ('SCALD',               'Scald',                  'Major',    '%',     20),
    ('BITTER_PIT_PLARA',    'Bitter Pit / Plara',     'Major',    '%',     30),
    ('LENTICELS_BREAKDOWN', 'Lenticels Breakdown',    'Major',    '%',     40),
    ('STEM_INJURY',         'Stem Injury',            'Minor',    '%',     50),
    ('BRUISING',            'Bruising',               'Major',    '%',     60),
    ('CRACK',               'Crack',                  'Major',    '%',     70),
    ('RUSSETING',           'Russeting',              'Minor',    '%',     80),
    ('SHRIVELLING',         'Shrivelling',            'Minor',    '%',     90),
    ('SUNBURN',             'Sunburn',                'Minor',    '%',    100),
    ('WAX_RESIDUE',         'Wax Residue',            'Minor',    '%',    110),
    ('RED_BLUSH',           'Red Blush',              'Minor',    '%',    120),
    ('INSECT_DAMAGE',       'Insect Damage',          'Major',    '%',    130),
    ('CHEMICAL_RESIDUE',    'Chemical Residue',       'Critical', '%',    140),
    ('CALYX_MOLDS',         'Calyx Molds',            'Major',    '%',    150),
    ('MECHANICAL_INJURY',   'Mechanical Injury',      'Minor',    '%',    160);

-- ---------------------------------------------------------------------------
-- Reading types (plan §1.1 phase 1.9)
-- ---------------------------------------------------------------------------
INSERT INTO qms_reading_type (reading_type_code, reading_name, value_kind, default_unit, sort_order) VALUES
    ('BRIX',               'Brix',                  'Numeric', 'Bx',    10),
    ('PUC',                'PUC',                   'Numeric', '%',     20),
    ('PHC',                'pH',                    'Numeric', 'pH',    30),
    ('STICKER',            'Sticker',               'Text',    NULL,    40),
    ('FIRMNESS',           'Firmness',              'Numeric', 'lbs',   50),
    ('GROSS_WEIGHT',       'Gross Weight',          'Numeric', 'kg',    60),
    ('NET_WEIGHT',         'Net Weight',            'Numeric', 'kg',    70),
    ('TARA',               'Tara',                  'Numeric', 'kg',    80),
    ('COLOUR',             'Colour',                'Text',    NULL,    90),
    ('WAXING',             'Waxing',                'Text',    NULL,   100),
    ('DOWNGRADE',          'Downgrade',             'Numeric', '%',    110),
    ('PACKAGING_MATERIAL', 'Packaging Material',    'Text',    NULL,   120);

-- ---------------------------------------------------------------------------
-- Apple bindings: which defects/readings show up for Apple material group
-- (placeholder material group 'FRSH-APP' -- swap when SAP CDS is wired)
-- ---------------------------------------------------------------------------
INSERT INTO qms_material_group_defect (material_group, major_category, defect_id, is_required, display_section, sort_order)
SELECT 'FRSH-APP', 'Apples', dc.defect_id,
       CASE WHEN dc.defect_category = 'Critical' THEN 1 ELSE 0 END,
       CASE WHEN dc.defect_category IN ('Major','Critical') THEN 'Major' ELSE 'Minor' END,
       dc.sort_order
FROM   qms_defect_catalog dc;

INSERT INTO qms_material_group_reading (material_group, major_category, reading_type_id, is_required, sort_order)
SELECT 'FRSH-APP', 'Apples', rt.reading_type_id,
       CASE WHEN rt.reading_type_code IN ('BRIX','FIRMNESS','NET_WEIGHT') THEN 1 ELSE 0 END,
       rt.sort_order
FROM   qms_reading_type rt;

-- Default email group to anchor alert rules to
INSERT INTO EmailGroups (GroupName, ShortCode, Description)
VALUES ('QC Managers', 'QCM', 'Default recipient group for QMS alerts');
