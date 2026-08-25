param(
    [string]$TargetRoot = ''
)

$ErrorActionPreference = 'Stop'
$payloadRoot = Join-Path $PSScriptRoot 'payload'
$pluginVersion = '0.4.7'
$packageVersion = '0.4.7.1'
$allowedSourceVersions = @('0.4.6', '0.4.7')

function Get-RelativeFiles {
    param([string]$Root)

    $resolvedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    return @(Get-ChildItem -LiteralPath $Root -Recurse -File | ForEach-Object {
        $resolvedFile = [System.IO.Path]::GetFullPath($_.FullName)
        $resolvedFile.Substring($resolvedRoot.Length).Replace('\', '/')
    } | Sort-Object)
}

function Get-ProtectedSnapshot {
    param([string]$FixtureRoot)

    $entries = @()
    foreach ($directoryName in @('template', 'source', 'reference')) {
        $directoryPath = Join-Path $FixtureRoot $directoryName
        if (-not (Test-Path -LiteralPath $directoryPath -PathType Container)) {
            throw "Protected fixture directory is missing: $directoryPath"
        }
        $resolvedDirectory = [System.IO.Path]::GetFullPath($directoryPath).TrimEnd('\') + '\'
        foreach ($file in @(Get-ChildItem -LiteralPath $directoryPath -Recurse -File | Sort-Object FullName)) {
            $resolvedFile = [System.IO.Path]::GetFullPath($file.FullName)
            $entries += [ordered]@{
                path = ($directoryName + '/' + $resolvedFile.Substring($resolvedDirectory.Length).Replace('\', '/'))
                length = $file.Length
                sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash
            }
        }
    }
    return ($entries | ConvertTo-Json -Depth 5 -Compress)
}

function Assert-EmptyOutputDirectory {
    param([string]$Path)

    if (Test-Path -LiteralPath $Path) {
        $entries = @(Get-ChildItem -LiteralPath $Path -Force)
        if ($entries.Count -ne 0) {
            throw "TV-13 output directory is not empty; no files were removed: $Path"
        }
    }
}

function Assert-FileMatchesPayload {
    param(
        [string]$PayloadPath,
        [string]$TargetPath
    )

    if (-not (Test-Path -LiteralPath $TargetPath -PathType Leaf)) {
        throw "Updated file is missing: $TargetPath"
    }
    $payloadHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $PayloadPath).Hash
    $targetHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $TargetPath).Hash
    if ($payloadHash -cne $targetHash) {
        throw "Updated file hash mismatch: $TargetPath"
    }
}

if (-not (Test-Path -LiteralPath $payloadRoot -PathType Container)) {
    throw "Update payload is missing: $payloadRoot"
}
if (-not $TargetRoot) {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = 'Select the existing package folder that directly contains the complete fixture folder.'
    $dialog.ShowNewFolderButton = $false
    if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { throw 'Update canceled.' }
    $TargetRoot = $dialog.SelectedPath
}

$resolvedTarget = (Resolve-Path -LiteralPath $TargetRoot).Path
$targetFixture = Join-Path $resolvedTarget 'fixture'
$targetPlugin = Join-Path $resolvedTarget 'uxp-validation-plugin'
$targetManifest = Join-Path $targetPlugin 'manifest.json'
$targetBaseVerifier = Join-Path $targetFixture 'verify_fixture.ps1'
$targetNormalPlan = Join-Path $targetFixture 'whole-product-a6015-render-plan.json'
foreach ($path in @($targetFixture, $targetBaseVerifier, $targetNormalPlan, (Join-Path $targetFixture 'template'), (Join-Path $targetFixture 'source'), (Join-Path $targetFixture 'reference'))) {
    if (-not (Test-Path -LiteralPath $path)) { throw "The selected package does not contain a complete fixture: $path" }
}

$payloadPlugin = Join-Path $payloadRoot 'uxp-validation-plugin'
$payloadFixture = Join-Path $payloadRoot 'fixture'
$payloadReadme = Join-Path $payloadRoot 'README-TV13.md'
$pluginFiles = @(
    'index.html',
    'jpeg-icc.js',
    'main.js',
    'manifest.json',
    'README.md',
    'THIRD_PARTY_NOTICES.md',
    'render-plans/multi-frame-g05-render-plan.json',
    'render-plans/sample-render-plan.json',
    'render-plans/single-frame-g03-render-plan.json',
    'render-plans/tv13-controlled-failure-render-plan.json',
    'render-plans/whole-product-a6015-render-plan.json'
)
$fixtureFiles = @(
    'tv13-controlled-failure-render-plan.json',
    'verify_tv13.ps1',
    'CHECK-TV13-INPUTS.bat',
    'VERIFY-TV13.bat'
)

foreach ($path in @($payloadPlugin, $payloadFixture, $payloadReadme)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Update payload is incomplete: $path" }
}
$actualPluginFiles = @(Get-RelativeFiles -Root $payloadPlugin)
if (($actualPluginFiles -join '|') -cne (($pluginFiles | Sort-Object) -join '|')) {
    throw 'Update payload plugin file allowlist is inconsistent.'
}
foreach ($relativePath in $fixtureFiles) {
    $payloadPath = Join-Path $payloadFixture $relativePath
    if (-not (Test-Path -LiteralPath $payloadPath -PathType Leaf)) { throw "Update payload is incomplete: $payloadPath" }
}

$payloadManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $payloadPlugin 'manifest.json') | ConvertFrom-Json
$failurePlan = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $payloadFixture 'tv13-controlled-failure-render-plan.json') | ConvertFrom-Json
if ($payloadManifest.version -ne $pluginVersion) { throw 'Payload manifest version is incorrect.' }
if ($failurePlan.validation.controlled_failure.error_code -ne 'TV13_CONTROLLED_FAILURE' -or $failurePlan.validation.controlled_failure.completed_page_count -ne 2) {
    throw 'Payload TV-13 checkpoint is incorrect.'
}

if (Test-Path -LiteralPath $targetManifest -PathType Leaf) {
    $sourceManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $targetManifest | ConvertFrom-Json
    if ($sourceManifest.version -notin $allowedSourceVersions) {
        throw "Expected plugin 0.4.6 or 0.4.7, found $($sourceManifest.version)."
    }
}

$backupRoot = Join-Path $resolvedTarget '_update-backup-0.4.6'
$backupPlugin = Join-Path $backupRoot 'uxp-validation-plugin'
$backupManifest = Join-Path $backupPlugin 'manifest.json'
if (Test-Path -LiteralPath $backupRoot) {
    if (-not (Test-Path -LiteralPath $backupManifest -PathType Leaf)) { throw "Existing update backup is incomplete: $backupRoot" }
    $backupVersion = (Get-Content -Raw -Encoding UTF8 -LiteralPath $backupManifest | ConvertFrom-Json).version
    if ($backupVersion -ne '0.4.6') { throw "Existing update backup has unexpected plugin version: $backupVersion" }
} elseif (Test-Path -LiteralPath $targetManifest -PathType Leaf) {
    $sourceVersion = (Get-Content -Raw -Encoding UTF8 -LiteralPath $targetManifest | ConvertFrom-Json).version
    if ($sourceVersion -eq '0.4.6') {
        New-Item -ItemType Directory -Path $backupRoot | Out-Null
        Copy-Item -LiteralPath $targetPlugin -Destination $backupRoot -Recurse
    }
}

$failureOutput = Join-Path $targetFixture 'tv13-failure-output'
$recoveryOutput = Join-Path $targetFixture 'tv13-recovery-output'
Assert-EmptyOutputDirectory -Path $failureOutput
Assert-EmptyOutputDirectory -Path $recoveryOutput

& $targetBaseVerifier -FixtureRoot $targetFixture | Out-Null
$protectedBefore = Get-ProtectedSnapshot -FixtureRoot $targetFixture

New-Item -ItemType Directory -Path $targetPlugin -Force | Out-Null
foreach ($relativePath in @($pluginFiles | Where-Object { $_ -ne 'manifest.json' })) {
    $payloadPath = Join-Path $payloadPlugin $relativePath
    $targetPath = Join-Path $targetPlugin $relativePath
    $targetParent = Split-Path -Parent $targetPath
    New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
    Copy-Item -LiteralPath $payloadPath -Destination $targetPath -Force
}
foreach ($relativePath in $fixtureFiles) {
    Copy-Item -LiteralPath (Join-Path $payloadFixture $relativePath) -Destination (Join-Path $targetFixture $relativePath) -Force
}
Copy-Item -LiteralPath $payloadReadme -Destination (Join-Path $resolvedTarget 'README-TV13.md') -Force
New-Item -ItemType Directory -Path $failureOutput -Force | Out-Null
New-Item -ItemType Directory -Path $recoveryOutput -Force | Out-Null

# The manifest is copied last so its version acts as the update commit marker.
Copy-Item -LiteralPath (Join-Path $payloadPlugin 'manifest.json') -Destination $targetManifest -Force

foreach ($relativePath in $pluginFiles) {
    Assert-FileMatchesPayload -PayloadPath (Join-Path $payloadPlugin $relativePath) -TargetPath (Join-Path $targetPlugin $relativePath)
}
foreach ($relativePath in $fixtureFiles) {
    Assert-FileMatchesPayload -PayloadPath (Join-Path $payloadFixture $relativePath) -TargetPath (Join-Path $targetFixture $relativePath)
}
Assert-FileMatchesPayload -PayloadPath $payloadReadme -TargetPath (Join-Path $resolvedTarget 'README-TV13.md')

$protectedAfter = Get-ProtectedSnapshot -FixtureRoot $targetFixture
if ($protectedBefore -cne $protectedAfter) { throw 'Protected template/source/reference files changed during update.' }
$updatedManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $targetManifest | ConvertFrom-Json
if ($updatedManifest.version -ne $pluginVersion) { throw 'Updated manifest version check failed.' }
& (Join-Path $targetFixture 'verify_tv13.ps1') -FixtureRoot $targetFixture | Out-Null

Write-Output 'UPDATE_PASSED=true'
Write-Output "PACKAGE_VERSION=$packageVersion"
Write-Output "PLUGIN_VERSION=$pluginVersion"
Write-Output "UPDATED_ROOT=$resolvedTarget"
Write-Output "LOAD_MANIFEST=$targetManifest"
Write-Output 'The package folder name is intentionally unchanged.'
