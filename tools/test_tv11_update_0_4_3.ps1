param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tmpParent = Join-Path $root 'tmp'
$testRoot = Join-Path $tmpParent 'tv11-update-0.4.3-roundtrip'
$oldZip = Join-Path $root 'uxp-validation-tv11-whole-product-0.4.2.zip'
$updateZip = Join-Path $root 'uxp-validation-tv11-update-0.4.3.zip'

foreach ($path in @($oldZip, $updateZip)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing test input: $path" }
}
if (Test-Path -LiteralPath $testRoot) { throw "Test root already exists: $testRoot" }

$resolvedTmpParent = [System.IO.Path]::GetFullPath($tmpParent).TrimEnd('\') + '\'
$resolvedTestRoot = [System.IO.Path]::GetFullPath($testRoot)
if (-not $resolvedTestRoot.StartsWith($resolvedTmpParent, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Test root escaped the controlled tmp directory.'
}

try {
    $oldExtract = Join-Path $testRoot 'old'
    $updateExtract = Join-Path $testRoot 'update'
    Expand-Archive -LiteralPath $oldZip -DestinationPath $oldExtract
    Expand-Archive -LiteralPath $updateZip -DestinationPath $updateExtract

    $oldRoot = Join-Path $oldExtract 'uxp-validation-tv11-whole-product-0.4.2'
    $updater = Join-Path $updateExtract 'uxp-validation-tv11-update-0.4.3/apply_update_0_4_3.ps1'
    & $updater -TargetRoot $oldRoot | Write-Output

    $newRoot = Join-Path $oldExtract 'uxp-validation-tv11-whole-product-0.4.3'
    $manifestPath = Join-Path $newRoot 'uxp-validation-plugin/manifest.json'
    $planPath = Join-Path $newRoot 'fixture/whole-product-a6015-render-plan.json'
    $verifierPath = Join-Path $newRoot 'fixture/verify_fixture.ps1'
    $backupRoot = Join-Path $newRoot '_update-backup-0.4.2'
    foreach ($path in @($manifestPath, $planPath, $verifierPath, $backupRoot)) {
        if (-not (Test-Path -LiteralPath $path)) { throw "Updated package is incomplete: $path" }
    }

    $manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $manifestPath | ConvertFrom-Json
    $plan = Get-Content -Raw -Encoding UTF8 -LiteralPath $planPath | ConvertFrom-Json
    if ($manifest.version -ne '0.4.3') { throw 'Updated manifest version is not 0.4.3.' }
    if ($plan.pages[0].frames[0].target.expected_clipping -ne $false) { throw 'Updated G01 binding is incorrect.' }
    & $verifierPath -FixtureRoot (Join-Path $newRoot 'fixture') | Write-Output

    Write-Output 'ROUNDTRIP_UPDATE=PASS'
    Write-Output "UPDATED_VERSION=$($manifest.version)"
    Write-Output "UPDATE_SHA256=$((Get-FileHash -Algorithm SHA256 -LiteralPath $updateZip).Hash)"
    Write-Output "UPDATE_BYTES=$((Get-Item -LiteralPath $updateZip).Length)"
} finally {
    if (Test-Path -LiteralPath $resolvedTestRoot) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
