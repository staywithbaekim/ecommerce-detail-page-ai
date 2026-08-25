$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $root 'uxp-validation-plugin/manifest.json'
$planPath = Join-Path $root 'uxp-validation-plugin/render-plans/sample-render-plan.json'
$singleFramePlanPath = Join-Path $root 'uxp-validation-plugin/render-plans/single-frame-g03-render-plan.json'
$multiFramePlanPath = Join-Path $root 'uxp-validation-plugin/render-plans/multi-frame-g05-render-plan.json'
$wholeProductPlanPath = Join-Path $root 'uxp-validation-plugin/render-plans/whole-product-a6015-render-plan.json'
$tv13FailurePlanPath = Join-Path $root 'uxp-validation-plugin/render-plans/tv13-controlled-failure-render-plan.json'
$mainPath = Join-Path $root 'uxp-validation-plugin/main.js'
$jpegIccPath = Join-Path $root 'uxp-validation-plugin/jpeg-icc.js'
$thirdPartyNoticePath = Join-Path $root 'uxp-validation-plugin/THIRD_PARTY_NOTICES.md'
$detailWord = ([string][char]0x8BE6) + ([string][char]0x60C5)
$detailSuffix = ([string][char]0x7EC6) + ([string][char]0x8282)
$fabricWord = ([string][char]0x9762) + ([string][char]0x6599)
$modelWord = ([string][char]0x6A21) + ([string][char]0x7279) + ([string][char]0x56FE)

$manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $manifestPath | ConvertFrom-Json
if ($manifest.manifestVersion -ne 5) { throw 'manifestVersion must be 5' }
if (-not $manifest.id -or -not $manifest.main) { throw 'manifest id/main is required' }
if ($manifest.version -ne '0.4.7') { throw 'plugin version must be 0.4.7 for the controlled whole-product failure-isolation build' }
if ($manifest.host.app -ne 'PS' -or $manifest.host.minVersion -ne '23.3.0') { throw 'Photoshop host baseline is incorrect' }
if ($manifest.entrypoints.type -notcontains 'panel') { throw 'a panel entrypoint is required' }
if ($manifest.requiredPermissions.localFileSystem -ne 'fullAccess') { throw 'fullAccess local file permission is required for evidence files' }
if ($manifest.requiredPermissions.network.domains -ne 'all') { throw 'diagnostic probe must temporarily allow all network domains to isolate manifest matching from host restrictions' }

$plan = Get-Content -Raw -Encoding UTF8 -LiteralPath $planPath | ConvertFrom-Json
if ($plan.schema -ne 'render-plan/v1') { throw 'RenderPlan schema is incorrect' }
if (-not $plan.input_policy.source_files_read_only -or -not $plan.input_policy.template_copies_only) { throw 'source safety policy is missing' }
if ($plan.pages.Count -ne 2) { throw 'sample plan must include the single and complex pages' }
if ($plan.output.jpg.color_profile -ne 'sRGB IEC61966-2.1') { throw 'sample plan JPG profile must be explicit sRGB IEC61966-2.1' }
if ($plan.output.jpg.conversion_intent -ne 'relative_colorimetric') { throw 'sample plan JPG conversion intent is incorrect' }
if (-not $plan.output.jpg.black_point_compensation -or -not $plan.output.jpg.dither -or -not $plan.output.jpg.embed_color_profile) { throw 'sample plan JPG sRGB conversion flags are incomplete' }
foreach ($page in $plan.pages) {
    if (-not $page.template_copy -or $page.template_copy -notlike 'sample-copies/*') { throw "page $($page.page_id) does not use a template copy" }
    foreach ($frame in $page.frames) {
        if (-not $frame.target.layer_path -or -not $frame.source.sha256 -or $frame.source.sha256.Length -ne 64) { throw "frame $($frame.frame_id) lacks binding evidence" }
        if ($frame.target.layer_path_order -ne 'psd_bottom_to_top') { throw "frame $($frame.frame_id) lacks the PSD bottom-to-top path semantic" }
        if ($frame.target.expected_kind -ne 'smartObject') { throw "frame $($frame.frame_id) lacks its calibrated smart-object kind" }
        if ($frame.target.registered_frame.Count -ne 4) { throw "frame $($frame.frame_id) lacks its registered frame" }
        if ($frame.fit -notin @('cover','contain')) { throw "frame $($frame.frame_id) has unsupported fit" }
    }
}

$singleFramePlan = Get-Content -Raw -Encoding UTF8 -LiteralPath $singleFramePlanPath | ConvertFrom-Json
if ($singleFramePlan.schema -ne 'render-plan/v1') { throw 'single-frame RenderPlan schema is incorrect' }
if ($singleFramePlan.validation.mode -ne 'single_frame_copy') { throw 'single-frame plan must use validation.mode single_frame_copy' }
if (-not $singleFramePlan.input_policy.source_files_read_only -or -not $singleFramePlan.input_policy.template_copies_only) { throw 'single-frame source safety policy is missing' }
if ($singleFramePlan.input_policy.allow_generated_imagery) { throw 'single-frame plan must forbid generated imagery' }
if ($singleFramePlan.pages.Count -ne 1 -or $singleFramePlan.pages[0].frames.Count -ne 1) { throw 'single-frame plan must include exactly one page and one frame' }

$singlePage = $singleFramePlan.pages[0]
$singleFrame = $singlePage.frames[0]
if ($singlePage.page_id -ne "G-$detailWord-03") { throw 'single-frame plan has the wrong page' }
if ($singlePage.template_copy -ne "fixture/template/G-$detailWord-03-validation-copy.psd") { throw 'single-frame plan must point to the disposable validation copy' }
if ($singlePage.template_sha256 -ne 'b94e7458a33f645e322f30efde675ccb268be5a72ca2ff4b7618f241eed14a35') { throw 'single-frame template hash is incorrect' }
if ($singleFrame.target.layer_path -ne '2' -or $singleFrame.target.expected_kind -ne 'smartObject') { throw 'single-frame calibrated binding is incorrect' }
if ($singleFrame.target.layer_path_order -ne 'psd_bottom_to_top') { throw 'single-frame layer path order is incorrect' }
if (($singleFrame.target.registered_frame -join ',') -ne '0,0,790,1252') { throw 'single-frame registered frame is incorrect' }
if ($singleFrame.source.relative_path -ne 'fixture/source/7Y9A3760.jpg') { throw 'single-frame source mapping is incorrect' }
if ($singleFrame.source.sha256 -ne '04720404a97fbe6dfd6eb138946c129545f37020f81346d98aa0616668c0b0db') { throw 'single-frame source hash is incorrect' }
if ($singleFrame.fit -ne 'cover' -or $singleFrame.crop.overflow_policy -ne 'clip_to_registered_frame') { throw 'single-frame geometry policy is incorrect' }
if (-not $singleFramePlan.output.jpg.enabled -or -not $singleFramePlan.output.editable_psd.enabled) { throw 'single-frame probe must produce JPG and editable PSD evidence' }
if ($singleFramePlan.output.jpg.color_profile -ne 'sRGB IEC61966-2.1') { throw 'single-frame JPG profile must be explicit sRGB IEC61966-2.1' }
if ($singleFramePlan.output.jpg.conversion_intent -ne 'relative_colorimetric') { throw 'single-frame JPG conversion intent is incorrect' }
if (-not $singleFramePlan.output.jpg.black_point_compensation -or -not $singleFramePlan.output.jpg.dither -or -not $singleFramePlan.output.jpg.embed_color_profile) { throw 'single-frame JPG sRGB conversion flags are incomplete' }

$multiFramePlan = Get-Content -Raw -Encoding UTF8 -LiteralPath $multiFramePlanPath | ConvertFrom-Json
if ($multiFramePlan.schema -ne 'render-plan/v1') { throw 'multi-frame RenderPlan schema is incorrect' }
if ($multiFramePlan.validation.mode -ne 'multi_frame_copy') { throw 'multi-frame plan must use validation.mode multi_frame_copy' }
if (-not $multiFramePlan.input_policy.source_files_read_only -or -not $multiFramePlan.input_policy.template_copies_only -or $multiFramePlan.input_policy.allow_generated_imagery) { throw 'multi-frame source safety policy is incorrect' }
if ($multiFramePlan.pages.Count -ne 1 -or $multiFramePlan.pages[0].frames.Count -ne 4) { throw 'multi-frame plan must include exactly one page and four frames' }
$multiPage = $multiFramePlan.pages[0]
if ($multiPage.page_id -ne "G-$detailWord-05-$detailSuffix") { throw 'multi-frame plan has the wrong page' }
if ($multiPage.template_copy -ne "fixture/template/G-$detailWord-05-validation-copy.psd") { throw 'multi-frame plan must point to the disposable validation copy' }
if ($multiPage.template_sha256 -ne '69bf44a64e47cb8d6817fa8589888163b14ab08a19a0bcfcee02352c3392f5ef') { throw 'multi-frame template hash is incorrect' }
if (($multiPage.expected_canvas -join ',') -ne '790,1115') { throw 'multi-frame canvas is incorrect' }
$expectedMultiFrames = @(
    @{ id = "G-$detailWord-05.frame-3"; path = '3'; window = '1'; superseded = '2'; source = '7Y9A3762.jpg'; hash = 'b7161b1a39447258c39464706cced111b0736189cfc20c608cb1fae5353b3de4'; frame = '406,698,766,985' },
    @{ id = "G-$detailWord-05.frame-7"; path = '7'; window = '5'; superseded = '6'; source = '7Y9A3751.jpg'; hash = 'f2862b0162aa2fb6bf969c88e9df9f0ec4839e5565b0a45ccf4af8b3721e0df3'; frame = '23,693,387,980' },
    @{ id = "G-$detailWord-05.frame-11"; path = '11'; window = '9'; superseded = '10'; source = '7Y9A3755.jpg'; hash = '868e85930c3996e2565490a89e80154cc6ef26edabf58044e635b475779079ca'; frame = '443,276,765,510' },
    @{ id = "G-$detailWord-05.frame-15"; path = '15'; window = '13'; superseded = '14'; source = '7Y9A3750.jpg'; hash = 'c04dddd4c7802277d9996f347ba6fd4285987147941eed5a69638e0443696d57'; frame = '23,188,422,672' }
)
for ($index = 0; $index -lt $expectedMultiFrames.Count; $index++) {
    $frame = $multiPage.frames[$index]
    $expected = $expectedMultiFrames[$index]
    if ($frame.frame_id -ne $expected.id -or $frame.target.layer_path -ne $expected.path) { throw "multi-frame binding $index is incorrect" }
    if ($frame.target.layer_path_order -ne 'psd_bottom_to_top' -or -not $frame.target.expected_visible -or -not $frame.target.expected_clipping) { throw "multi-frame target structure $index is incomplete" }
    if ($frame.target.window.layer_path -ne $expected.window -or $frame.target.window.expected_clipping) { throw "multi-frame window structure $index is incorrect" }
    if (($frame.target.superseded_paths -join ',') -ne $expected.superseded) { throw "multi-frame replacement stack $index is incorrect" }
    if (($frame.target.registered_frame -join ',') -ne $expected.frame) { throw "multi-frame registered frame $index is incorrect" }
    if (-not $frame.source.relative_path.EndsWith($expected.source) -or $frame.source.sha256 -ne $expected.hash) { throw "multi-frame source mapping $index is incorrect" }
    if ($frame.fit -ne 'cover' -or $frame.crop.overflow_policy -ne 'clip_to_registered_frame') { throw "multi-frame geometry $index is incorrect" }
}
if (-not $multiFramePlan.output.jpg.enabled -or -not $multiFramePlan.output.editable_psd.enabled) { throw 'multi-frame probe must produce JPG and editable PSD evidence' }
if ($multiFramePlan.output.jpg.color_profile -ne 'sRGB IEC61966-2.1' -or $multiFramePlan.output.jpg.conversion_intent -ne 'relative_colorimetric') { throw 'multi-frame JPG color policy is incorrect' }

$wholeProductPlan = Get-Content -Raw -Encoding UTF8 -LiteralPath $wholeProductPlanPath | ConvertFrom-Json
if ($wholeProductPlan.schema -ne 'render-plan/v1' -or $wholeProductPlan.validation.mode -ne 'whole_product_copy') { throw 'whole-product plan identity is incorrect' }
if (-not $wholeProductPlan.input_policy.source_files_read_only -or -not $wholeProductPlan.input_policy.template_copies_only -or $wholeProductPlan.input_policy.allow_generated_imagery) { throw 'whole-product source safety policy is incorrect' }
$wholePages = @($wholeProductPlan.pages)
$wholeFrames = @($wholePages | ForEach-Object { @($_.frames) })
if ($wholePages.Count -ne 6 -or $wholeFrames.Count -ne 11) { throw 'whole-product plan must include exactly six pages and eleven frames' }
if (-not $wholeProductPlan.output.jpg.enabled -or $wholeProductPlan.output.editable_psd.enabled) { throw 'whole-product plan must use the default JPG-only output policy' }
if ($wholeProductPlan.output.jpg.color_profile -ne 'sRGB IEC61966-2.1' -or $wholeProductPlan.output.jpg.conversion_intent -ne 'relative_colorimetric') { throw 'whole-product JPG color policy is incorrect' }
if ($wholeProductPlan.output.jpg.overwrite -or $wholeProductPlan.output.editable_psd.overwrite) { throw 'whole-product outputs must not overwrite existing files' }
$expectedWholePages = @(
    "G-$detailWord-01",
    "G-$detailWord-03",
    "G-$detailWord-04-$fabricWord",
    "G-$detailWord-05-$detailSuffix",
    "H-$modelWord-01",
    "H-$modelWord-02"
)
foreach ($pageId in $expectedWholePages) {
    if ($pageId -notin @($wholePages.page_id)) { throw "whole-product plan is missing page $pageId" }
}
foreach ($page in $wholePages) {
    if ($page.template_copy -notlike 'fixture/template/*-validation-copy.psd' -or $page.template_sha256.Length -ne 64) { throw "whole-product page $($page.page_id) has an unsafe template binding" }
    foreach ($frame in @($page.frames)) {
        if ($frame.target.layer_path_order -ne 'psd_bottom_to_top' -or $frame.target.expected_kind -ne 'smartObject') { throw "whole-product frame $($frame.frame_id) has an invalid layer binding" }
        if ($null -ne $frame.target.PSObject.Properties['superseded_paths'] -or $null -eq $frame.target.PSObject.Properties['superseded_layers']) { throw "whole-product frame $($frame.frame_id) lacks structured superseded-layer contracts" }
        foreach ($superseded in @($frame.target.superseded_layers)) {
            if ($superseded.layer_path_order -ne 'psd_bottom_to_top' -or $superseded.expected_kind -ne 'smartObject' -or $superseded.expected_visible -ne $true -or $superseded.expected_clipping -isnot [bool]) { throw "whole-product frame $($frame.frame_id) has an invalid superseded-layer contract" }
        }
        if ($frame.source.sha256.Length -ne 64 -or $frame.fit -ne 'cover' -or $frame.crop.overflow_policy -ne 'clip_to_registered_frame') { throw "whole-product frame $($frame.frame_id) has an invalid source or geometry policy" }
    }
}
$wholeSuperseded = @($wholePages | ForEach-Object { @($_.frames) } | ForEach-Object { @($_.target.superseded_layers) })
if ($wholeSuperseded.Count -ne 14 -or @($wholeSuperseded | Where-Object { $_.expected_clipping -eq $false }).Count -ne 1) { throw 'whole-product superseded-layer calibration totals are incorrect' }

$tv13FailurePlan = Get-Content -Raw -Encoding UTF8 -LiteralPath $tv13FailurePlanPath | ConvertFrom-Json
$tv13Failure = $tv13FailurePlan.validation.controlled_failure
if ($tv13FailurePlan.job_id -ne 'uxp-a6015-tv13-failure-001' -or $tv13FailurePlan.validation.mode -ne 'whole_product_copy') { throw 'TV-13 plan identity is incorrect' }
if (@($tv13FailurePlan.pages).Count -ne 6 -or @($tv13FailurePlan.pages | ForEach-Object { @($_.frames) }).Count -ne 11) { throw 'TV-13 plan must preserve the six-page eleven-frame fixture' }
if ($tv13Failure.checkpoint -ne 'after_completed_pages' -or $tv13Failure.completed_page_count -ne 2 -or $tv13Failure.error_code -ne 'TV13_CONTROLLED_FAILURE') { throw 'TV-13 controlled failure checkpoint is incorrect' }
if ($tv13Failure.next_page_id -ne "G-$detailWord-04-$fabricWord" -or $tv13Failure.next_frame_id -ne "G-$detailWord-04.frame-4") { throw 'TV-13 next page or frame evidence is incorrect' }

$mainSource = Get-Content -Raw -Encoding UTF8 -LiteralPath $mainPath
foreach ($requiredToken in @(
    'placedLayerReplaceContents',
    'const uxpIndex = collection.length - 1 - psdIndex',
    'preflightFrameBindings(document, pagePlan, structureBefore)',
    'multi_frame_copy',
    'whole_product_copy',
    'executeWholeProductPlan',
    'openDocumentAcrossModalBoundary',
    'createPhotoshopOpenDescriptor',
    'batchplay_session_token',
    'localFileSystem.createSessionToken(entry)',
    'target: { _path: sessionToken, _kind: "local" }',
    '_options: { dialogOptions: "silent" }',
    'firstResult._obj === "error"',
    'document_visibility_wait_ms',
    'supersededStructure.clipping === supersededContract.expected_clipping',
    'TARGET_CLIPPING_MISMATCH',
    'WINDOW_BOUNDS_MISMATCH',
    'SUPERSEDED_CLIPPING_MISMATCH',
    'compareStructureSnapshots',
    'non_target_structure_unchanged',
    'BINDING_AUDIT_MISMATCH',
    'document.saveAs.psd',
    'document.convertProfile',
    'constants.Intent.RELATIVECOLORIMETRIC',
    'document.colorProfileName',
    'sRGB IEC61966-2.1',
    'document.saveAs.jpg',
    'ensureJpegHasSrgbProfile',
    'storage.formats.binary',
    'embedding_requested',
    'photoshop_embedded_before_postprocess',
    'adapter_injected',
    'embedding_verified',
    'document.closeWithoutSaving()',
    'partial_outputs_removed: cleanup.confirmed',
    'TV13_CONTROLLED_FAILURE',
    'prior_controlled_failure: lastControlledFailureEvidence',
    'lastControlledFailureEvidence = result',
    'residual_outputs',
    'source_template_saved: false'
)) {
    if (-not $mainSource.Contains($requiredToken)) { throw "main.js is missing required safety or execution token: $requiredToken" }
}

foreach ($requiredPath in @($jpegIccPath, $thirdPartyNoticePath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) { throw "missing JPG color artifact: $requiredPath" }
}
$jpegIccSource = Get-Content -Raw -Encoding UTF8 -LiteralPath $jpegIccPath
foreach ($requiredToken in @(
    '14FB97CEB79AB10B4A72C839A16A334CAC46E7991C6A7F46DE4E56136675920D',
    'Existing JPEG ICC profile is not a valid sRGB profile.',
    'if (before.present)',
    'valid_srgb'
)) {
    if (-not $jpegIccSource.Contains($requiredToken)) { throw "jpeg-icc.js is missing required validation token: $requiredToken" }
}

Write-Output 'UXP prototype preflight: PASS'
Write-Output "Manifest: $manifestPath"
Write-Output "RenderPlan pages: $($plan.pages.Count)"
Write-Output "RenderPlan frames: $(($plan.pages | ForEach-Object { $_.frames.Count } | Measure-Object -Sum).Sum)"
Write-Output "Single-frame plan: $($singlePage.page_id) / $($singleFrame.frame_id)"
Write-Output "Multi-frame plan: $($multiPage.page_id) / $($multiPage.frames.Count) frames"
Write-Output "Whole-product plan: $($wholePages.Count) pages / $($wholeFrames.Count) frames"
