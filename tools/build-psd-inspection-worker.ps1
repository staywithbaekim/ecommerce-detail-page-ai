[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputDirectory,
    [Parameter(Mandatory = $false)]
    [string]$BuildDirectory
)

$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$workerRoot = Join-Path $repoRoot 'src/PsdInspection.Worker'
$specPath = Join-Path $workerRoot 'psd-inspection-worker.spec'
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$buildRoot = if ([string]::IsNullOrWhiteSpace($BuildDirectory)) {
    Join-Path (Split-Path -Parent $outputRoot) 'obj/psd-inspection-worker'
} else {
    [IO.Path]::GetFullPath($BuildDirectory)
}
$distributionRoot = Join-Path $buildRoot 'dist'
$workRoot = Join-Path $buildRoot 'work'
$cachedWorker = Join-Path $distributionRoot 'psd-inspection-worker'
$cachedExecutable = Join-Path $cachedWorker 'psd-inspection-worker.exe'
$cacheManifestPath = Join-Path $buildRoot 'worker-build-manifest.json'
$targetWorker = Join-Path $outputRoot 'psd-inspection-worker'
$targetExecutable = Join-Path $targetWorker 'psd-inspection-worker.exe'
$targetManifestPath = Join-Path $targetWorker 'worker-build-manifest.json'
$inputs = @(
    (Join-Path $workerRoot 'worker.py'),
    $specPath,
    (Join-Path $workerRoot 'requirements.lock'),
    (Join-Path $workerRoot 'build-requirements.lock')
)
$pythonRunner = Join-Path $PSScriptRoot 'project-python.ps1'

function Get-FileSha256([string]$Path) {
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString(
            $sha256.ComputeHash([IO.File]::ReadAllBytes($Path))).Replace('-', '')
    } finally {
        $sha256.Dispose()
    }
}

foreach ($path in @($specPath, $pythonRunner) + $inputs) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "PSD inspection worker build input is missing: $path"
    }
}

foreach ($directory in @($outputRoot, $buildRoot, $distributionRoot, $workRoot)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

$outputRoot = (Resolve-Path -LiteralPath $outputRoot).Path.TrimEnd('\')
$buildRoot = (Resolve-Path -LiteralPath $buildRoot).Path.TrimEnd('\')
$cachedWorker = Join-Path $buildRoot 'dist/psd-inspection-worker'
$cachedExecutable = Join-Path $cachedWorker 'psd-inspection-worker.exe'
$cacheManifestPath = Join-Path $buildRoot 'worker-build-manifest.json'
$targetWorker = Join-Path $outputRoot 'psd-inspection-worker'
$targetExecutable = Join-Path $targetWorker 'psd-inspection-worker.exe'
$targetManifestPath = Join-Path $targetWorker 'worker-build-manifest.json'

$inputHashes = [ordered]@{}
foreach ($path in $inputs) {
    $inputHashes[[IO.Path]::GetFileName($path)] = Get-FileSha256 $path
}
$manifestJson = ConvertTo-Json -InputObject $inputHashes -Compress
$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $manifestHash = [BitConverter]::ToString($sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($manifestJson))).Replace('-', '')
} finally {
    $sha256.Dispose()
}

$cachedWorkerIsCurrent = $false
$cachedExecutableExists = Test-Path -LiteralPath $cachedExecutable -PathType Leaf
$cachedInternalExists = Test-Path -LiteralPath (Join-Path $cachedWorker '_internal') -PathType Container
$cachedManifestExists = Test-Path -LiteralPath $cacheManifestPath -PathType Leaf
if ($cachedExecutableExists -and $cachedInternalExists -and $cachedManifestExists) {
    $cachedWorkerIsCurrent = (Get-Content -LiteralPath $cacheManifestPath -Raw -Encoding UTF8).Trim() -eq $manifestHash
}

if (-not $cachedWorkerIsCurrent) {
    $distributionPrefix = $buildRoot.TrimEnd('\') + '\'
    $resolvedCachedWorker = [IO.Path]::GetFullPath($cachedWorker)
    $cachedWorkerIsIsolated = $resolvedCachedWorker.StartsWith($distributionPrefix, [StringComparison]::OrdinalIgnoreCase)
    if (-not $cachedWorkerIsIsolated -or [IO.Path]::GetFileName($resolvedCachedWorker) -ne 'psd-inspection-worker') {
        throw 'Refusing to clean a worker build directory outside its isolated distribution root.'
    }
    if (Test-Path -LiteralPath $resolvedCachedWorker) {
        Remove-Item -LiteralPath $resolvedCachedWorker -Recurse -Force
    }

    & $pythonRunner -m PyInstaller --noconfirm `
        --distpath $distributionRoot `
        --workpath $workRoot `
        $specPath
    if ($LASTEXITCODE -ne 0) {
        throw "PyInstaller failed with exit code $LASTEXITCODE."
    }
    $cachedExecutableExists = Test-Path -LiteralPath $cachedExecutable -PathType Leaf
    $cachedInternalExists = Test-Path -LiteralPath (Join-Path $cachedWorker '_internal') -PathType Container
    if (-not $cachedExecutableExists -or -not $cachedInternalExists) {
        throw 'PyInstaller did not produce a complete onedir worker.'
    }

    Set-Content -LiteralPath $cacheManifestPath -Value $manifestHash -Encoding ASCII
}

$targetHasCurrentWorker = $false
$targetExecutableExists = Test-Path -LiteralPath $targetExecutable -PathType Leaf
$targetInternalExists = Test-Path -LiteralPath (Join-Path $targetWorker '_internal') -PathType Container
$targetManifestExists = Test-Path -LiteralPath $targetManifestPath -PathType Leaf
if ($targetExecutableExists -and $targetInternalExists -and $targetManifestExists) {
    $targetHasCurrentWorker = (Get-Content -LiteralPath $targetManifestPath -Raw -Encoding UTF8).Trim() -eq $manifestHash
}

if (-not $targetHasCurrentWorker) {
    $outputPrefix = $outputRoot.TrimEnd('\') + '\'
    $resolvedTargetWorker = [IO.Path]::GetFullPath($targetWorker)
    $targetWorkerIsOwned = $resolvedTargetWorker.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)
    if (-not $targetWorkerIsOwned -or [IO.Path]::GetFileName($resolvedTargetWorker) -ne 'psd-inspection-worker') {
        throw 'Refusing to replace a worker directory outside the requested app output directory.'
    }
    if (Test-Path -LiteralPath $resolvedTargetWorker) {
        Remove-Item -LiteralPath $resolvedTargetWorker -Recurse -Force
    }

    Copy-Item -LiteralPath $cachedWorker -Destination $resolvedTargetWorker -Recurse
    Set-Content -LiteralPath (Join-Path $resolvedTargetWorker 'worker-build-manifest.json') `
        -Value $manifestHash -Encoding ASCII
}

Write-Output "PSD inspection worker ready: $targetExecutable"
