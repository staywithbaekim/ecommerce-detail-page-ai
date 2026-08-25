param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tmpParent = Join-Path $root 'tmp'
$testRoot = Join-Path $tmpParent 'tv11-update-0.4.5-roundtrip'
$baseZip = Join-Path $root 'uxp-validation-tv11-whole-product-0.4.2.zip'
$update043Zip = Join-Path $root 'uxp-validation-tv11-update-0.4.3.zip'
$update044Zip = Join-Path $root 'uxp-validation-tv11-update-0.4.4.zip'
$update045Zip = Join-Path $root 'uxp-validation-tv11-update-0.4.5.zip'

foreach ($path in @($baseZip, $update043Zip, $update044Zip, $update045Zip)) {
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
    $update043Extract = Join-Path $testRoot 'update-0.4.3'
    $update044Extract = Join-Path $testRoot 'update-0.4.4'
    $update045Extract = Join-Path $testRoot 'update-0.4.5'
    Expand-Archive -LiteralPath $baseZip -DestinationPath $baseExtract
    Expand-Archive -LiteralPath $update043Zip -DestinationPath $update043Extract
    Expand-Archive -LiteralPath $update044Zip -DestinationPath $update044Extract
    Expand-Archive -LiteralPath $update045Zip -DestinationPath $update045Extract

    $root042 = Join-Path $baseExtract 'uxp-validation-tv11-whole-product-0.4.2'
    & (Join-Path $update043Extract 'uxp-validation-tv11-update-0.4.3/apply_update_0_4_3.ps1') -TargetRoot $root042 | Write-Output
    $root043 = Join-Path $baseExtract 'uxp-validation-tv11-whole-product-0.4.3'
    & (Join-Path $update044Extract 'uxp-validation-tv11-update-0.4.4/apply_update_0_4_4.ps1') -TargetRoot $root043 | Write-Output
    $root044 = Join-Path $baseExtract 'uxp-validation-tv11-whole-product-0.4.4'
    & (Join-Path $update045Extract 'uxp-validation-tv11-update-0.4.5/apply_update_0_4_5.ps1') -TargetRoot $root044 | Write-Output

    $root045 = Join-Path $baseExtract 'uxp-validation-tv11-whole-product-0.4.5'
    $manifestPath = Join-Path $root045 'uxp-validation-plugin/manifest.json'
    $mainPath = Join-Path $root045 'uxp-validation-plugin/main.js'
    $planPath = Join-Path $root045 'fixture/whole-product-a6015-render-plan.json'
    $verifierPath = Join-Path $root045 'fixture/verify_fixture.ps1'
    $backupRoot = Join-Path $root045 '_update-backup-0.4.4'
    foreach ($path in @($manifestPath, $mainPath, $planPath, $verifierPath, $backupRoot)) {
        if (-not (Test-Path -LiteralPath $path)) { throw "Updated package is incomplete: $path" }
    }

    $manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $manifestPath | ConvertFrom-Json
    $mainText = Get-Content -Raw -Encoding UTF8 -LiteralPath $mainPath
    $plan = Get-Content -Raw -Encoding UTF8 -LiteralPath $planPath | ConvertFrom-Json
    if ($manifest.version -ne '0.4.5') { throw 'Updated manifest version is not 0.4.5.' }
    if ($mainText -notmatch 'strategy:\s*"batchplay_session_token"') { throw 'Updated plugin lost the session-token open strategy.' }
    if ($mainText -notmatch 'supersededStructure\.clipping === supersededContract\.expected_clipping') { throw 'Updated plugin is missing calibrated superseded-layer clipping validation.' }

    $contracts = @($plan.pages | ForEach-Object { @($_.frames) } | ForEach-Object { @($_.target.superseded_layers) })
    if ($contracts.Count -ne 14) { throw 'Updated plan does not contain fourteen superseded-layer contracts.' }
    if (@($contracts | Where-Object { $_.expected_clipping -eq $false }).Count -ne 1) { throw 'Updated plan has an incorrect non-clipping superseded-layer count.' }
    $g01Contract = @($plan.pages[0].frames[0].target.superseded_layers)
    if ($g01Contract.Count -ne 1 -or $g01Contract[0].layer_path -ne '2' -or $g01Contract[0].expected_clipping -ne $false) { throw 'Updated plan has an incorrect G01 superseded-layer contract.' }
    & $verifierPath -FixtureRoot (Join-Path $root045 'fixture') | Write-Output

    Write-Output 'ROUNDTRIP_UPDATE=PASS'
    Write-Output "UPDATED_VERSION=$($manifest.version)"
    Write-Output "UPDATE_SHA256=$((Get-FileHash -Algorithm SHA256 -LiteralPath $update045Zip).Hash)"
    Write-Output "UPDATE_BYTES=$((Get-Item -LiteralPath $update045Zip).Length)"
} finally {
    if (Test-Path -LiteralPath $resolvedTestRoot) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
