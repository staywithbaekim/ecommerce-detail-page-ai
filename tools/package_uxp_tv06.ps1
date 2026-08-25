param(
    [Parameter(Mandatory = $false)]
    [string]$Version = '0.3.0',

    [Parameter(Mandatory = $false)]
    [string]$PackageRevision = '0.3.0.1'
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$pluginSource = Join-Path $root 'uxp-validation-plugin'
$planSource = Join-Path $pluginSource 'render-plans/multi-frame-g05-render-plan.json'
$verifySource = Join-Path $root 'tools/tv06/verify_fixture.ps1'
$checkBatSource = Join-Path $root 'tools/tv06/CHECK-TV06-INPUTS.bat'
$verifyBatSource = Join-Path $root 'tools/tv06/VERIFY-TV06.bat'
$selectFolderSource = Join-Path $root 'tools/tv06/SELECT-THIS-FOLDER.md'
$packageName = "uxp-validation-tv06-multi-frame-$PackageRevision"
$zipPath = Join-Path $root "$packageName.zip"
$detailWord = ([string][char]0x8BE6) + ([string][char]0x60C5)
$templateTargetName = "G-$detailWord-05-validation-copy.psd"
$referenceTargetName = "G-$detailWord-05-manual-reference.jpg"

$expectedTemplateHash = '69BF44A64E47CB8D6817FA8589888163B14AB08A19A0BCFCEE02352C3392F5EF'
$expectedReferenceHash = '1377264FB8A2A8AF9F525413BE57A8D0B2FDDE01F8263662B4F3CFFA58F232F1'
$expectedSources = [ordered]@{
    '7Y9A3762.jpg' = 'B7161B1A39447258C39464706CCED111B0736189CFC20C608CB1FAE5353B3DE4'
    '7Y9A3751.jpg' = 'F2862B0162AA2FB6BF969C88E9DF9F0EC4839E5565B0A45CCF4AF8B3721E0DF3'
    '7Y9A3755.jpg' = '868E85930C3996E2565490A89E80154CC6EF26EDABF58044E635B475779079CA'
    '7Y9A3750.jpg' = 'C04DDDD4C7802277D9996F347BA6FD4285987147941EED5A69638E0443696D57'
}

$operationCardSource = Get-ChildItem -LiteralPath (Join-Path $root 'docs') -Recurse -File -Filter '*.md' |
    Where-Object { Select-String -LiteralPath $_.FullName -SimpleMatch $packageName -Quiet } |
    Select-Object -First 1 -ExpandProperty FullName
$templateSource = Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.psd' |
    Where-Object { $_.FullName -notlike "$(Join-Path $root 'tmp')*" -and (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash -eq $expectedTemplateHash } |
    Select-Object -First 1 -ExpandProperty FullName
$referenceSource = Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.jpg' |
    Where-Object { $_.FullName -notlike "$(Join-Path $root 'tmp')*" -and (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash -eq $expectedReferenceHash } |
    Select-Object -First 1 -ExpandProperty FullName
$sourcePaths = [ordered]@{}
foreach ($sourceName in $expectedSources.Keys) {
    $sourcePaths[$sourceName] = Get-ChildItem -LiteralPath $root -Recurse -File -Filter $sourceName |
        Where-Object { $_.FullName -notlike "$(Join-Path $root 'tmp')*" -and (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash -eq $expectedSources[$sourceName] } |
        Select-Object -First 1 -ExpandProperty FullName
}

foreach ($requiredPath in @($pluginSource, $planSource, $verifySource, $checkBatSource, $verifyBatSource, $selectFolderSource, $operationCardSource, $templateSource, $referenceSource) + @($sourcePaths.Values)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) { throw "Missing package input: $requiredPath" }
}
$manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $pluginSource 'manifest.json') | ConvertFrom-Json
if ($manifest.version -ne $Version) { throw "Package version $Version does not match manifest version $($manifest.version)." }
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $templateSource).Hash -ne $expectedTemplateHash) { throw 'G-detail-05 source template hash is not approved.' }
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $referenceSource).Hash -ne $expectedReferenceHash) { throw 'G-detail-05 manual reference hash is not approved.' }
foreach ($sourceName in $expectedSources.Keys) {
    $sourcePath = $sourcePaths[$sourceName]
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw "Missing source image: $sourceName" }
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $sourcePath).Hash -ne $expectedSources[$sourceName]) { throw "Source image hash is not approved: $sourceName" }
}
if (Test-Path -LiteralPath $zipPath) { throw "Package already exists; refusing to overwrite: $zipPath" }

$stagingParent = Join-Path $root 'tmp'
if (-not (Test-Path -LiteralPath $stagingParent -PathType Container)) { New-Item -ItemType Directory -Path $stagingParent | Out-Null }
$stagingRoot = Join-Path $stagingParent ("tv06-package-{0}" -f ([guid]::NewGuid().ToString('N')))
$resolvedStagingParent = [System.IO.Path]::GetFullPath($stagingParent).TrimEnd('\') + '\'
$resolvedStagingRoot = [System.IO.Path]::GetFullPath($stagingRoot)
if (-not $resolvedStagingRoot.StartsWith($resolvedStagingParent, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Staging path escaped the controlled tmp directory.' }

try {
    $packageRoot = Join-Path $stagingRoot $packageName
    $fixtureRoot = Join-Path $packageRoot 'fixture'
    $templateRoot = Join-Path $fixtureRoot 'template'
    $fixtureSourceRoot = Join-Path $fixtureRoot 'source'
    $outputRoot = Join-Path $fixtureRoot 'output'
    $referenceRoot = Join-Path $fixtureRoot 'reference'
    foreach ($directory in @($packageRoot, $fixtureRoot, $templateRoot, $fixtureSourceRoot, $outputRoot, $referenceRoot)) {
        New-Item -ItemType Directory -Path $directory | Out-Null
    }

    Copy-Item -LiteralPath $pluginSource -Destination $packageRoot -Recurse
    Copy-Item -LiteralPath $templateSource -Destination (Join-Path $templateRoot $templateTargetName)
    foreach ($sourceName in $expectedSources.Keys) {
        Copy-Item -LiteralPath $sourcePaths[$sourceName] -Destination (Join-Path $fixtureSourceRoot $sourceName)
    }
    Copy-Item -LiteralPath $referenceSource -Destination (Join-Path $referenceRoot $referenceTargetName)
    Copy-Item -LiteralPath $planSource -Destination (Join-Path $fixtureRoot 'multi-frame-g05-render-plan.json')
    Copy-Item -LiteralPath $verifySource -Destination (Join-Path $fixtureRoot 'verify_fixture.ps1')
    Copy-Item -LiteralPath $checkBatSource -Destination (Join-Path $fixtureRoot 'CHECK-TV06-INPUTS.bat')
    Copy-Item -LiteralPath $verifyBatSource -Destination (Join-Path $fixtureRoot 'VERIFY-TV06.bat')
    Copy-Item -LiteralPath $selectFolderSource -Destination (Join-Path $outputRoot 'SELECT-THIS-FOLDER.md')
    Copy-Item -LiteralPath $operationCardSource -Destination (Join-Path $packageRoot 'README-TV06.md')

    & (Join-Path $fixtureRoot 'verify_fixture.ps1') -FixtureRoot $fixtureRoot | Write-Output
    & 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' -NoProfile -ExecutionPolicy Bypass -File (Join-Path $fixtureRoot 'verify_fixture.ps1') | Write-Output

    $forbiddenPluginFiles = Get-ChildItem -LiteralPath (Join-Path $packageRoot 'uxp-validation-plugin') -Recurse -File |
        Where-Object { $_.Extension -in @('.psd', '.psb', '.jpg', '.jpeg', '.png', '.tif', '.tiff') }
    if ($forbiddenPluginFiles) { throw 'Plugin directory unexpectedly contains user image or PSD files.' }

    Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal

    $roundTripRoot = Join-Path $stagingRoot 'roundtrip'
    Expand-Archive -LiteralPath $zipPath -DestinationPath $roundTripRoot
    $roundTripPackage = Join-Path $roundTripRoot $packageName
    $roundTripFixture = Join-Path $roundTripPackage 'fixture'
    & (Join-Path $roundTripFixture 'verify_fixture.ps1') -FixtureRoot $roundTripFixture | Write-Output
    & 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' -NoProfile -ExecutionPolicy Bypass -File (Join-Path $roundTripFixture 'verify_fixture.ps1') | Write-Output
    $roundTripManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $roundTripPackage 'uxp-validation-plugin/manifest.json') | ConvertFrom-Json
    if ($roundTripManifest.version -ne $Version) { throw 'Round-trip package manifest version mismatch.' }

    Write-Output "PACKAGE=$zipPath"
    Write-Output "PACKAGE_SHA256=$((Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash)"
    Write-Output "PACKAGE_BYTES=$((Get-Item -LiteralPath $zipPath).Length)"
    Write-Output "PLUGIN_VERSION=$($roundTripManifest.version)"
    Write-Output 'ROUNDTRIP_FIXTURE_CHECK=PASS'
} finally {
    if (Test-Path -LiteralPath $resolvedStagingRoot) {
        Remove-Item -LiteralPath $resolvedStagingRoot -Recurse -Force
    }
}
