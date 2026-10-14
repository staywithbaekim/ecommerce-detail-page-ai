[CmdletBinding()]
param(
    [ValidateSet('general', 'tpl02', 'stage', 'validation', 'architecture', 'render')]
    [string]$Route = 'general',
    [switch]$IncludeCurrentContext,
    [ValidateRange(10, 160)]
    [int]$MaxLines = 40,
    [switch]$IncludeLargestDocs
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$docsRoot = Join-Path $root 'docs'

function From-Hex([string]$Value) {
    $result = ''
    foreach ($hex in $Value.Split(',')) {
        $result += [char][Convert]::ToInt32($hex, 16)
    }
    return $result
}

function Get-ExactDoc([string]$Name) {
    $matches = @(Get-ChildItem -LiteralPath $docsRoot -File -Recurse | Where-Object {
        [string]::Equals($_.Name, $Name, [StringComparison]::OrdinalIgnoreCase)
    })
    if ($matches.Count -ne 1) { throw "Expected exactly one document '$Name'; found $($matches.Count)." }
    return $matches[0].FullName
}

function Get-Metrics([string]$Path, [string]$Kind) {
    $text = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
    $bytes = [System.Text.Encoding]::UTF8.GetByteCount($text)
    [pscustomobject]@{
        Kind = $Kind
        Path = $Path.Substring($root.Length).TrimStart([char[]]@([char]92, [char]47))
        Bytes = $bytes
        Lines = @($text -split "`r?`n").Count
        ApproxTokens = [Math]::Ceiling($bytes / 4)
    }
}

function Get-InstructionFiles([string]$Directory) {
    Get-ChildItem -LiteralPath $Directory -File | Where-Object {
        $_.Name -in @('AGENTS.md', 'AGENTS.override.md')
    }
    Get-ChildItem -LiteralPath $Directory -Directory | Where-Object {
        $_.Name -notin @('.git', 'tmp', 'bin', 'obj', 'node_modules', 'packages')
    } | ForEach-Object {
        Get-InstructionFiles $_.FullName
    }
}

$contextName = (From-Hex '5F53,524D,4EFB,52A1,4E0A,4E0B,6587') + '.md'
$l0 = @(
    Get-Metrics (Join-Path $root 'AGENTS.md') 'L0'
    Get-Metrics (Get-ExactDoc $contextName) 'L0'
)
$instructionFiles = @(Get-InstructionFiles $root)

$router = Join-Path $root 'tools/read-project-context.ps1'
if ($IncludeCurrentContext) {
    $routeText = (& $router -Route $Route -MaxLines $MaxLines -IncludeCurrentContext 2>&1 | Out-String)
} else {
    $routeText = (& $router -Route $Route -MaxLines $MaxLines 2>&1 | Out-String)
}
if ($routeText -match 'read-project-context\.ps1:\d+|Document not found|Expected exactly|ParserError') {
    throw 'Context router emitted an error; audit aborted.'
}
$routeBytes = [System.Text.Encoding]::UTF8.GetByteCount($routeText)
$routeMetric = [pscustomobject]@{
    Kind = 'Route output'
    Path = $Route
    Bytes = $routeBytes
    Lines = @($routeText -split "`r?`n").Count
    ApproxTokens = [Math]::Ceiling($routeBytes / 4)
}

Write-Output 'Context cost audit (repository-side estimate; not model billing telemetry)'
Write-Output 'ApproxTokens uses UTF-8 bytes / 4 and is only a size heuristic.'
Write-Output ''
Write-Output '--- resident L0 ---'
$l0 | Format-Table -AutoSize
Write-Output '--- discovered instruction files ---'
$instructionFiles | ForEach-Object {
    Get-Metrics $_.FullName 'Instruction candidate'
} | Format-Table -AutoSize
Write-Output '--- selected route output ---'
$routeMetric | Format-Table -AutoSize

if ($IncludeLargestDocs) {
    Write-Output '--- largest documentation files ---'
    Get-ChildItem -LiteralPath $docsRoot -File -Recurse |
        Sort-Object Length -Descending |
        Select-Object -First 15 @{Name='Path';Expression={$_.FullName.Substring($root.Length).TrimStart([char[]]@([char]92, [char]47))}}, Length |
        Format-Table -AutoSize
}
