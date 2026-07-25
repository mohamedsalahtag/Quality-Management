-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M08  -  Discharge date
-- ============================================================================
--  Adds the inspector-entered discharge date to the shipment snapshot. It sits
--  next to the (SAP-sourced, read-only) arrival date on the arrival page and
--  becomes the default basis for the Time Bar calculation on the QO report --
--  see the Report.TimeBarBasis setting, which can switch the basis back to the
--  arrival date.
--
--  Idempotent: safe to re-run.
-- ============================================================================

IF COL_LENGTH('qms.qms_shipment_snapshot', 'discharge_date') IS NULL
    ALTER TABLE qms.qms_shipment_snapshot ADD discharge_date date NULL;
GO
