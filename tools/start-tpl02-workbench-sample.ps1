[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$sample = Join-Path $repo 'tmp/tpl02-ui-20261001'
$data = Join-Path $sample 'data-root'
$exe = Join-Path $repo 'src/Desktop.App/bin/Tpl02UI/Debug/net10.0-windows/win-x64/Desktop.App.exe'

# This launcher never creates, seeds, repairs or falls back to a different database.
# The local sample is ignored by Git and intentionally not shipped with the code.
foreach ($file in @($exe, (Join-Path $data 'state/desktop-v1.db'),
        (Join-Path $sample 'sample-source-protection.json'), (Join-Path $sample 'sample-preconditions.md'))) {
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Missing verified sample or Debug build: $file. Do not start the executable without --data-root."
    }
}
for ($directory = [IO.DirectoryInfo]::new($data); $null -ne $directory; $directory = $directory.Parent) {
    if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The sample path must not contain a junction or symbolic link.'
    }
}
Write-Output "Executable: $exe"
Write-Output "Isolated data root: $data"
Write-Output 'Open 模板 -> TPL-02 实交互样板 -> 编辑模板组. No AI request or rendering is triggered by this launcher.'
if ($CheckOnly) { return }
# A visible interactive application is the requested deliverable, not a helper.
Start-Process -FilePath $exe -ArgumentList ('--data-root="' + $data + '"') -WorkingDirectory $repo
