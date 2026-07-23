# ============================================================================
#  SharbatlyQMS on Sharbatly_MIS  -  configuration + catalogue carry-over
# ============================================================================
#  Copies the CONFIGURATION and CATALOGUE data out of the retiring
#  SharbatlyQMS database into Sharbatly_MIS. Transactional history (arrivals,
#  quality orders, samples, images, audit) is deliberately NOT copied.
#
#  Idempotent: every step skips work that is already present, so a re-run after
#  a partial failure is safe. Nothing is ever deleted from the source.
#
#  Usage:  .\carry-over-data.ps1            # copy
#          .\carry-over-data.ps1 -DryRun    # report only, write nothing
# ============================================================================
[CmdletBinding()]
param(
    [string]$SourceCs = "Server=192.168.3.10;Database=SharbatlyQMS;UID=linkserver;PWD=P@ssw0rd;TrustServerCertificate=True",
    [string]$TargetCs = "Server=KSAJEDSVSQL003;Database=Sharbatly_MIS;UID=SAP_User;PWD=SAP_User;TrustServerCertificate=True",
    [switch]$DryRun,
    # Quality floor staff generally never used the SCM portal, so they have no
    # portal.User row and would be locked out of QMS. With this switch they are
    # created in the shared identity store as LDAP users holding ONLY their Qc*
    # role -- they gain no SCM access. Off by default: adding rows to a live
    # identity store shared with another application should be a deliberate act.
    [switch]$CreateMissingUsers
)

# Never recreate these: 'admin' is the legacy local BCrypt seed account, not a
# real person, and the local-password fallback is being dropped.
$script:NeverCreate = @('admin')

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Data

$src = New-Object System.Data.SqlClient.SqlConnection $SourceCs
$tgt = New-Object System.Data.SqlClient.SqlConnection $TargetCs
$src.Open(); $tgt.Open()

function Scalar($conn, $sql) {
    $c = $conn.CreateCommand(); $c.CommandText = $sql; return $c.ExecuteScalar()
}
function Exec($conn, $sql, $params) {
    $c = $conn.CreateCommand(); $c.CommandText = $sql
    if ($params) { foreach ($k in $params.Keys) {
        $p = $c.Parameters.AddWithValue("@$k", $(if ($null -eq $params[$k]) { [DBNull]::Value } else { $params[$k] }))
    } }
    return $c.ExecuteNonQuery()
}
function Table($conn, $sql) {
    $c = $conn.CreateCommand(); $c.CommandText = $sql
    $t = New-Object System.Data.DataTable; $t.Load($c.ExecuteReader())
    # ",$t" stops PowerShell unrolling the DataTable into a DataRow array on
    # return -- without it $t.Rows yields objects whose columns read as blank.
    return ,$t
}

$mode = if ($DryRun) { 'DRY RUN - nothing will be written' } else { 'LIVE' }
Write-Host "=== QMS catalogue carry-over ($mode) ===" -ForegroundColor Cyan
Write-Host ""

# ---------------------------------------------------------------------------
# 1) Catalogues, in FK order. These are configuration, not transactions:
#    without them the app cannot run an inspection.
# ---------------------------------------------------------------------------
$catalogues = @(
    'qms_defect_category',
    'qms_defect_catalog',
    'qms_reading_type',
    'qms_material_group_defect',
    'qms_material_group_reading',
    'qms_sample_header_field',
    'qms_arrival_field',
    'qms_perspective'
)

Write-Host "--- catalogues ---" -ForegroundColor Yellow
foreach ($t in $catalogues) {
    $srcRows = [int](Scalar $src "SELECT COUNT(*) FROM $t")
    $tgtRows = [int](Scalar $tgt "SELECT COUNT(*) FROM qms.$t")

    if ($tgtRows -gt 0) {
        Write-Host ("  {0,-30} target already has {1} row(s) - skipped" -f $t, $tgtRows) -ForegroundColor DarkGray
        continue
    }
    if ($srcRows -eq 0) {
        Write-Host ("  {0,-30} source empty - nothing to copy" -f $t) -ForegroundColor DarkGray
        continue
    }
    if ($DryRun) {
        Write-Host ("  {0,-30} would copy {1} row(s)" -f $t, $srcRows)
        continue
    }

    $data = Table $src "SELECT * FROM $t"
    # KeepIdentity: catalogue ids are referenced by the mapping tables and by
    # saved report perspectives, so they must survive the move unchanged.
    $bulk = New-Object System.Data.SqlClient.SqlBulkCopy($TargetCs, [System.Data.SqlClient.SqlBulkCopyOptions]::KeepIdentity)
    $bulk.DestinationTableName = "qms.$t"
    $bulk.BulkCopyTimeout = 300
    foreach ($col in $data.Columns) { [void]$bulk.ColumnMappings.Add($col.ColumnName, $col.ColumnName) }
    $bulk.WriteToServer($data)
    $bulk.Close()

    $after = [int](Scalar $tgt "SELECT COUNT(*) FROM qms.$t")
    $ok = if ($after -eq $srcRows) { 'OK' } else { "MISMATCH (src=$srcRows)" }
    Write-Host ("  {0,-30} copied {1,5} row(s)  {2}" -f $t, $after, $ok) -ForegroundColor $(if ($ok -eq 'OK') { 'Green' } else { 'Red' })
}

# ---------------------------------------------------------------------------
# 2) Material brand -> qms.MaterialExtra
#    dbo.Mara has no brand column, so the QMS-only brand values are preserved
#    beside it. Only materials that actually carry a brand are copied.
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "--- material brands ---" -ForegroundColor Yellow
$tgtBrands = [int](Scalar $tgt "SELECT COUNT(*) FROM qms.MaterialExtra")
if ($tgtBrands -gt 0) {
    Write-Host "  target already has $tgtBrands row(s) - skipped" -ForegroundColor DarkGray
} else {
    $brands = Table $src "SELECT material_no, brand FROM qms_sap_material_cache WHERE brand IS NOT NULL AND LEN(brand) > 0"
    if ($DryRun) {
        Write-Host "  would copy $($brands.Rows.Count) brand value(s)"
    } else {
        $bulk = New-Object System.Data.SqlClient.SqlBulkCopy($TargetCs)
        $bulk.DestinationTableName = 'qms.MaterialExtra'
        $bulk.BulkCopyTimeout = 300
        [void]$bulk.ColumnMappings.Add('material_no','material_no')
        [void]$bulk.ColumnMappings.Add('brand','brand')
        $bulk.WriteToServer($brands)
        $bulk.Close()
        $n = [int](Scalar $tgt "SELECT COUNT(*) FROM qms.MaterialExtra")
        $matched = [int](Scalar $tgt "SELECT COUNT(*) FROM qms.MaterialExtra x JOIN dbo.Mara m ON m.MATERIAL = x.material_no")
        Write-Host "  copied $n brand value(s); $matched match a current dbo.Mara material" -ForegroundColor Green
    }
}

# ---------------------------------------------------------------------------
# 3) SiteConfiguration -> portal.SystemSetting
#    Keys are prefixed 'qms.' so they stay identifiable next to SCM's own
#    settings. SettingValueJson holds JSON, so plain string values are
#    JSON-encoded here and decoded by the app on read.
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "--- site configuration -> portal.SystemSetting ---" -ForegroundColor Yellow
$cfg = Table $src "SELECT ConfigKey, ConfigValue FROM SiteConfiguration ORDER BY ConfigKey"
$copied = 0; $skipped = 0
foreach ($row in $cfg.Rows) {
    $key = "qms.$($row.ConfigKey)"
    $exists = [int](Scalar $tgt "SELECT COUNT(*) FROM portal.SystemSetting WHERE SettingKey = '$($key.Replace("'","''"))'")
    if ($exists -gt 0) { $skipped++; continue }
    if ($DryRun) { $copied++; continue }

    $val = if ($row.ConfigValue -is [DBNull]) { $null } else { [string]$row.ConfigValue }
    $json = if ($null -eq $val) { 'null' } else { ConvertTo-Json $val -Compress }

    Exec $tgt "INSERT INTO portal.SystemSetting (SettingKey, SettingValueJson, UpdatedAt, UpdatedByUserId)
               VALUES (@k, @v, SYSUTCDATETIME(), NULL)" @{ k = $key; v = $json } | Out-Null
    $copied++
}
Write-Host "  $copied setting(s) $(if($DryRun){'would be '})copied, $skipped already present" -ForegroundColor Green

# ---------------------------------------------------------------------------
# 4) Users -> portal.User role assignments
#    Users are NOT created here. portal.User is the live identity store for the
#    SCM app's 118 users; inventing rows in it is exactly the duplication this
#    migration exists to remove. Instead each QMS user is matched to an
#    existing portal.User and given the equivalent Qc* role. Anything that
#    cannot be matched is reported for a human to decide.
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "--- users -> portal.UserRole / UserPlant / qms.UserProfile ---" -ForegroundColor Yellow
$roleMap = @{ 'Viewer' = 'QcViewer'; 'Operator' = 'QcOperator'; 'Supervisor' = 'QcSupervisor'
              'Manager' = 'QcManager'; 'ClaimManager' = 'QcClaimManager'; 'SiteAdmin' = 'QcAdmin' }

$qmsUsers = Table $src "SELECT UserId, Username, FullName, Email, Department, EmployeeId, Role, IsActive, PlantCode FROM Users"
$matched = 0; $unmatched = @(); $rolesAdded = 0; $plantsAdded = 0; $profilesAdded = 0; $created = 0

foreach ($u in $qmsUsers.Rows) {
    $uname = ([string]$u.Username).Trim()
    $safe  = $uname.Replace("'","''")
    # QMS stores the sAMAccountName (no @domain); match on either column.
    $portalUid = Scalar $tgt "SELECT TOP 1 UserId FROM portal.[User]
                        WHERE Username = '$safe' OR SamAccountName = '$safe'
                        ORDER BY CASE WHEN Username = '$safe' THEN 0 ELSE 1 END"
    if ($null -eq $portalUid -or $portalUid -is [DBNull]) {
        if (-not $CreateMissingUsers -or $script:NeverCreate -contains $uname.ToLower()) {
            $unmatched += "$uname  (role=$($u.Role), name=$($u.FullName))"
            continue
        }
        if ($DryRun) { $created++; continue }

        # Identity only -- no password, no SCM role. They authenticate by AD bind.
        $ins = $tgt.CreateCommand()
        $ins.CommandText = "
            INSERT INTO portal.[User] (IdType, Username, DisplayName, Email, SamAccountName,
                                       MfaEnabled, IsActive, CreatedAt, UpdatedAt)
            OUTPUT INSERTED.UserId
            VALUES ('LDAP', @n, @d, @e, @n, 0, @a, SYSUTCDATETIME(), SYSUTCDATETIME())"
        [void]$ins.Parameters.AddWithValue('@n', $uname)
        [void]$ins.Parameters.AddWithValue('@d', $(if ($u.FullName -is [DBNull]) { $uname } else { [string]$u.FullName }))
        [void]$ins.Parameters.AddWithValue('@e', $(if ($u.Email -is [DBNull]) { [DBNull]::Value } else { [string]$u.Email }))
        [void]$ins.Parameters.AddWithValue('@a', $(if ($u.IsActive -is [DBNull]) { $true } else { [bool]$u.IsActive }))
        $portalUid = $ins.ExecuteScalar()
        $created++
    }
    $matched++
    $portalUid = [int]$portalUid
    $roleCode = $roleMap[[string]$u.Role]
    if (-not $roleCode) { $roleCode = 'QcViewer' }

    if (-not $DryRun) {
        # role
        $has = [int](Scalar $tgt "SELECT COUNT(*) FROM portal.UserRole WHERE UserId=$portalUid AND RoleCode='$roleCode'")
        if ($has -eq 0) {
            Exec $tgt "INSERT INTO portal.UserRole (UserId, RoleCode, SourceKind) VALUES (@u, @r, 'MANUAL')" `
                 @{ u = $portalUid; r = $roleCode } | Out-Null
            $rolesAdded++
        }
        # QMS-only profile fields
        $hasP = [int](Scalar $tgt "SELECT COUNT(*) FROM qms.UserProfile WHERE UserId=$portalUid")
        if ($hasP -eq 0) {
            Exec $tgt "INSERT INTO qms.UserProfile (UserId, EmployeeId, Department, IsOnline)
                       VALUES (@u, @e, @d, 0)" `
                 @{ u = $portalUid
                    e = $(if ($u.EmployeeId -is [DBNull]) { $null } else { [string]$u.EmployeeId })
                    d = $(if ($u.Department -is [DBNull]) { $null } else { [string]$u.Department }) } | Out-Null
            $profilesAdded++
        }
        # plant
        if ($u.PlantCode -isnot [DBNull] -and [string]$u.PlantCode) {
            $plant = ([string]$u.PlantCode).Replace("'","''")
            $hasPl = [int](Scalar $tgt "SELECT COUNT(*) FROM portal.UserPlant WHERE UserId=$portalUid AND Plant='$plant'")
            if ($hasPl -eq 0) {
                Exec $tgt "INSERT INTO portal.UserPlant (UserId, Plant, AssignedAt, AssignedByUserId)
                           VALUES (@u, @p, SYSUTCDATETIME(), NULL)" @{ u = $portalUid; p = [string]$u.PlantCode } | Out-Null
                $plantsAdded++
            }
        }
    }
}

Write-Host "  QMS users            : $($qmsUsers.Rows.Count)"
Write-Host "  matched in portal    : $($matched - $created)" -ForegroundColor Green
Write-Host "  created in portal    : $created" -ForegroundColor $(if ($created) { 'Yellow' } else { 'DarkGray' })
Write-Host "  Qc roles granted     : $rolesAdded"
Write-Host "  profiles created     : $profilesAdded"
Write-Host "  plant links created  : $plantsAdded"
if ($unmatched.Count) {
    Write-Host "  NOT FOUND in portal.User ($($unmatched.Count)) - these people cannot log in to QMS until" -ForegroundColor Red
    Write-Host "  they are created in the portal (or they sign in once via AD if auto-create is on):" -ForegroundColor Red
    $unmatched | ForEach-Object { Write-Host "     $_" -ForegroundColor Red }
}

$src.Close(); $tgt.Close()
Write-Host ""
Write-Host "=== done ($mode) ===" -ForegroundColor Cyan
