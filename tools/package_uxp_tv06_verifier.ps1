param(
    [Parameter(Mandatory = $false)]
    [string]$Version = '0.3.5'
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$sourceRoot = Join-Path $root 'tools/tv06'
$packageName = "uxp-validation-tv06-verifier-$Version"
$zipPath = Join-Path $root "$packageName.zip"
$requiredNames = @('verify_fixture.ps1', 'VERIFY-TV06.bat', "README-VERIFIER-$Version.txt")

foreach ($name in $requiredNames) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot $name) -PathType Leaf)) {
        throw "Missing verifier package input: $name"
    }
}
$verifierText = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $sourceRoot 'verify_fixture.ps1')
if ($verifierText -notmatch [regex]::Escape("plugin_version -ne '$Version'")) {
    throw "Verifier does not require plugin version $Version."
}
if (Test-Path -LiteralPath $zipPath) {
    throw "Package already exists; refusing to overwrite: $zipPath"
}

$stagingParent = Join-Path $root 'tmp'
if (-not (Test-Path -LiteralPath $stagingParent -PathType Container)) {
    New-Item -ItemType Directory -Path $stagingParent | Out-Null
}
$stagingRoot = Join-Path $stagingParent ("tv06-verifier-package-{0}" -f ([guid]::NewGuid().ToString('N')))
$resolvedStagingParent = [System.IO.Path]::GetFullPath($stagingParent).TrimEnd('\') + '\'
$resolvedStagingRoot = [System.IO.Path]::GetFullPath($stagingRoot)
if (-not $resolvedStagingRoot.StartsWith($resolvedStagingParent, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Staging path escaped the controlled tmp directory.'
}

try {
    $packageRoot = Join-Path $stagingRoot $packageName
    New-Item -ItemType Directory -Path $packageRoot | Out-Null
    foreach ($name in $requiredNames) {
        Copy-Item -LiteralPath (Join-Path $sourceRoot $name) -Destination (Join-Path $packageRoot $name)
    }
    Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal

    $roundTripRoot = Join-Path $stagingRoot 'roundtrip'
    Expand-Archive -LiteralPath $zipPath -DestinationPath $roundTripRoot
    $roundTripPackage = Join-Path $roundTripRoot $packageName
    foreach ($name in $requiredNames) {
        if (-not (Test-Path -LiteralPath (Join-Path $roundTripPackage $name) -PathType Leaf)) {
            throw "Round-trip verifier file is missing: $name"
        }
    }
    $forbidden = Get-ChildItem -LiteralPath $roundTripPackage -Recurse -File |
        Where-Object { $_.Extension -in @('.psd', '.psb', '.jpg', '.jpeg', '.png', '.tif', '.tiff') }
    if ($forbidden) { throw 'Verifier package unexpectedly contains media or PSD files.' }

    Write-Output "PACKAGE=$zipPath"
    Write-Output "PACKAGE_SHA256=$((Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash)"
    Write-Output "PACKAGE_BYTES=$((Get-Item -LiteralPath $zipPath).Length)"
    Write-Output 'ROUNDTRIP_VERIFIER_CHECK=PASS'
} finally {
    if (Test-Path -LiteralPath $resolvedStagingRoot) {
        Remove-Item -LiteralPath $resolvedStagingRoot -Recurse -Force
    }
}
