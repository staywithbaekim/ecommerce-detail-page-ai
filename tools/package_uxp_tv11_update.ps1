param(
    [string]$Version = '0.4.6'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$packageName = "uxp-validation-tv11-update-$Version"
$zipPath = Join-Path $root "$packageName.zip"
$pluginSource = Join-Path $root 'uxp-validation-plugin'
$planSource = Join-Path $pluginSource 'render-plans/whole-product-a6015-render-plan.json'
$verifierSource = Join-Path $root 'tools/tv11/verify_fixture.ps1'
$versionToken = $Version.Replace('.', '_')
$updaterSource = Join-Path $root "tools/tv11/apply_update_$versionToken.ps1"
$updaterBatSource = Join-Path $root "tools/tv11/APPLY-UPDATE-$Version.bat"
$operationCardMatches = @(Get-ChildItem -LiteralPath (Join-Path $root 'docs') -Recurse -File |
    Where-Object { (Get-Content -Raw -Encoding UTF8 -LiteralPath $_.FullName) -match '<!-- package: tv11 -->' })
if ($operationCardMatches.Count -ne 1) { throw "Expected one TV-11 operation card; found $($operationCardMatches.Count)." }
$operationCardSource = $operationCardMatches[0].FullName

foreach ($path in @($pluginSource, $planSource, $verifierSource, $updaterSource, $updaterBatSource, $operationCardSource)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing update input: $path" }
}
if (Test-Path -LiteralPath $zipPath) { throw "Update package already exists; refusing to overwrite: $zipPath" }

$manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $pluginSource 'manifest.json') | ConvertFrom-Json
$plan = Get-Content -Raw -Encoding UTF8 -LiteralPath $planSource | ConvertFrom-Json
if ($manifest.version -ne $Version) { throw 'Plugin and update package versions differ.' }
$g01Superseded = @($plan.pages[0].frames[0].target.superseded_layers)
if ($plan.pages[0].frames[0].target.expected_clipping -ne $false -or $g01Superseded.Count -ne 1 -or $g01Superseded[0].layer_path -ne '2' -or $g01Superseded[0].expected_clipping -ne $false) { throw 'G01 structured clipping correction is missing.' }

$stagingParent = Join-Path $root 'tmp'
if (-not (Test-Path -LiteralPath $stagingParent -PathType Container)) { New-Item -ItemType Directory -Path $stagingParent | Out-Null }
$stagingRoot = Join-Path $stagingParent ("tv11-update-{0}" -f ([guid]::NewGuid().ToString('N')))
$resolvedStagingParent = [System.IO.Path]::GetFullPath($stagingParent).TrimEnd('\') + '\'
$resolvedStagingRoot = [System.IO.Path]::GetFullPath($stagingRoot)
if (-not $resolvedStagingRoot.StartsWith($resolvedStagingParent, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Staging path escaped the controlled tmp directory.' }

try {
    $packageRoot = Join-Path $stagingRoot $packageName
    $payloadRoot = Join-Path $packageRoot 'payload'
    $payloadFixture = Join-Path $payloadRoot 'fixture'
    New-Item -ItemType Directory -Path $payloadFixture -Force | Out-Null
    Copy-Item -LiteralPath $pluginSource -Destination $payloadRoot -Recurse
    Copy-Item -LiteralPath $planSource -Destination (Join-Path $payloadFixture 'whole-product-a6015-render-plan.json')
    Copy-Item -LiteralPath $verifierSource -Destination (Join-Path $payloadFixture 'verify_fixture.ps1')
    Copy-Item -LiteralPath $operationCardSource -Destination (Join-Path $payloadRoot 'README-TV11.md')
    Copy-Item -LiteralPath $updaterSource -Destination (Join-Path $packageRoot "apply_update_$versionToken.ps1")
    Copy-Item -LiteralPath $updaterBatSource -Destination (Join-Path $packageRoot "APPLY-UPDATE-$Version.bat")

    $forbiddenFiles = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Where-Object { $_.Extension -in @('.psd', '.psb', '.jpg', '.jpeg', '.png', '.tif', '.tiff') })
    if ($forbiddenFiles.Count -ne 0) { throw 'Incremental update unexpectedly contains PSD or image files.' }

    Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal
    $roundTrip = Join-Path $stagingRoot 'roundtrip'
    Expand-Archive -LiteralPath $zipPath -DestinationPath $roundTrip
    $roundTripRoot = Join-Path $roundTrip $packageName
    $roundTripManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $roundTripRoot 'payload/uxp-validation-plugin/manifest.json') | ConvertFrom-Json
    $roundTripPlan = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $roundTripRoot 'payload/fixture/whole-product-a6015-render-plan.json') | ConvertFrom-Json
    $roundTripG01Superseded = @($roundTripPlan.pages[0].frames[0].target.superseded_layers)
    if ($roundTripManifest.version -ne $Version -or $roundTripPlan.pages[0].frames[0].target.expected_clipping -ne $false -or $roundTripG01Superseded.Count -ne 1 -or $roundTripG01Superseded[0].expected_clipping -ne $false) { throw 'Round-trip update content is incorrect.' }

    Write-Output "PACKAGE=$zipPath"
    Write-Output "PACKAGE_SHA256=$((Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash)"
    Write-Output "PACKAGE_BYTES=$((Get-Item -LiteralPath $zipPath).Length)"
    Write-Output 'MEDIA_FILES=0'
    Write-Output 'ROUNDTRIP=PASS'
} finally {
    if (Test-Path -LiteralPath $resolvedStagingRoot) { Remove-Item -LiteralPath $resolvedStagingRoot -Recurse -Force }
}
