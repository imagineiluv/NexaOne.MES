# 운영 MSSQL 메뉴 시드 SQL 생성기 — config/Seed/nexaone-menu.json(임베디드 dev 시드의 단일 출처)을
# ops/sql/sys-menu-seed.mssql.sql 로 변환한다. 메뉴 JSON을 고치면 본 스크립트를 재실행해 산출물을 갱신·커밋한다.
# 배경: SeedDevMenuIfEmpty는 Development+SQLite 이중 게이트라 운영 MSSQL은 메뉴 공급 경로가 없다(빈 사이드바).
# 정책: SYS_MENU가 비었을 때만 일괄 삽입(dev 시드와 동일 의미론) — 운영 커스터마이징 덮어쓰기 방지.
#       NX_DEV 데모 폴더는 dev 런타임 부가분이라 미포함(운영 비노출). legacyId는 PROGRAM_ID로 보존(V081 규약).
param(
    [string]$MenuJson = (Join-Path $PSScriptRoot '..\..\src\00.Main\NexaOne.Server\config\Seed\nexaone-menu.json'),
    [string]$OutFile  = (Join-Path $PSScriptRoot '..\..\ops\sql\sys-menu-seed.mssql.sql')
)

$ErrorActionPreference = 'Stop'
$rows = Get-Content -Raw -Encoding UTF8 $MenuJson | ConvertFrom-Json
if ($rows.Count -lt 300) { throw "menu json rows unexpectedly low: $($rows.Count)" }

function Esc([string]$s) { if ($null -eq $s) { return $null } return $s.Replace("'", "''") }

$values = foreach ($r in $rows) {
    $parent = if ([string]::IsNullOrEmpty($r.parentMenuId)) { 'NULL' } else { "N'$(Esc $r.parentMenuId)'" }
    $legacy = if ([string]::IsNullOrEmpty($r.legacyId)) { "N''" } else { "N'$(Esc $r.legacyId)'" }
    $uiId   = if ([string]::IsNullOrEmpty($r.uiId)) { "N''" } else { "N'$(Esc $r.uiId)'" }
    "    (N'$(Esc $r.menuId)', N'$(Esc $r.menuName)', $parent, $($r.displaySequence), N'$($r.menuType)', $uiId, $legacy, N'Valid')"
}

$header = @"
-- ============================================================================
-- 운영 MSSQL SYS_MENU 시드(SmartUX 트리 $($rows.Count)행) — tools/ops/Generate-MenuSeedSql.ps1 생성물.
-- 수동 편집 금지: 원본은 config/Seed/nexaone-menu.json — 변경 시 생성기를 재실행해 갱신한다.
-- 적용 시점: 마이그레이션(V001..V081+) 적용 이후. V071(i18n)·V081(ID 리매핑)은 본 시드와 정합.
-- 멱등: SYS_MENU가 비었을 때만 삽입(dev SeedDevMenuIfEmpty와 동일 의미론) — 운영 수정 보존.
-- ============================================================================
IF EXISTS (SELECT 1 FROM SYS_MENU)
BEGIN
    PRINT 'SYS_MENU already populated - seed skipped.';
END
ELSE
BEGIN
    INSERT INTO SYS_MENU (MENU_ID, MENU_NAME, PARENT_MENU_ID, DISPLAY_SEQUENCE, MENU_TYPE, UI_ID, PROGRAM_ID, VALID_STATE)
    VALUES
"@

$body = ($values -join ",`r`n") + ";`r`n    PRINT 'SYS_MENU seeded ($($rows.Count) rows).';`r`nEND`r`n"

$outDir = Split-Path -Parent $OutFile
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force $outDir | Out-Null }
[System.IO.File]::WriteAllText($OutFile, $header + "`r`n" + $body, (New-Object System.Text.UTF8Encoding($true)))
Write-Host "generated: $OutFile ($($rows.Count) menu rows)"
