param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tmpParent = Join-Path $root 'tmp'
$testRoot = Join-Path $tmpParent 'tv13-update-0.4.7-roundtrip'
$baseZip = Join-Path $root 'uxp-validation-tv11-whole-product-0.4.6.zip'
$updateZip = Join-Path $root 'uxp-validation-tv13-update-0.4.7.zip'
foreach ($path in @($baseZip, $updateZip)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing test input: $path" }
}
if (Test-Path -LiteralPath $testRoot) { throw "Test root already exists: $testRoot" }
$resolvedParent = [System.IO.Path]::GetFullPath($tmpParent).TrimEnd('\') + '\'
$resolvedTest = [System.IO.Path]::GetFullPath($testRoot)
if (-not $resolvedTest.StartsWith($resolvedParent, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Test root escaped tmp.' }

try {
    $baseExtract = Join-Path $testRoot 'base'
    $updateExtract = Join-Path $testRoot 'update'
    Expand-Archive -LiteralPath $baseZip -DestinationPath $baseExtract
    Expand-Archive -LiteralPath $updateZip -DestinationPath $updateExtract
    $oldRoot = Join-Path $baseExtract 'uxp-validation-tv11-whole-product-0.4.6'
    $updater = Join-Path $updateExtract 'uxp-validation-tv13-update-0.4.7/apply_update_0_4_7.ps1'
    & $updater -TargetRoot $oldRoot | Write-Output

    $newRoot = Join-Path $baseExtract 'uxp-validation-tv11-whole-product-0.4.7'
    $fixture = Join-Path $newRoot 'fixture'
    $manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $newRoot 'uxp-validation-plugin/manifest.json') | ConvertFrom-Json
    $failurePlan = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $fixture 'tv13-controlled-failure-render-plan.json') | ConvertFrom-Json
    if ($manifest.version -ne '0.4.7') { throw 'Updated manifest is not 0.4.7.' }
    if ($failurePlan.validation.controlled_failure.completed_page_count -ne 2) { throw 'Updated TV-13 checkpoint is incorrect.' }
    foreach ($path in @('verify_tv13.ps1','CHECK-TV13-INPUTS.bat','VERIFY-TV13.bat','tv13-failure-output','tv13-recovery-output')) {
        if (-not (Test-Path -LiteralPath (Join-Path $fixture $path))) { throw "Updated TV-13 fixture is incomplete: $path" }
    }
    & (Join-Path $fixture 'verify_tv13.ps1') -FixtureRoot $fixture | Write-Output
    Write-Output 'ROUNDTRIP_UPDATE=PASS'
    Write-Output "UPDATED_VERSION=$($manifest.version)"
    Write-Output "UPDATE_SHA256=$((Get-FileHash -Algorithm SHA256 -LiteralPath $updateZip).Hash)"
    Write-Output "UPDATE_BYTES=$((Get-Item -LiteralPath $updateZip).Length)"
} finally {
    if (Test-Path -LiteralPath $resolvedTest) { Remove-Item -LiteralPath $resolvedTest -Recurse -Force }
}
