[CmdletBinding()]
param(
    [ValidateSet('general', 'tpl02', 'stage', 'validation', 'architecture', 'render')]
    [string]$Route = 'general',
    [string]$Query,
    [switch]$IncludeCurrentContext,
    [ValidateRange(10, 160)]
    [int]$MaxLines = 40
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
    if ($matches.Count -ne 1) {
        throw "Expected exactly one document '$Name'; found $($matches.Count)."
    }
    return $matches[0].FullName
}

function Get-PrefixDoc([string]$Prefix) {
    $matches = @(Get-ChildItem -LiteralPath $docsRoot -File -Recurse | Where-Object {
        $_.Name.StartsWith($Prefix, [StringComparison]::OrdinalIgnoreCase)
    })
    if ($matches.Count -ne 1) {
        throw "Expected exactly one document with prefix '$Prefix'; found $($matches.Count)."
    }
    return $matches[0].FullName
}

function Get-RootDoc([string]$Name) {
    $matches = @(Get-ChildItem -LiteralPath $root -File | Where-Object {
        [string]::Equals($_.Name, $Name, [StringComparison]::OrdinalIgnoreCase)
    })
    if ($matches.Count -ne 1) {
        throw "Expected exactly one root document '$Name'; found $($matches.Count)."
    }
    return $matches[0].FullName
}

function Get-RelativePath([string]$Path) {
    return $Path.Substring($root.Length).TrimStart([char[]]@([char]92, [char]47))
}

function Write-BoundedFile([string]$Path, [int]$Count) {
    Write-Output ("--- {0} (first {1} lines) ---" -f (Get-RelativePath $Path), $Count)
    Get-Content -LiteralPath $Path -Encoding UTF8 | Select-Object -First $Count
}

function Write-Matches([string]$Path, [string]$Needle, [int]$Radius = 1, [int]$MaxMatches = 6) {
    $lines = @(Get-Content -LiteralPath $Path -Encoding UTF8)
    $hits = @()
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index].IndexOf($Needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $hits += $index
        }
    }
    if ($hits.Count -eq 0) {
        Write-Output ("--- no matches in {0}: {1} ---" -f (Get-RelativePath $Path), $Needle)
        return
    }
    Write-Output ("--- {0} matches: {1} ---" -f (Get-RelativePath $Path), $Needle)
    $last = -1
    foreach ($hit in $hits | Select-Object -First $MaxMatches) {
        $start = [Math]::Max(0, $hit - $Radius)
        $end = [Math]::Min($lines.Count - 1, $hit + $Radius)
        if ($start -le $last) {
            $start = $last + 1
        }
        for ($index = $start; $index -le $end; $index++) {
            Write-Output ("{0}: {1}" -f ($index + 1), $lines[$index])
        }
        $last = $end
        Write-Output ''
    }
}

$contextName = (From-Hex '5F53,524D,4EFB,52A1,4E0A,4E0B,6587') + '.md'
$tplHandoffName = 'TPL-02-' + (From-Hex '5F53,524D,4EA4,63A5') + '.md'
$tplBriefPrefix = 'TPL-02-' + (From-Hex '6A21,677F,89C4,5219,5DE5,4F5C,53F0,4E0E,81EA,7136,8BED,8A00,89C4,5219,65B9,6848')
$stageName = (From-Hex '5F53,524D,9879,76EE,72B6,6001,6458,8981') + '.md'
$acceptanceName = (From-Hex '5F53,524D,9A8C,6536,7D22,5F15') + '.md'
$matrixName = (From-Hex '9A8C,6536,77E9,9635') + '.md'
$overviewName = (From-Hex '9879,76EE,603B,89C8') + '.md'
$architectureDirectoryName = From-Hex '67B6,6784,51B3,7B56'

Write-Output ("Project root: {0}" -f $root)
Write-Output ("Route: {0}" -f $Route)

if ($IncludeCurrentContext) {
    Write-BoundedFile (Get-ExactDoc $contextName) 40
} else {
    Write-Output 'L0 context omitted by default; use -IncludeCurrentContext only when it is not already in the conversation.'
}

switch ($Route) {
    'general' {
        Write-Output 'Use a task-specific route before opening long documents.'
    }
    'tpl02' {
        Write-BoundedFile (Get-ExactDoc $tplHandoffName) ([Math]::Min($MaxLines, 40))
        if ($Query) {
            Write-Matches (Get-PrefixDoc $tplBriefPrefix) $Query 1 6
        } else {
            Write-Output 'Pass -Query <keyword> to read bounded excerpts from the detailed TPL-02 brief.'
        }
    }
    'stage' {
        Write-BoundedFile (Get-ExactDoc $stageName) ([Math]::Min($MaxLines, 40))
        Write-BoundedFile (Get-RootDoc $overviewName) 25
    }
    'validation' {
        Write-BoundedFile (Get-ExactDoc $acceptanceName) ([Math]::Min($MaxLines, 40))
        if ($Query) {
            Write-Matches (Get-ExactDoc $matrixName) $Query 1 6
        } else {
            Write-Output 'Pass -Query <acceptance ID or keyword> to read bounded matrix windows.'
        }
    }
    'architecture' {
        if (-not $Query) {
            Write-Output 'Pass -Query <keyword>; ADRs are not enumerated by the default route.'
            return
        }
        $adrRoot = Join-Path $docsRoot $architectureDirectoryName
        $adrFiles = @(Get-ChildItem -LiteralPath $adrRoot -File)
        $filenameMatches = @($adrFiles | Where-Object {
            $_.Name.IndexOf($Query, [StringComparison]::OrdinalIgnoreCase) -ge 0
        } | Select-Object -First 12)
        if ($filenameMatches.Count -eq 0) {
            Write-Output ("No ADR filename matches: {0}" -f $Query)
        } else {
            $filenameMatches | ForEach-Object { Write-Output (Get-RelativePath $_.FullName) }
        }
        $contentMatches = @($adrFiles | Where-Object {
            Select-String -LiteralPath $_.FullName -Pattern $Query -SimpleMatch -Quiet
        } | Select-Object -First 12)
        foreach ($match in $contentMatches) {
            Write-Output ("content-match: {0}" -f (Get-RelativePath $match.FullName))
        }
    }
    'render' {
        Write-BoundedFile (Get-ExactDoc $stageName) ([Math]::Min($MaxLines, 40))
        Write-Output 'Read the RENDER task and handoff contract only when the requested change affects them.'
        $renderTask = @(Get-ChildItem -LiteralPath (Join-Path $docsRoot (From-Hex '4EFB,52A1')) -File | Where-Object { $_.Name -like 'RENDER-01*Photoshop*' } | Select-Object -First 1)
        $renderContract = @(Get-ChildItem -LiteralPath (Join-Path $docsRoot (From-Hex '4EFB,52A1')) -File | Where-Object { $_.Name.StartsWith('RENDER-01-SIZE-01', [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1)
        if ($renderTask.Count -eq 1) { Write-Output ("Task: {0}" -f (Get-RelativePath $renderTask[0].FullName)) }
        if ($renderContract.Count -eq 1) { Write-Output ("Contract: {0}" -f (Get-RelativePath $renderContract[0].FullName)) }
    }
}
