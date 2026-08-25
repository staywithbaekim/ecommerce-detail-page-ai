param(
    [Parameter(Mandatory = $false)]
    [string]$Version = '0.3.5'
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$pluginSource = Join-Path $root 'uxp-validation-plugin'
$packageName = "uxp-validation-tv06-plugin-$Version"
$zipPath = Join-Path $root "$packageName.zip"
$manifestPath = Join-Path $pluginSource 'manifest.json'

$manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $manifestPath | ConvertFrom-Json
if ($manifest.version -ne $Version) { throw "Package version $Version does not match manifest version $($manifest.version)." }
if (Test-Path -LiteralPath $zipPath) { throw "Package already exists; refusing to overwrite: $zipPath" }

$forbiddenFiles = Get-ChildItem -LiteralPath $pluginSource -Recurse -File |
    Where-Object { $_.Extension -in @('.psd', '.psb', '.jpg', '.jpeg', '.png', '.tif', '.tiff') }
if ($forbiddenFiles) { throw 'Plugin directory unexpectedly contains user image or PSD files.' }

$stagingParent = Join-Path $root 'tmp'
if (-not (Test-Path -LiteralPath $stagingParent -PathType Container)) { New-Item -ItemType Directory -Path $stagingParent | Out-Null }
$stagingRoot = Join-Path $stagingParent ("tv06-plugin-package-{0}" -f ([guid]::NewGuid().ToString('N')))
$resolvedStagingParent = [System.IO.Path]::GetFullPath($stagingParent).TrimEnd('\') + '\'
$resolvedStagingRoot = [System.IO.Path]::GetFullPath($stagingRoot)
if (-not $resolvedStagingRoot.StartsWith($resolvedStagingParent, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Staging path escaped the controlled tmp directory.' }

try {
    $packageRoot = Join-Path $stagingRoot $packageName
    Copy-Item -LiteralPath $pluginSource -Destination $packageRoot -Recurse
    Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal

    $roundTripRoot = Join-Path $stagingRoot 'roundtrip'
    Expand-Archive -LiteralPath $zipPath -DestinationPath $roundTripRoot
    $roundTripPackage = Join-Path $roundTripRoot $packageName
    $roundTripManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $roundTripPackage 'manifest.json') | ConvertFrom-Json
    if ($roundTripManifest.version -ne $Version) { throw 'Round-trip plugin manifest version mismatch.' }
    foreach ($requiredName in @('main.js', 'index.html', 'jpeg-icc.js', 'THIRD_PARTY_NOTICES.md')) {
        if (-not (Test-Path -LiteralPath (Join-Path $roundTripPackage $requiredName) -PathType Leaf)) { throw "Round-trip plugin file is missing: $requiredName" }
    }

    Write-Output "PACKAGE=$zipPath"
    Write-Output "PACKAGE_SHA256=$((Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash)"
    Write-Output "PACKAGE_BYTES=$((Get-Item -LiteralPath $zipPath).Length)"
    Write-Output "PLUGIN_VERSION=$($roundTripManifest.version)"
    Write-Output 'ROUNDTRIP_PLUGIN_CHECK=PASS'
} finally {
    if (Test-Path -LiteralPath $resolvedStagingRoot) {
        Remove-Item -LiteralPath $resolvedStagingRoot -Recurse -Force
    }
}
