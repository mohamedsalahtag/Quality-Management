-- ===================================================================
-- V29  Plant scoping for operator users
--
-- Adds:
--   * Users.plant_code          -- single-plant assignment (Operator-only)
--   * qms_arrival.plant         -- denormalized from the first cache row
--                                   at create time (qms_arrival_item.plant
--                                   already carries this per line)
--   * Filtered IX on qms_arrival(plant, created_at DESC) for the
--     plant-scoped list query.
--
-- Idempotent.
-- ===================================================================

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE  object_id = OBJECT_ID(N'dbo.Users')
      AND  name      = N'PlantCode')
BEGIN
    ALTER TABLE dbo.Users
        ADD PlantCode VARCHAR(4) NULL;
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE  object_id = OBJECT_ID(N'dbo.qms_arrival')
      AND  name      = N'plant')
BEGIN
    ALTER TABLE dbo.qms_arrival
        ADD plant VARCHAR(4) NULL;
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE  name = 'IX_qms_arrival_plant_active'
      AND  object_id = OBJECT_ID(N'dbo.qms_arrival'))
BEGIN
    -- Operator queue: WHERE plant = @plant AND status_code <> 'Cancelled'
    -- ORDER BY created_at DESC. Filtered to skip cancelled rows so the
    -- index stays small.
    CREATE INDEX IX_qms_arrival_plant_active
        ON dbo.qms_arrival (plant, created_at DESC)
        WHERE status_code <> 'Cancelled';
END;
