$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $root 'uxp-validation-plugin/manifest.json'
$planPath = Join-Path $root 'uxp-validation-plugin/render-plans/sample-render-plan.json'

$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
if ($manifest.manifestVersion -ne 5) { throw 'manifestVersion must be 5' }
if (-not $manifest.id -or -not $manifest.main) { throw 'manifest id/main is required' }
if ($manifest.host.app -ne 'PS' -or $manifest.host.minVersion -ne '23.3.0') { throw 'Photoshop host baseline is incorrect' }
if ($manifest.entrypoints.type -notcontains 'panel') { throw 'a panel entrypoint is required' }
if ($manifest.requiredPermissions.localFileSystem -ne 'fullAccess') { throw 'fullAccess local file permission is required for evidence files' }

$plan = Get-Content -Raw -LiteralPath $planPath | ConvertFrom-Json
if ($plan.schema -ne 'render-plan/v1') { throw 'RenderPlan schema is incorrect' }
if (-not $plan.input_policy.source_files_read_only -or -not $plan.input_policy.template_copies_only) { throw 'source safety policy is missing' }
if ($plan.pages.Count -ne 2) { throw 'sample plan must include the single and complex pages' }
foreach ($page in $plan.pages) {
    if (-not $page.template_copy -or $page.template_copy -notlike 'sample-copies/*') { throw "page $($page.page_id) does not use a template copy" }
    foreach ($frame in $page.frames) {
        if (-not $frame.target.layer_path -or -not $frame.source.sha256 -or $frame.source.sha256.Length -ne 64) { throw "frame $($frame.frame_id) lacks binding evidence" }
        if ($frame.fit -notin @('cover','contain')) { throw "frame $($frame.frame_id) has unsupported fit" }
    }
}

Write-Output 'UXP prototype preflight: PASS'
Write-Output "Manifest: $manifestPath"
Write-Output "RenderPlan pages: $($plan.pages.Count)"
Write-Output "RenderPlan frames: $(($plan.pages | ForEach-Object { $_.frames.Count } | Measure-Object -Sum).Sum)"
