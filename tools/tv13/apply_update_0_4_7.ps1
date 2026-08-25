param(
    [string]$TargetRoot = ''
)

$ErrorActionPreference = 'Stop'
$payloadRoot = Join-Path $PSScriptRoot 'payload'
$expectedOldVersion = '0.4.6'
$expectedNewVersion = '0.4.7'

if (-not (Test-Path -LiteralPath $payloadRoot -PathType Container)) { throw "Update payload is missing: $payloadRoot" }
if (-not $TargetRoot) {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = 'Select the existing uxp-validation-tv11-whole-product-0.4.6 folder.'
    $dialog.ShowNewFolderButton = $false
    if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { throw 'Update canceled.' }
    $TargetRoot = $dialog.SelectedPath
}

$resolvedTarget = (Resolve-Path -LiteralPath $TargetRoot).Path
$targetParent = Split-Path -Parent $resolvedTarget
$targetLeaf = Split-Path -Leaf $resolvedTarget
if ($targetLeaf -ne 'uxp-validation-tv11-whole-product-0.4.6') { throw 'Select the inner folder named uxp-validation-tv11-whole-product-0.4.6.' }
$newTarget = Join-Path $targetParent 'uxp-validation-tv11-whole-product-0.4.7'
if (Test-Path -LiteralPath $newTarget) { throw "The 0.4.7 target folder already exists: $newTarget" }

$targetPlugin = Join-Path $resolvedTarget 'uxp-validation-plugin'
$targetFixture = Join-Path $resolvedTarget 'fixture'
$targetManifest = Join-Path $targetPlugin 'manifest.json'
$targetBaseVerifier = Join-Path $targetFixture 'verify_fixture.ps1'
$targetNormalPlan = Join-Path $targetFixture 'whole-product-a6015-render-plan.json'
foreach ($path in @($targetPlugin, $targetFixture, $targetManifest, $targetBaseVerifier, $targetNormalPlan, (Join-Path $targetFixture 'template'), (Join-Path $targetFixture 'source'))) {
    if (-not (Test-Path -LiteralPath $path)) { throw "The selected 0.4.6 package is incomplete: $path" }
}
$oldManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $targetManifest | ConvertFrom-Json
if ($oldManifest.version -ne $expectedOldVersion) { throw "Expected plugin 0.4.6, found $($oldManifest.version)." }
& $targetBaseVerifier -FixtureRoot $targetFixture | Out-Null

$payloadPlugin = Join-Path $payloadRoot 'uxp-validation-plugin'
$payloadFixture = Join-Path $payloadRoot 'fixture'
$payloadFailurePlan = Join-Path $payloadFixture 'tv13-controlled-failure-render-plan.json'
$payloadVerifier = Join-Path $payloadFixture 'verify_tv13.ps1'
$payloadCheckBat = Join-Path $payloadFixture 'CHECK-TV13-INPUTS.bat'
$payloadVerifyBat = Join-Path $payloadFixture 'VERIFY-TV13.bat'
$payloadReadme = Join-Path $payloadRoot 'README-TV13.md'
foreach ($path in @($payloadPlugin, $payloadFailurePlan, $payloadVerifier, $payloadCheckBat, $payloadVerifyBat, $payloadReadme)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Update payload is incomplete: $path" }
}
$payloadManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $payloadPlugin 'manifest.json') | ConvertFrom-Json
$failurePlan = Get-Content -Raw -Encoding UTF8 -LiteralPath $payloadFailurePlan | ConvertFrom-Json
if ($payloadManifest.version -ne $expectedNewVersion) { throw 'Payload manifest version is incorrect.' }
if ($failurePlan.validation.controlled_failure.error_code -ne 'TV13_CONTROLLED_FAILURE' -or $failurePlan.validation.controlled_failure.completed_page_count -ne 2) { throw 'Payload TV-13 checkpoint is incorrect.' }

$backupRoot = Join-Path $resolvedTarget '_update-backup-0.4.6'
if (Test-Path -LiteralPath $backupRoot) { throw "Update backup already exists: $backupRoot" }
New-Item -ItemType Directory -Path $backupRoot | Out-Null
Copy-Item -LiteralPath $targetPlugin -Destination $backupRoot -Recurse

try {
    Copy-Item -Path (Join-Path $payloadPlugin '*') -Destination $targetPlugin -Recurse -Force
    Copy-Item -LiteralPath $payloadFailurePlan -Destination (Join-Path $targetFixture 'tv13-controlled-failure-render-plan.json')
    Copy-Item -LiteralPath $payloadVerifier -Destination (Join-Path $targetFixture 'verify_tv13.ps1')
    Copy-Item -LiteralPath $payloadCheckBat -Destination (Join-Path $targetFixture 'CHECK-TV13-INPUTS.bat')
    Copy-Item -LiteralPath $payloadVerifyBat -Destination (Join-Path $targetFixture 'VERIFY-TV13.bat')
    Copy-Item -LiteralPath $payloadReadme -Destination (Join-Path $resolvedTarget 'README-TV13.md')
    New-Item -ItemType Directory -Path (Join-Path $targetFixture 'tv13-failure-output') | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $targetFixture 'tv13-recovery-output') | Out-Null

    $updatedManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $targetManifest | ConvertFrom-Json
    if ($updatedManifest.version -ne $expectedNewVersion) { throw 'Updated manifest version check failed.' }
    & (Join-Path $targetFixture 'verify_tv13.ps1') -FixtureRoot $targetFixture | Out-Null
} catch {
    Copy-Item -Path (Join-Path $backupRoot 'uxp-validation-plugin/*') -Destination $targetPlugin -Recurse -Force
    throw
}

Move-Item -LiteralPath $resolvedTarget -Destination $newTarget
Write-Output 'UPDATE_PASSED=true'
Write-Output "UPDATED_ROOT=$newTarget"
Write-Output 'Close this window, then load the 0.4.7 manifest in UXP Developer Tool.'
