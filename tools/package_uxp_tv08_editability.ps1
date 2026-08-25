param(
    [Parameter(Mandatory = $true)]
    [string]$BaselineFolder,

    [Parameter(Mandatory = $true)]
    [string]$FrontSource,

    [Parameter(Mandatory = $false)]
    [string]$Version = '0.2.3.1',

    [Parameter(Mandatory = $false)]
    [string]$DestinationRoot
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
if ([string]::IsNullOrWhiteSpace($DestinationRoot)) { $DestinationRoot = $root }
$resolvedDestination = (Resolve-Path -LiteralPath $DestinationRoot).Path
$resolvedBaseline = (Resolve-Path -LiteralPath $BaselineFolder).Path
$resolvedFront = (Resolve-Path -LiteralPath $FrontSource).Path

$detailWord = ([string][char]0x8BE6) + ([string][char]0x60C5)
$baselineStem = "G-$detailWord-03-validation-output"
$baselinePsd = Join-Path $resolvedBaseline "$baselineStem.psd"
$baselineJpg = Join-Path $resolvedBaseline "$baselineStem.jpg"
$baselineEvidence = Join-Path $resolvedBaseline "G-$detailWord-03-validation-evidence.json"
$englishBaselinePsd = Join-Path $resolvedBaseline 'G-detail-03-validation-output.psd'
$englishBaselineJpg = Join-Path $resolvedBaseline 'G-detail-03-validation-output.jpg'
$englishBaselineEvidence = Join-Path $resolvedBaseline 'G-detail-03-validation-evidence.json'
if (-not (Test-Path -LiteralPath $baselinePsd -PathType Leaf) -and (Test-Path -LiteralPath $englishBaselinePsd -PathType Leaf)) {
    $baselinePsd = $englishBaselinePsd
    $baselineJpg = $englishBaselineJpg
    $baselineEvidence = $englishBaselineEvidence
}
$verifySource = Join-Path $root 'tools/tv08/verify_fixture.ps1'
$startSource = Join-Path $root 'tools/tv08/START-TV08.bat'
$finishSource = Join-Path $root 'tools/tv08/VERIFY-TV08.bat'
$operationCard = Get-ChildItem -LiteralPath (Join-Path $root 'docs') -Recurse -File -Filter 'UXP*.md' |
    Where-Object { Select-String -LiteralPath $_.FullName -SimpleMatch 'TV08 EDIT OK' -Quiet } |
    Select-Object -First 1 -ExpandProperty FullName
$zipPath = Join-Path $resolvedDestination "uxp-validation-tv08-editability-$Version.zip"

$expected = [ordered]@{
    $baselinePsd = 'DD997E7F47B8792744C4EBF57261A5109B50D195BEDFCC8B89FE836DCE7F6949'
    $baselineJpg = 'D18AD1D8389289E06D9F0A66E544DA15D85F181F9D49F2229BA410A9245180A4'
    $baselineEvidence = '358EE8A940A6217BB7A8EF815D2246EEAC3BC3EEABE8E2151E3674E9603CFB0F'
    $resolvedFront = 'C04DDDD4C7802277D9996F347BA6FD4285987147941EED5A69638E0443696D57'
}

foreach ($path in @($baselinePsd, $baselineJpg, $baselineEvidence, $resolvedFront, $verifySource, $startSource, $finishSource, $operationCard)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing package input: $path" }
}
foreach ($entry in $expected.GetEnumerator()) {
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $entry.Key).Hash
    if ($actual -ne $entry.Value) { throw "Package input hash mismatch: $($entry.Key)" }
}
if (Test-Path -LiteralPath $zipPath) { throw "Package already exists; refusing to overwrite: $zipPath" }

$stagingParent = Join-Path $root 'tmp'
if (-not (Test-Path -LiteralPath $stagingParent -PathType Container)) {
    New-Item -ItemType Directory -Path $stagingParent | Out-Null
}
$stagingRoot = Join-Path $stagingParent ("uxp-tv08-package-{0}" -f ([guid]::NewGuid().ToString('N')))
$packageRoot = Join-Path $stagingRoot 'package'
$roundtripRoot = Join-Path $stagingRoot 'roundtrip'
$packageBaseline = Join-Path $packageRoot 'baseline'
$packageSource = Join-Path $packageRoot 'source'
$packageWork = Join-Path $packageRoot 'work'
foreach ($directory in @($packageRoot, $packageBaseline, $packageSource, $packageWork)) {
    New-Item -ItemType Directory -Path $directory | Out-Null
}

Copy-Item -LiteralPath $baselinePsd -Destination (Join-Path $packageBaseline 'G-detail-03-validation-output.psd')
Copy-Item -LiteralPath $baselineJpg -Destination (Join-Path $packageBaseline 'G-detail-03-validation-output.jpg')
Copy-Item -LiteralPath $baselineEvidence -Destination (Join-Path $packageBaseline 'G-detail-03-validation-evidence.json')
Copy-Item -LiteralPath $resolvedFront -Destination (Join-Path $packageSource '7Y9A3750.jpg')
Copy-Item -LiteralPath $verifySource -Destination (Join-Path $packageRoot 'verify_fixture.ps1')
Copy-Item -LiteralPath $startSource -Destination (Join-Path $packageRoot 'START-TV08.bat')
Copy-Item -LiteralPath $finishSource -Destination (Join-Path $packageRoot 'VERIFY-TV08.bat')
Copy-Item -LiteralPath $operationCard -Destination (Join-Path $packageRoot 'README.md')
Set-Content -LiteralPath (Join-Path $packageWork 'RETURN-ITEMS.txt') -Encoding ASCII -Value 'Generated TV-08 evidence and the edited working PSD will be written here.'

$preflight = & (Join-Path $packageRoot 'verify_fixture.ps1') -FixtureRoot $packageRoot | ConvertFrom-Json
if (-not $preflight.fixture_ready -or $preflight.prepared) { throw 'Package preflight did not return the expected state.' }

Compress-Archive -LiteralPath (Get-ChildItem -LiteralPath $packageRoot | ForEach-Object { $_.FullName }) -DestinationPath $zipPath -CompressionLevel Optimal

New-Item -ItemType Directory -Path $roundtripRoot | Out-Null
Expand-Archive -LiteralPath $zipPath -DestinationPath $roundtripRoot
$roundtripPreflight = & (Join-Path $roundtripRoot 'verify_fixture.ps1') -FixtureRoot $roundtripRoot | ConvertFrom-Json
if (-not $roundtripPreflight.fixture_ready -or $roundtripPreflight.integrity.baseline_psd -ne $expected[$baselinePsd]) {
    throw 'Roundtrip package preflight failed.'
}
$roundtripPrepare = & (Join-Path $roundtripRoot 'verify_fixture.ps1') -FixtureRoot $roundtripRoot -Prepare | ConvertFrom-Json
if (-not $roundtripPrepare.prepared) { throw 'Roundtrip preparation failed.' }

$negativeCheckPassed = $false
try {
    & (Join-Path $roundtripRoot 'verify_fixture.ps1') -FixtureRoot $roundtripRoot -RequireEdited | Out-Null
} catch {
    if ($_.Exception.Message -notlike 'Working PSD hash has not changed*') { throw }
    $negativeCheckPassed = $true
}
if (-not $negativeCheckPassed) { throw 'Unedited working copy was not rejected.' }

foreach ($entry in $expected.GetEnumerator()) {
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $entry.Key).Hash
    if ($actual -ne $entry.Value) { throw "Source changed during packaging: $($entry.Key)" }
}

$zipHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash
[ordered]@{
    passed = $true
    package = $zipPath
    package_sha256 = $zipHash
    fixture_version = $preflight.fixture_version
    roundtrip_preflight = $true
    roundtrip_prepare = $true
    unedited_copy_rejected = $true
    staging_root = $stagingRoot
} | ConvertTo-Json -Depth 6
