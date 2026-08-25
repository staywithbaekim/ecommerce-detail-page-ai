param(
    [Parameter(Mandatory = $false)]
    [string]$Version = '0.2.3'
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$pluginSource = Join-Path $root 'uxp-validation-plugin'
$bridgeSource = Join-Path $root 'tools/uxp_validation_bridge.ps1'
$verifySource = Join-Path $root 'tools/verify_uxp_single_frame_fixture.ps1'
$profileGeneratorSource = Join-Path $root 'tools/generate_srgb_icc_profile.py'
$planSource = Join-Path $pluginSource 'render-plans/single-frame-g03-render-plan.json'
$expectedTemplateHash = 'B94E7458A33F645E322F30EFDE675CCB268BE5A72CA2FF4B7618F241EED14A35'
$expectedImageHash = '04720404A97FBE6DFD6EB138946C129545F37020F81346D98AA0616668C0B0DB'
$detailWord = ([string][char]0x8BE6) + ([string][char]0x60C5)
$templateCopyName = "G-$detailWord-03-validation-copy.psd"
$operationCardSource = Get-ChildItem -LiteralPath (Join-Path $root 'docs') -Recurse -File -Filter '*.md' |
    Where-Object { $_.Name -like 'UXP*.md' -and (Select-String -LiteralPath $_.FullName -SimpleMatch "uxp-validation-probe-$Version.zip" -Quiet) } |
    Select-Object -First 1 -ExpandProperty FullName
$templateSource = Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.psd' |
    Where-Object { $_.FullName -notlike "$(Join-Path $root 'tmp')*" -and (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash -eq $expectedTemplateHash } |
    Select-Object -First 1 -ExpandProperty FullName
$imageSource = Get-ChildItem -LiteralPath $root -Recurse -File -Filter '7Y9A3760.jpg' |
    Where-Object { $_.FullName -notlike "$(Join-Path $root 'tmp')*" -and (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash -eq $expectedImageHash } |
    Select-Object -First 1 -ExpandProperty FullName
$pluginZip = Join-Path $root "uxp-validation-probe-$Version.zip"
$fixtureZip = Join-Path $root "uxp-validation-g03-fixture-$Version.zip"

foreach ($requiredPath in @($pluginSource, $bridgeSource, $verifySource, $profileGeneratorSource, $operationCardSource, $planSource, $templateSource, $imageSource)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) { throw "Missing package input: $requiredPath" }
}
$manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $pluginSource 'manifest.json') | ConvertFrom-Json
if ($manifest.version -ne $Version) { throw "Package version $Version does not match manifest version $($manifest.version)." }
foreach ($destination in @($pluginZip, $fixtureZip)) {
    if (Test-Path -LiteralPath $destination) { throw "Package already exists; refusing to overwrite: $destination" }
}

$stagingParent = Join-Path $root 'tmp'
if (-not (Test-Path -LiteralPath $stagingParent -PathType Container)) {
    New-Item -ItemType Directory -Path $stagingParent | Out-Null
}
$stagingRoot = Join-Path $stagingParent ("uxp-validation-package-{0}" -f ([guid]::NewGuid().ToString('N')))
New-Item -ItemType Directory -Path $stagingRoot | Out-Null

$pluginPackageRoot = Join-Path $stagingRoot 'plugin-package'
$pluginToolsRoot = Join-Path $pluginPackageRoot 'tools'
New-Item -ItemType Directory -Path $pluginPackageRoot | Out-Null
New-Item -ItemType Directory -Path $pluginToolsRoot | Out-Null
Copy-Item -LiteralPath $pluginSource -Destination $pluginPackageRoot -Recurse
Copy-Item -LiteralPath $bridgeSource -Destination $pluginToolsRoot
Copy-Item -LiteralPath $verifySource -Destination $pluginToolsRoot
Copy-Item -LiteralPath $profileGeneratorSource -Destination $pluginToolsRoot
Copy-Item -LiteralPath $operationCardSource -Destination (Join-Path $pluginPackageRoot 'UXP-single-frame-validation-card.md')

$fixtureRoot = Join-Path $stagingRoot 'fixture-package'
$fixtureTemplateRoot = Join-Path $fixtureRoot 'template'
$fixtureSourceRoot = Join-Path $fixtureRoot 'source'
$fixtureOutputRoot = Join-Path $fixtureRoot 'output'
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
New-Item -ItemType Directory -Path $fixtureTemplateRoot | Out-Null
New-Item -ItemType Directory -Path $fixtureSourceRoot | Out-Null
New-Item -ItemType Directory -Path $fixtureOutputRoot | Out-Null
Copy-Item -LiteralPath $templateSource -Destination (Join-Path $fixtureTemplateRoot $templateCopyName)
Copy-Item -LiteralPath $imageSource -Destination (Join-Path $fixtureSourceRoot '7Y9A3760.jpg')
Copy-Item -LiteralPath $planSource -Destination (Join-Path $fixtureRoot 'single-frame-g03-render-plan.json')
Copy-Item -LiteralPath $verifySource -Destination (Join-Path $fixtureRoot 'verify_fixture.ps1')
Copy-Item -LiteralPath $operationCardSource -Destination (Join-Path $fixtureRoot 'README.md')
Copy-Item -LiteralPath $operationCardSource -Destination (Join-Path $fixtureOutputRoot 'SELECT-THIS-FOLDER.md')

& $verifySource -FixtureRoot $fixtureRoot | Write-Output
Compress-Archive -LiteralPath (Get-ChildItem -LiteralPath $pluginPackageRoot | ForEach-Object { $_.FullName }) -DestinationPath $pluginZip -CompressionLevel Optimal
Compress-Archive -LiteralPath (Get-ChildItem -LiteralPath $fixtureRoot | ForEach-Object { $_.FullName }) -DestinationPath $fixtureZip -CompressionLevel Optimal

$pluginHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $pluginZip).Hash
$fixtureHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $fixtureZip).Hash
Write-Output "PLUGIN_ZIP=$pluginZip"
Write-Output "PLUGIN_SHA256=$pluginHash"
Write-Output "FIXTURE_ZIP=$fixtureZip"
Write-Output "FIXTURE_SHA256=$fixtureHash"
Write-Output "STAGING_ROOT=$stagingRoot"
