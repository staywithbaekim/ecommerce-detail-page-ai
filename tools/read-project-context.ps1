[CmdletBinding()]
param(
    [ValidateSet('general', 'tpl02', 'stage', 'validation', 'architecture', 'render')]
    [string]$Route = 'general',
    [string]$Query,
    [ValidateRange(20, 240)]
    [int]$MaxLines = 80
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Resolve-ProjectPath([string]$RelativePath) {
    $path = Join-Path $root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Project context file not found: $RelativePath"
    }
    return $path
}

function Write-BoundedFile([string]$RelativePath, [int]$Count = $MaxLines) {
    $path = Resolve-ProjectPath $RelativePath
    Write-Output ("--- $RelativePath (first $Count lines) ---")
    Get-Content -LiteralPath $path -Encoding UTF8 | Select-Object -First $Count
}

function Write-Matches([string]$RelativePath, [string]$Needle, [int]$Radius = 1, [int]$MaxMatches = 8) {
    $path = Resolve-ProjectPath $RelativePath
    $lines = @(Get-Content -LiteralPath $path -Encoding UTF8)
    $matches = @()
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index].IndexOf($Needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $matches += $index
        }
    }
    if ($matches.Count -eq 0) {
        Write-Output ("--- no matches: $RelativePath :: $Needle ---")
        return
    }
    Write-Output ("--- $RelativePath matches: $Needle ---")
    $last = -1
    foreach ($match in $matches | Select-Object -First $MaxMatches) {
        $start = [Math]::Max(0, $match - $Radius)
        $end = [Math]::Min($lines.Count - 1, $match + $Radius)
        if ($start -le $last) { $start = $last + 1 }
        for ($index = $start; $index -le $end; $index++) {
            Write-Output ("{0}: {1}" -f ($index + 1), $lines[$index])
        }
        $last = $end
        Write-Output ''
    }
}

Write-Output ("Project root: $root")
Write-Output ("Route: $Route")
Write-BoundedFile 'docs\当前任务上下文.md' ([Math]::Min($MaxLines, 80))

switch ($Route) {
    'general' {
        Write-Output 'Use the task-specific route before opening long documents.'
    }
    'tpl02' {
        Write-BoundedFile 'docs\任务\TPL-02-当前交接.md' ([Math]::Min($MaxLines, 80))
        if ($Query) {
            Write-Matches 'docs\任务\TPL-02-模板规则工作台与自然语言规则方案-20260928.md' $Query -Radius 1 -MaxMatches 8
        }
        else {
            Write-Output 'Pass -Query <keyword> to read bounded excerpts from the detailed TPL-02 brief; it is not opened by the default route.'
        }
    }
    'stage' {
        Write-BoundedFile 'docs\当前项目状态摘要.md' ([Math]::Min($MaxLines, 80))
        Write-BoundedFile '项目总览.md' 35
    }
    'validation' {
        Write-BoundedFile 'docs\当前验收索引.md' ([Math]::Min($MaxLines, 80))
        if ($Query) { Write-Matches 'docs\验收矩阵.md' $Query -Radius 1 -MaxMatches 8 }
        else { Write-Output 'Pass -Query <验收ID或关键词> to read bounded evidence windows from the full matrix.' }
    }
    'architecture' {
        $adrRoot = Join-Path $root 'docs\架构决策'
        if (-not $Query) {
            Write-Output 'Pass -Query <keyword> to locate relevant ADR filenames and content matches; no ADRs are enumerated by the default route.'
            return
        }
        Write-Output '--- matching ADR filenames ---'
        $files = Get-ChildItem -LiteralPath $adrRoot -File | Where-Object { -not $Query -or $_.Name.IndexOf($Query, [StringComparison]::OrdinalIgnoreCase) -ge 0 }
        $files | Select-Object -First 20 -ExpandProperty Name
        if ($Query) {
            Get-ChildItem -LiteralPath $adrRoot -File | Where-Object { $_.Name.IndexOf($Query, [StringComparison]::OrdinalIgnoreCase) -ge 0 } | ForEach-Object {
                $hit = Select-String -LiteralPath $_.FullName -Pattern $Query -SimpleMatch -Quiet
                if ($hit) { Write-Output ("content-match: " + $_.Name) }
            }
        }
    }
    'render' {
        Write-BoundedFile 'docs\当前项目状态摘要.md' 80
        Write-Output 'Read the RENDER task and handoff contract only when the requested change affects them; locate headings first and read bounded excerpts.'
        Write-Output 'Task: docs\任务\RENDER-01正式Photoshop-UXP渲染适配器纵向实现任务书.md'
        Write-Output 'Contract: docs\任务\RENDER-01-SIZE-01渲染交接合同.md'
    }
}
