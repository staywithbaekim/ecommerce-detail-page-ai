param(
    [string]$PackageVersion = '0.4.7.1',
    [string]$PluginVersion = '0.4.7'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$packageName = "uxp-validation-tv13-update-$PackageVersion"
$zipPath = Join-Path $root "$packageName.zip"
$pluginSource = Join-Path $root 'uxp-validation-plugin'
$failurePlanSource = Join-Path $pluginSource 'render-plans/tv13-controlled-failure-render-plan.json'
$verifierSource = Join-Path $root 'tools/tv13/verify_tv13.ps1'
$checkBatSource = Join-Path $root 'tools/tv13/CHECK-TV13-INPUTS.bat'
$verifyBatSource = Join-Path $root 'tools/tv13/VERIFY-TV13.bat'
$updaterSource = Join-Path $root 'tools/tv13/apply_update_0_4_7_1.ps1'
$updaterBatSource = Join-Path $root 'tools/tv13/APPLY-RECOVERY-0.4.7.1.bat'
$operationCards = @(Get-ChildItem -LiteralPath (Join-Path $root 'docs') -Recurse -File | Where-Object { (Get-Content -Raw -Encoding UTF8 -LiteralPath $_.FullName) -match '<!-- package: tv13 -->' })
if ($operationCards.Count -ne 1) { throw "Expected one TV-13 operation card; found $($operationCards.Count)." }
$operationCardSource = $operationCards[0].FullName

foreach ($path in @($pluginSource, $failurePlanSource, $verifierSource, $checkBatSource, $verifyBatSource, $updaterSource, $updaterBatSource, $operationCardSource)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing TV-13 update input: $path" }
}
if (Test-Path -LiteralPath $zipPath) { throw "Update package already exists; refusing to overwrite: $zipPath" }
$manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $pluginSource 'manifest.json') | ConvertFrom-Json
if ($manifest.version -ne $PluginVersion) { throw 'Plugin and TV-13 package versions differ.' }

$stagingParent = Join-Path $root 'tmp'
if (-not (Test-Path -LiteralPath $stagingParent)) { New-Item -ItemType Directory -Path $stagingParent | Out-Null }
$stagingRoot = Join-Path $stagingParent ("tv13-update-{0}" -f ([guid]::NewGuid().ToString('N')))
$resolvedParent = [System.IO.Path]::GetFullPath($stagingParent).TrimEnd('\') + '\'
$resolvedStaging = [System.IO.Path]::GetFullPath($stagingRoot)
if (-not $resolvedStaging.StartsWith($resolvedParent, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'TV-13 staging path escaped tmp.' }

try {
    $packageRoot = Join-Path $stagingRoot $packageName
    $payloadRoot = Join-Path $packageRoot 'payload'
    $payloadFixture = Join-Path $payloadRoot 'fixture'
    New-Item -ItemType Directory -Path $payloadFixture -Force | Out-Null
    Copy-Item -LiteralPath $pluginSource -Destination $payloadRoot -Recurse
    Copy-Item -LiteralPath $failurePlanSource -Destination $payloadFixture
    Copy-Item -LiteralPath $verifierSource -Destination $payloadFixture
    Copy-Item -LiteralPath $checkBatSource -Destination $payloadFixture
    Copy-Item -LiteralPath $verifyBatSource -Destination $payloadFixture
    Copy-Item -LiteralPath $operationCardSource -Destination (Join-Path $payloadRoot 'README-TV13.md')
    Copy-Item -LiteralPath $updaterSource -Destination $packageRoot
    Copy-Item -LiteralPath $updaterBatSource -Destination $packageRoot

    $media = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Where-Object { $_.Extension -in @('.psd','.psb','.jpg','.jpeg','.png','.tif','.tiff') })
    if ($media.Count -ne 0) { throw 'TV-13 update unexpectedly contains PSD or image files.' }
    Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal

    $roundTrip = Join-Path $stagingRoot 'roundtrip'
    Expand-Archive -LiteralPath $zipPath -DestinationPath $roundTrip
    $roundTripRoot = Join-Path $roundTrip $packageName
    $roundTripManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $roundTripRoot 'payload/uxp-validation-plugin/manifest.json') | ConvertFrom-Json
    $roundTripPlan = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $roundTripRoot 'payload/fixture/tv13-controlled-failure-render-plan.json') | ConvertFrom-Json
    if ($roundTripManifest.version -ne $PluginVersion -or $roundTripPlan.validation.controlled_failure.error_code -ne 'TV13_CONTROLLED_FAILURE') { throw 'TV-13 update round-trip content is incorrect.' }

    Write-Output "PACKAGE=$zipPath"
    Write-Output "PACKAGE_SHA256=$((Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash)"
    Write-Output "PACKAGE_BYTES=$((Get-Item -LiteralPath $zipPath).Length)"
    Write-Output 'MEDIA_FILES=0'
    Write-Output 'ROUNDTRIP=PASS'
} finally {
    if (Test-Path -LiteralPath $resolvedStaging) { Remove-Item -LiteralPath $resolvedStaging -Recurse -Force }
}
