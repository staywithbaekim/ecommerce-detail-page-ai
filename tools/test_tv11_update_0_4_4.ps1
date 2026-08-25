param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tmpParent = Join-Path $root 'tmp'
$testRoot = Join-Path $tmpParent 'tv11-update-0.4.4-roundtrip'
$baseZip = Join-Path $root 'uxp-validation-tv11-whole-product-0.4.2.zip'
$previousUpdateZip = Join-Path $root 'uxp-validation-tv11-update-0.4.3.zip'
$currentUpdateZip = Join-Path $root 'uxp-validation-tv11-update-0.4.4.zip'

foreach ($path in @($baseZip, $previousUpdateZip, $currentUpdateZip)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing test input: $path" }
}
if (Test-Path -LiteralPath $testRoot) { throw "Test root already exists: $testRoot" }

$resolvedTmpParent = [System.IO.Path]::GetFullPath($tmpParent).TrimEnd('\') + '\'
$resolvedTestRoot = [System.IO.Path]::GetFullPath($testRoot)
if (-not $resolvedTestRoot.StartsWith($resolvedTmpParent, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Test root escaped the controlled tmp directory.'
}

try {
    $baseExtract = Join-Path $testRoot 'base'
    $previousExtract = Join-Path $testRoot 'previous-update'
    $currentExtract = Join-Path $testRoot 'current-update'
    Expand-Archive -LiteralPath $baseZip -DestinationPath $baseExtract
    Expand-Archive -LiteralPath $previousUpdateZip -DestinationPath $previousExtract
    Expand-Archive -LiteralPath $currentUpdateZip -DestinationPath $currentExtract

    $baseRoot = Join-Path $baseExtract 'uxp-validation-tv11-whole-product-0.4.2'
    $previousUpdater = Join-Path $previousExtract 'uxp-validation-tv11-update-0.4.3/apply_update_0_4_3.ps1'
    & $previousUpdater -TargetRoot $baseRoot | Write-Output

    $previousRoot = Join-Path $baseExtract 'uxp-validation-tv11-whole-product-0.4.3'
    $currentUpdater = Join-Path $currentExtract 'uxp-validation-tv11-update-0.4.4/apply_update_0_4_4.ps1'
    & $currentUpdater -TargetRoot $previousRoot | Write-Output

    $newRoot = Join-Path $baseExtract 'uxp-validation-tv11-whole-product-0.4.4'
    $manifestPath = Join-Path $newRoot 'uxp-validation-plugin/manifest.json'
    $mainPath = Join-Path $newRoot 'uxp-validation-plugin/main.js'
    $verifierPath = Join-Path $newRoot 'fixture/verify_fixture.ps1'
    $backupRoot = Join-Path $newRoot '_update-backup-0.4.3'
    foreach ($path in @($manifestPath, $mainPath, $verifierPath, $backupRoot)) {
        if (-not (Test-Path -LiteralPath $path)) { throw "Updated package is incomplete: $path" }
    }

    $manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $manifestPath | ConvertFrom-Json
    $mainText = Get-Content -Raw -Encoding UTF8 -LiteralPath $mainPath
    if ($manifest.version -ne '0.4.4') { throw 'Updated manifest version is not 0.4.4.' }
    if ($mainText -notmatch 'strategy:\s*"batchplay_session_token"') { throw 'Updated plugin is missing the session-token open strategy.' }
    if ($mainText -notmatch 'target:\s*\{\s*_path:\s*sessionToken,\s*_kind:\s*"local"\s*\}') { throw 'Updated plugin is missing the approved Action Manager target descriptor.' }
    if ($mainText -match 'null:\s*\{\s*_path:\s*sessionToken') { throw 'Updated plugin contains the rejected null open descriptor.' }
    & $verifierPath -FixtureRoot (Join-Path $newRoot 'fixture') | Write-Output

    Write-Output 'ROUNDTRIP_UPDATE=PASS'
    Write-Output "UPDATED_VERSION=$($manifest.version)"
    Write-Output "UPDATE_SHA256=$((Get-FileHash -Algorithm SHA256 -LiteralPath $currentUpdateZip).Hash)"
    Write-Output "UPDATE_BYTES=$((Get-Item -LiteralPath $currentUpdateZip).Length)"
} finally {
    if (Test-Path -LiteralPath $resolvedTestRoot) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
