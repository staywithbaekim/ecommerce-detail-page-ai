param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tmpParent = Join-Path $root 'tmp'
$testRoot = Join-Path $tmpParent 'tv13-update-0.4.7.1-roundtrip'
$baseZip = Join-Path $root 'uxp-validation-tv11-whole-product-0.4.6.zip'
$updateZip = Join-Path $root 'uxp-validation-tv13-update-0.4.7.1.zip'
foreach ($path in @($baseZip, $updateZip)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing test input: $path" }
}
if (Test-Path -LiteralPath $testRoot) { throw "Test root already exists: $testRoot" }
$resolvedParent = [System.IO.Path]::GetFullPath($tmpParent).TrimEnd('\') + '\'
$resolvedTest = [System.IO.Path]::GetFullPath($testRoot)
if (-not $resolvedTest.StartsWith($resolvedParent, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Test root escaped tmp.' }

function Get-ProtectedSnapshot {
    param([string]$FixtureRoot)

    $items = @()
    foreach ($directoryName in @('template', 'source', 'reference')) {
        $directoryPath = Join-Path $FixtureRoot $directoryName
        $resolvedDirectory = [System.IO.Path]::GetFullPath($directoryPath).TrimEnd('\') + '\'
        foreach ($file in @(Get-ChildItem -LiteralPath $directoryPath -Recurse -File | Sort-Object FullName)) {
            $resolvedFile = [System.IO.Path]::GetFullPath($file.FullName)
            $items += [ordered]@{
                path = ($directoryName + '/' + $resolvedFile.Substring($resolvedDirectory.Length).Replace('\', '/'))
                length = $file.Length
                sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash
            }
        }
    }
    return ($items | ConvertTo-Json -Depth 5 -Compress)
}

try {
    $baseExtract = Join-Path $testRoot 'base'
    $updateExtract = Join-Path $testRoot 'update'
    Expand-Archive -LiteralPath $baseZip -DestinationPath $baseExtract
    Expand-Archive -LiteralPath $updateZip -DestinationPath $updateExtract
    $targetRoot = Join-Path $baseExtract 'uxp-validation-tv11-whole-product-0.4.6'
    $fixture = Join-Path $targetRoot 'fixture'
    $updater = Join-Path $updateExtract 'uxp-validation-tv13-update-0.4.7.1/apply_update_0_4_7_1.ps1'
    $protectedBefore = Get-ProtectedSnapshot -FixtureRoot $fixture

    & $updater -TargetRoot $targetRoot | Write-Output
    if (Test-Path -LiteralPath (Join-Path $baseExtract 'uxp-validation-tv11-whole-product-0.4.7')) { throw 'Updater unexpectedly renamed or moved the package root.' }
    $manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $targetRoot 'uxp-validation-plugin/manifest.json') | ConvertFrom-Json
    if ($manifest.version -ne '0.4.7') { throw 'Clean update did not install plugin 0.4.7.' }
    & (Join-Path $fixture 'verify_tv13.ps1') -FixtureRoot $fixture | Out-Null

    & $updater -TargetRoot $targetRoot | Out-Null
    if ((Get-ProtectedSnapshot -FixtureRoot $fixture) -cne $protectedBefore) { throw 'Idempotent update changed protected fixture files.' }

    $partialSibling = Join-Path $baseExtract 'uxp-validation-tv11-whole-product-0.4.7-partial'
    New-Item -ItemType Directory -Path $partialSibling | Out-Null
    Move-Item -LiteralPath (Join-Path $targetRoot 'uxp-validation-plugin') -Destination $partialSibling
    & $updater -TargetRoot $targetRoot | Out-Null
    $recoveredManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $targetRoot 'uxp-validation-plugin/manifest.json') | ConvertFrom-Json
    if ($recoveredManifest.version -ne '0.4.7') { throw 'Half-update recovery did not restore plugin 0.4.7.' }
    if ((Get-ProtectedSnapshot -FixtureRoot $fixture) -cne $protectedBefore) { throw 'Half-update recovery changed protected fixture files.' }

    $sentinel = Join-Path $fixture 'tv13-recovery-output/DO-NOT-DELETE.txt'
    Set-Content -LiteralPath $sentinel -Value 'sentinel' -Encoding ASCII
    $refusedNonEmptyOutput = $false
    try {
        & $updater -TargetRoot $targetRoot | Out-Null
    } catch {
        if ($_.Exception.Message -match 'output directory is not empty') { $refusedNonEmptyOutput = $true }
    }
    if (-not $refusedNonEmptyOutput -or -not (Test-Path -LiteralPath $sentinel -PathType Leaf)) {
        throw 'Updater did not preserve and reject a non-empty TV-13 output directory.'
    }
    Remove-Item -LiteralPath $sentinel -Force

    $updaterText = Get-Content -Raw -Encoding UTF8 -LiteralPath $updater
    if ($updaterText -match '(?im)^\s*(Move-Item|Rename-Item)\b') { throw 'Updater still moves or renames package content.' }
    Write-Output 'CLEAN_UPDATE=PASS'
    Write-Output 'IDEMPOTENT_RERUN=PASS'
    Write-Output 'HALF_UPDATE_RECOVERY=PASS'
    Write-Output 'NONEMPTY_OUTPUT_PRESERVED=PASS'
    Write-Output 'PROTECTED_FIXTURE_UNCHANGED=PASS'
    Write-Output "UPDATE_SHA256=$((Get-FileHash -Algorithm SHA256 -LiteralPath $updateZip).Hash)"
    Write-Output "UPDATE_BYTES=$((Get-Item -LiteralPath $updateZip).Length)"
} finally {
    if (Test-Path -LiteralPath $resolvedTest) { Remove-Item -LiteralPath $resolvedTest -Recurse -Force }
}
