param(
    [Parameter(Mandatory = $true)]
    [string]$BaselinePath,

    [Parameter(Mandatory = $true)]
    [string]$ReplacementPath,

    [Parameter(Mandatory = $false)]
    [string]$EditedPath,

    [Parameter(Mandatory = $false)]
    [switch]$BaselineOnly
)

$ErrorActionPreference = 'Stop'

$runtimeRoot = Join-Path $env:LOCALAPPDATA 'CodexSkillRuntimes/ecommerce-psd-composer'
$pythonDeps = Join-Path $runtimeRoot 'pydeps'
$scriptPath = Join-Path $PSScriptRoot 'verify_returned_psd.py'
$pythonCandidates = @(
    'C:\Program Files\Python310\python.exe',
    (Join-Path $env:USERPROFILE '.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe')
)
$python = $pythonCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1

if (-not $python) { throw 'Python 3.10 runtime was not found.' }
if (-not (Test-Path -LiteralPath $pythonDeps -PathType Container)) { throw 'PSD parser dependencies were not found.' }
if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) { throw 'Returned-PSD verifier script was not found.' }

$arguments = @(
    $scriptPath,
    '--baseline', (Resolve-Path -LiteralPath $BaselinePath).Path,
    '--replacement', (Resolve-Path -LiteralPath $ReplacementPath).Path
)
if ($BaselineOnly) {
    $arguments += '--baseline-only'
} else {
    if ([string]::IsNullOrWhiteSpace($EditedPath)) { throw 'EditedPath is required unless BaselineOnly is used.' }
    $arguments += @('--edited', (Resolve-Path -LiteralPath $EditedPath).Path)
}

$previousPythonPath = $env:PYTHONPATH
try {
    $env:PYTHONPATH = $pythonDeps
    & $python @arguments
    exit $LASTEXITCODE
} finally {
    $env:PYTHONPATH = $previousPythonPath
}
