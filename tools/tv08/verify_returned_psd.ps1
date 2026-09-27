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

$scriptPath = Join-Path $PSScriptRoot 'verify_returned_psd.py'
$projectPythonRunner = Join-Path (Split-Path -Parent $PSScriptRoot) 'project-python.ps1'

if (-not (Test-Path -LiteralPath $projectPythonRunner -PathType Leaf)) { throw 'Project Python entry point was not found.' }
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

& $projectPythonRunner @arguments
exit $LASTEXITCODE
