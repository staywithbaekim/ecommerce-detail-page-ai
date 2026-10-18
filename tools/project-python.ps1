$ErrorActionPreference = 'Stop'

# Keep project scripts and worker builds on the verified Python 3.10 environment.
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectPython = Join-Path $projectRoot 'tmp/runtime-repair-20260915/py310-venv2/Scripts/python.exe'
if (-not (Test-Path -LiteralPath $projectPython -PathType Leaf)) {
    throw 'Project Python 3.10.11 environment is missing. Restore the configured environment; do not fall back to another Python.'
}

$version = & $projectPython -E -s -c 'import sys; print(chr(46).join(map(str, sys.version_info[:3])))'
if ($LASTEXITCODE -ne 0 -or $version -ne '3.10.11') {
    throw "Expected project Python 3.10.11; version-check process exit code: $LASTEXITCODE; version received: $([bool]$version)."
}

# Ignore ambient PYTHON* settings and user packages; keep Chinese paths UTF-8.
if ($MyInvocation.ExpectingInput) {
    $input | & $projectPython -E -s -X utf8 @args
} else {
    & $projectPython -E -s -X utf8 @args
}
exit $LASTEXITCODE
