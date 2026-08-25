param(
    [string]$TargetRoot = ''
)

$ErrorActionPreference = 'Stop'
$payloadRoot = Join-Path $PSScriptRoot 'payload'
$expectedOldVersion = '0.4.3'
$expectedNewVersion = '0.4.4'

if (-not (Test-Path -LiteralPath $payloadRoot -PathType Container)) { throw "Update payload is missing: $payloadRoot" }

if (-not $TargetRoot) {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = 'Select the existing uxp-validation-tv11-whole-product-0.4.3 folder.'
    $dialog.ShowNewFolderButton = $false
    if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { throw 'Update canceled.' }
    $TargetRoot = $dialog.SelectedPath
}

$resolvedTarget = (Resolve-Path -LiteralPath $TargetRoot).Path
$targetParent = Split-Path -Parent $resolvedTarget
$targetLeaf = Split-Path -Leaf $resolvedTarget
if ($targetLeaf -ne 'uxp-validation-tv11-whole-product-0.4.3') {
    throw 'Select the inner folder named uxp-validation-tv11-whole-product-0.4.3.'
}

$newTarget = Join-Path $targetParent 'uxp-validation-tv11-whole-product-0.4.4'
if (Test-Path -LiteralPath $newTarget) { throw "The 0.4.4 target folder already exists: $newTarget" }

$targetPlugin = Join-Path $resolvedTarget 'uxp-validation-plugin'
$targetFixture = Join-Path $resolvedTarget 'fixture'
$targetManifest = Join-Path $targetPlugin 'manifest.json'
$targetPlan = Join-Path $targetFixture 'whole-product-a6015-render-plan.json'
$targetVerifier = Join-Path $targetFixture 'verify_fixture.ps1'
$targetReadme = Join-Path $resolvedTarget 'README-TV11.md'
$requiredTargets = @($targetPlugin, $targetFixture, $targetManifest, $targetPlan, $targetVerifier, $targetReadme)
foreach ($path in $requiredTargets) {
    if (-not (Test-Path -LiteralPath $path)) { throw "The selected 0.4.3 package is incomplete: $path" }
}

$oldManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $targetManifest | ConvertFrom-Json
if ($oldManifest.version -ne $expectedOldVersion) { throw "Expected plugin 0.4.3, found $($oldManifest.version)." }

$payloadPlugin = Join-Path $payloadRoot 'uxp-validation-plugin'
$payloadPlan = Join-Path $payloadRoot 'fixture/whole-product-a6015-render-plan.json'
$payloadVerifier = Join-Path $payloadRoot 'fixture/verify_fixture.ps1'
$payloadReadme = Join-Path $payloadRoot 'README-TV11.md'
foreach ($path in @($payloadPlugin, $payloadPlan, $payloadVerifier, $payloadReadme)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Update payload is incomplete: $path" }
}

$payloadManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $payloadPlugin 'manifest.json') | ConvertFrom-Json
$payloadPlanJson = Get-Content -Raw -Encoding UTF8 -LiteralPath $payloadPlan | ConvertFrom-Json
if ($payloadManifest.version -ne $expectedNewVersion) { throw 'Payload manifest version is incorrect.' }
$payloadG01 = $payloadPlanJson.pages[0].frames[0]
if ($payloadG01.target.audit_name -ne 'front_main' -or $payloadG01.target.layer_path -ne '3' -or $payloadG01.target.expected_clipping -ne $false) {
    throw 'Payload does not contain the approved G01 binding.'
}

& $payloadVerifier -FixtureRoot $targetFixture | Write-Output

$backupRoot = Join-Path $resolvedTarget '_update-backup-0.4.3'
if (Test-Path -LiteralPath $backupRoot) { throw "Update backup already exists: $backupRoot" }
New-Item -ItemType Directory -Path $backupRoot | Out-Null
Copy-Item -LiteralPath $targetPlugin -Destination $backupRoot -Recurse
New-Item -ItemType Directory -Path (Join-Path $backupRoot 'fixture') | Out-Null
Copy-Item -LiteralPath $targetPlan -Destination (Join-Path $backupRoot 'fixture/whole-product-a6015-render-plan.json')
Copy-Item -LiteralPath $targetVerifier -Destination (Join-Path $backupRoot 'fixture/verify_fixture.ps1')
Copy-Item -LiteralPath $targetReadme -Destination (Join-Path $backupRoot 'README-TV11.md')

try {
    Copy-Item -Path (Join-Path $payloadPlugin '*') -Destination $targetPlugin -Recurse -Force
    Copy-Item -LiteralPath $payloadPlan -Destination $targetPlan -Force
    Copy-Item -LiteralPath $payloadVerifier -Destination $targetVerifier -Force
    Copy-Item -LiteralPath $payloadReadme -Destination $targetReadme -Force

    $updatedManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $targetManifest | ConvertFrom-Json
    $updatedPlan = Get-Content -Raw -Encoding UTF8 -LiteralPath $targetPlan | ConvertFrom-Json
    if ($updatedManifest.version -ne $expectedNewVersion) { throw 'Updated manifest version check failed.' }
    if ($updatedPlan.pages[0].frames[0].target.expected_clipping -ne $false) { throw 'Updated G01 binding check failed.' }
    & $targetVerifier -FixtureRoot $targetFixture | Write-Output
} catch {
    Copy-Item -Path (Join-Path $backupRoot 'uxp-validation-plugin/*') -Destination $targetPlugin -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $backupRoot 'fixture/whole-product-a6015-render-plan.json') -Destination $targetPlan -Force
    Copy-Item -LiteralPath (Join-Path $backupRoot 'fixture/verify_fixture.ps1') -Destination $targetVerifier -Force
    Copy-Item -LiteralPath (Join-Path $backupRoot 'README-TV11.md') -Destination $targetReadme -Force
    throw
}

Move-Item -LiteralPath $resolvedTarget -Destination $newTarget
Write-Output "UPDATE_PASSED=true"
Write-Output "UPDATED_ROOT=$newTarget"
Write-Output 'Close this window, then load the 0.4.4 manifest in UXP Developer Tool.'
