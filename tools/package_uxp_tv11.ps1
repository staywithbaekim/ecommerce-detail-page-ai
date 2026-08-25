param(
    [string]$Version = '0.4.6'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$packageName = "uxp-validation-tv11-whole-product-$Version"
$zipPath = Join-Path $root "$packageName.zip"
$pluginSource = Join-Path $root 'uxp-validation-plugin'
$planSource = Join-Path $pluginSource 'render-plans/whole-product-a6015-render-plan.json'
$verifySource = Join-Path $root 'tools/tv11/verify_fixture.ps1'
$checkBatSource = Join-Path $root 'tools/tv11/CHECK-TV11-INPUTS.bat'
$verifyBatSource = Join-Path $root 'tools/tv11/VERIFY-TV11.bat'
$selectFolderSource = Join-Path $root 'tools/tv11/SELECT-THIS-FOLDER.md'
$operationCardMatches = @(Get-ChildItem -LiteralPath (Join-Path $root 'docs') -Recurse -File |
    Where-Object { (Get-Content -Raw -Encoding UTF8 -LiteralPath $_.FullName) -match '<!-- package: tv11 -->' })
if ($operationCardMatches.Count -ne 1) { throw "Expected one TV-11 operation card; found $($operationCardMatches.Count)." }
$operationCardSource = $operationCardMatches[0].FullName

foreach ($requiredPath in @($pluginSource, $planSource, $verifySource, $checkBatSource, $verifyBatSource, $selectFolderSource, $operationCardSource)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) { throw "Missing package input: $requiredPath" }
}
if (Test-Path -LiteralPath $zipPath) { throw "Package already exists; refusing to overwrite: $zipPath" }

$manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $pluginSource 'manifest.json') | ConvertFrom-Json
if ($manifest.version -ne $Version) { throw "Package version $Version does not match manifest version $($manifest.version)." }
$plan = Get-Content -Raw -Encoding UTF8 -LiteralPath $planSource | ConvertFrom-Json
if ($plan.validation.mode -ne 'whole_product_copy' -or @($plan.pages).Count -ne 6) { throw 'Whole-product RenderPlan is not ready for packaging.' }

$fileHashCache = @{}
function Get-CachedHash {
    param([System.IO.FileInfo]$File)
    if (-not $fileHashCache.ContainsKey($File.FullName)) {
        $fileHashCache[$File.FullName] = (Get-FileHash -Algorithm SHA256 -LiteralPath $File.FullName).Hash
    }
    return $fileHashCache[$File.FullName]
}
function Find-FileByHash {
    param([System.IO.FileInfo[]]$Files, [string]$Hash, [string]$Description, [string]$PreferredName = '')
    $matches = @($Files | Where-Object { (Get-CachedHash $_) -eq $Hash.ToUpperInvariant() })
    if ($PreferredName -and $matches.Count -gt 1) {
        $preferredMatches = @($matches | Where-Object { $_.Name -eq $PreferredName })
        if ($preferredMatches.Count -eq 1) { return $preferredMatches[0] }
    }
    if ($matches.Count -eq 0) { throw "Expected at least one approved file for $Description; found none." }
    return $matches[0]
}
$allPsdFiles = @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.psd')
$allJpgFiles = @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.jpg')
$referenceHashes = @{
    '790x1327' = 'D2A99449C920F06C4AB419A9558BBB34D332A03F55E275AEA7F738AE57A29422'
    '790x1252' = '63898394C8833DF916E0CD7EF4B3B6852519368E8AC2CC858D2A888713F01309'
    '790x1182' = 'CFDF12FB2AB9622FB462060ACD2AB89CCB18DF7CB4AFEAA9CC3BB11532604B0C'
    '790x1115' = '1377264FB8A2A8AF9F525413BE57A8D0B2FDDE01F8263662B4F3CFFA58F232F1'
    '790x2425' = '107B1613EA9CB6F63A3A8DA8DEBB6B4A1721B09A483FB00F849B4C7288CEB5B4'
    '790x2546' = 'DF12B64F9A2B897DA6B4140D28CBAF287422177C1A4C690B561C8B6686A3F8F3'
}

$stagingParent = Join-Path $root 'tmp'
if (-not (Test-Path -LiteralPath $stagingParent -PathType Container)) { New-Item -ItemType Directory -Path $stagingParent | Out-Null }
$stagingRoot = Join-Path $stagingParent ("tv11-package-{0}" -f ([guid]::NewGuid().ToString('N')))
$resolvedStagingParent = [System.IO.Path]::GetFullPath($stagingParent).TrimEnd('\') + '\'
$resolvedStagingRoot = [System.IO.Path]::GetFullPath($stagingRoot)
if (-not $resolvedStagingRoot.StartsWith($resolvedStagingParent, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Staging path escaped the controlled tmp directory.' }

try {
    $packageRoot = Join-Path $stagingRoot $packageName
    $fixtureRoot = Join-Path $packageRoot 'fixture'
    $templateRoot = Join-Path $fixtureRoot 'template'
    $fixtureSourceRoot = Join-Path $fixtureRoot 'source'
    $referenceRoot = Join-Path $fixtureRoot 'reference'
    $outputRoot = Join-Path $fixtureRoot 'output'
    foreach ($directory in @($packageRoot, $fixtureRoot, $templateRoot, $fixtureSourceRoot, $referenceRoot, $outputRoot)) {
        New-Item -ItemType Directory -Path $directory | Out-Null
    }

    Copy-Item -LiteralPath $pluginSource -Destination $packageRoot -Recurse
    Copy-Item -LiteralPath $planSource -Destination (Join-Path $fixtureRoot 'whole-product-a6015-render-plan.json')
    Copy-Item -LiteralPath $verifySource -Destination (Join-Path $fixtureRoot 'verify_fixture.ps1')
    Copy-Item -LiteralPath $checkBatSource -Destination (Join-Path $fixtureRoot 'CHECK-TV11-INPUTS.bat')
    Copy-Item -LiteralPath $verifyBatSource -Destination (Join-Path $fixtureRoot 'VERIFY-TV11.bat')
    Copy-Item -LiteralPath $selectFolderSource -Destination (Join-Path $outputRoot 'SELECT-THIS-FOLDER.md')
    Copy-Item -LiteralPath $operationCardSource -Destination (Join-Path $packageRoot 'README-TV11.md')

    $copiedSources = @{}
    foreach ($page in @($plan.pages)) {
        $targetTemplateName = [System.IO.Path]::GetFileName(($page.template_copy -replace '/', '\'))
        $preferredTemplateName = $targetTemplateName -replace '-validation-copy\.psd$', '.psd'
        $sourceTemplateFile = Find-FileByHash $allPsdFiles $page.template_sha256 "template $targetTemplateName" $preferredTemplateName
        Copy-Item -LiteralPath $sourceTemplateFile.FullName -Destination (Join-Path $templateRoot $targetTemplateName)

        $canvasKey = "$($page.expected_canvas[0])x$($page.expected_canvas[1])"
        $referenceFile = Find-FileByHash $allJpgFiles $referenceHashes[$canvasKey] "manual reference $canvasKey"
        Copy-Item -LiteralPath $referenceFile.FullName -Destination (Join-Path $referenceRoot "$($page.page_id)-manual-reference.jpg")

        foreach ($frame in @($page.frames)) {
            $sourceName = [System.IO.Path]::GetFileName(($frame.source.relative_path -replace '/', '\'))
            if ($copiedSources.ContainsKey($sourceName)) { continue }
            $sourceFile = Find-FileByHash $allJpgFiles $frame.source.sha256 "source $sourceName"
            Copy-Item -LiteralPath $sourceFile.FullName -Destination (Join-Path $fixtureSourceRoot $sourceName)
            $copiedSources[$sourceName] = $true
        }
    }

    & (Join-Path $fixtureRoot 'verify_fixture.ps1') -FixtureRoot $fixtureRoot | Write-Output
    & 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' -NoProfile -ExecutionPolicy Bypass -File (Join-Path $fixtureRoot 'verify_fixture.ps1') -FixtureRoot $fixtureRoot | Write-Output

    $forbiddenPluginFiles = @(Get-ChildItem -LiteralPath (Join-Path $packageRoot 'uxp-validation-plugin') -Recurse -File | Where-Object { $_.Extension -in @('.psd', '.psb', '.jpg', '.jpeg', '.png', '.tif', '.tiff') })
    if ($forbiddenPluginFiles.Count -ne 0) { throw 'Plugin directory unexpectedly contains user image or PSD files.' }

    Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal

    $roundTripRoot = Join-Path $stagingRoot 'roundtrip'
    Expand-Archive -LiteralPath $zipPath -DestinationPath $roundTripRoot
    $roundTripPackage = Join-Path $roundTripRoot $packageName
    $roundTripFixture = Join-Path $roundTripPackage 'fixture'
    & (Join-Path $roundTripFixture 'verify_fixture.ps1') -FixtureRoot $roundTripFixture | Write-Output
    & 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' -NoProfile -ExecutionPolicy Bypass -File (Join-Path $roundTripFixture 'verify_fixture.ps1') -FixtureRoot $roundTripFixture | Write-Output
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
