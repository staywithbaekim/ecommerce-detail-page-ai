param(
    [Parameter(Mandatory = $false)]
    [string]$FixtureRoot,

    [Parameter(Mandatory = $false)]
    [switch]$RequireRecovery
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($FixtureRoot)) { $FixtureRoot = $PSScriptRoot }
$resolvedRoot = (Resolve-Path -LiteralPath $FixtureRoot).Path
$baseVerifier = Join-Path $resolvedRoot 'verify_fixture.ps1'
$failurePlanPath = Join-Path $resolvedRoot 'tv13-controlled-failure-render-plan.json'
$normalPlanPath = Join-Path $resolvedRoot 'whole-product-a6015-render-plan.json'
$failureOutput = Join-Path $resolvedRoot 'tv13-failure-output'
$recoveryOutput = Join-Path $resolvedRoot 'tv13-recovery-output'

foreach ($path in @($baseVerifier, $failurePlanPath, $normalPlanPath, $failureOutput, $recoveryOutput)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing TV-13 fixture path: $path" }
}

& $baseVerifier -FixtureRoot $resolvedRoot | Out-Null

$failurePlan = Get-Content -Raw -Encoding UTF8 -LiteralPath $failurePlanPath | ConvertFrom-Json
$normalPlan = Get-Content -Raw -Encoding UTF8 -LiteralPath $normalPlanPath | ConvertFrom-Json
$fault = $failurePlan.validation.controlled_failure
$expectedNextPageId = $normalPlan.pages[2].page_id
$expectedNextFrameId = $normalPlan.pages[2].frames[0].frame_id
if ($failurePlan.job_id -ne 'uxp-a6015-tv13-failure-001' -or $failurePlan.validation.mode -ne 'whole_product_copy') { throw 'TV-13 failure-plan identity is incorrect.' }
if ($fault.checkpoint -ne 'after_completed_pages' -or $fault.completed_page_count -ne 2 -or $fault.error_code -ne 'TV13_CONTROLLED_FAILURE') { throw 'TV-13 controlled checkpoint is incorrect.' }
if ($fault.next_page_id -ne $expectedNextPageId -or $fault.next_frame_id -ne $expectedNextFrameId) { throw 'TV-13 failure location is incorrect.' }
if (($failurePlan.pages | ConvertTo-Json -Depth 20 -Compress) -cne ($normalPlan.pages | ConvertTo-Json -Depth 20 -Compress)) { throw 'TV-13 failure plan changed the approved page or frame plan.' }

$failureFiles = @(Get-ChildItem -LiteralPath $failureOutput -File)
if ($failureFiles.Count -ne 0) { throw "TV-13 failure output is not clean: $($failureFiles.Name -join ', ')" }

$result = [ordered]@{
    passed = $true
    fixture_root = $resolvedRoot
    plugin_version = '0.4.7'
    controlled_failure = [ordered]@{
        checkpoint = $fault.checkpoint
        completed_page_count = $fault.completed_page_count
        next_page_id = $fault.next_page_id
        next_frame_id = $fault.next_frame_id
        failure_output_clean = $true
    }
    recovery_required = [bool]$RequireRecovery
}

if ($RequireRecovery) {
    $jpgFiles = @(Get-ChildItem -LiteralPath $recoveryOutput -File -Filter '*.jpg')
    $psdFiles = @(Get-ChildItem -LiteralPath $recoveryOutput -File -Filter '*.psd')
    $evidenceFiles = @(Get-ChildItem -LiteralPath $recoveryOutput -File -Filter '*-whole-product-validation-evidence.json')
    if ($jpgFiles.Count -ne 6 -or $psdFiles.Count -ne 0 -or $evidenceFiles.Count -ne 1) { throw 'TV-13 recovery output must contain six JPG files, no PSD, and one success evidence JSON.' }

    $evidence = Get-Content -Raw -Encoding UTF8 -LiteralPath $evidenceFiles[0].FullName | ConvertFrom-Json
    if (-not $evidence.ok -or $evidence.plugin_version -ne '0.4.7' -or $evidence.validation_mode -ne 'whole_product_copy') { throw 'TV-13 recovery success evidence is incorrect.' }
    if ($evidence.page_count -ne 6 -or $evidence.frame_count -ne 11 -or $evidence.output.jpg_count -ne 6) { throw 'TV-13 recovery did not complete the full six-page plan.' }
    if (-not $evidence.output.all_fixed_structures_unchanged -or -not $evidence.output.all_jpg_profiles_verified -or $evidence.output.source_templates_saved) { throw 'TV-13 recovery output safety checks failed.' }

    $prior = $evidence.prior_controlled_failure
    if ($null -eq $prior -or $prior.ok -ne $false -or $prior.error.code -ne 'TV13_CONTROLLED_FAILURE') { throw 'Recovery evidence does not contain the prior controlled failure.' }
    $details = $prior.error.details
    if ($details.active_page_id -ne $expectedNextPageId -or $details.next_frame_id -ne $expectedNextFrameId) { throw 'Controlled failure page or frame evidence is incorrect.' }
    if (-not $details.failed_after_partial_output -or -not $details.partial_outputs_removed -or -not $details.output_cleanup.confirmed -or $details.source_templates_saved) { throw 'Controlled failure cleanup or input-safety evidence is incomplete.' }
    $completed = @($details.completed_page_ids_before_failure)
    $expectedCompleted = @($normalPlan.pages[0].page_id, $normalPlan.pages[1].page_id)
    if (($completed -join '|') -cne ($expectedCompleted -join '|')) { throw 'Controlled failure did not occur after the approved two completed pages.' }
    if (@($details.output_cleanup.residual_outputs).Count -ne 0) { throw 'Controlled failure left residual formal output.' }
    $removed = @($details.output_cleanup.attempts | Where-Object { $_.removed -eq $true })
    if ($removed.Count -ne 2) { throw 'Controlled failure did not remove exactly two completed JPG files.' }

    $result.recovery = [ordered]@{
        continued_run_passed = $true
        jpg_count = 6
        editable_psd_count = 0
        prior_failure_code = $prior.error.code
        completed_pages_before_failure = $completed
        removed_partial_outputs = @($removed.name)
        residual_outputs = @($details.output_cleanup.residual_outputs)
        evidence_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $evidenceFiles[0].FullName).Hash
    }
}

$result | ConvertTo-Json -Depth 12
