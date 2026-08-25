param(
    [Parameter(Mandatory = $false)]
    [string]$FixtureRoot,

    [Parameter(Mandatory = $false)]
    [switch]$RequireOutputs
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($FixtureRoot)) {
    $FixtureRoot = $PSScriptRoot
}
if ([string]::IsNullOrWhiteSpace($FixtureRoot)) {
    throw 'FixtureRoot could not be resolved from the script location.'
}

function Test-ByteSequence {
    param([byte[]]$Bytes, [byte[]]$Sequence)
    for ($offset = 0; $offset -le $Bytes.Length - $Sequence.Length; $offset++) {
        $matches = $true
        for ($index = 0; $index -lt $Sequence.Length; $index++) {
            if ($Bytes[$offset + $index] -ne $Sequence[$index]) {
                $matches = $false
                break
            }
        }
        if ($matches) { return $true }
    }
    return $false
}

function Get-RelativeFileName {
    param([string]$Value)
    return [System.IO.Path]::GetFileName(($Value -replace '/', '\'))
}

$resolvedRoot = (Resolve-Path -LiteralPath $FixtureRoot).Path
$templateRoot = Join-Path $resolvedRoot 'template'
$sourceRoot = Join-Path $resolvedRoot 'source'
$referenceRoot = Join-Path $resolvedRoot 'reference'
$outputRoot = Join-Path $resolvedRoot 'output'
$planPath = Join-Path $resolvedRoot 'whole-product-a6015-render-plan.json'

foreach ($requiredPath in @($templateRoot, $sourceRoot, $referenceRoot, $outputRoot, $planPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) { throw "Missing fixture path: $requiredPath" }
}

$plan = Get-Content -Raw -Encoding UTF8 -LiteralPath $planPath | ConvertFrom-Json
if ($plan.schema -ne 'render-plan/v1' -or $plan.validation.mode -ne 'whole_product_copy') { throw 'Fixture RenderPlan is not the controlled whole-product plan.' }
if (-not $plan.input_policy.source_files_read_only -or -not $plan.input_policy.template_copies_only -or $plan.input_policy.allow_generated_imagery) { throw 'Fixture source-safety policy is incorrect.' }
if (-not $plan.output.jpg.enabled -or $plan.output.editable_psd.enabled) { throw 'Fixture must represent the default JPG-only workflow.' }
if ($plan.output.jpg.overwrite -or $plan.output.editable_psd.overwrite) { throw 'Fixture must forbid output overwrite.' }
if ($plan.output.jpg.color_profile -ne 'sRGB IEC61966-2.1' -or $plan.output.jpg.conversion_intent -ne 'relative_colorimetric') { throw 'Fixture JPG color policy is incorrect.' }
if (-not $plan.output.jpg.black_point_compensation -or -not $plan.output.jpg.dither -or -not $plan.output.jpg.embed_color_profile) { throw 'Fixture JPG conversion flags are incomplete.' }

$pages = @($plan.pages)
$frames = @($pages | ForEach-Object { @($_.frames) })
if ($pages.Count -ne 6 -or $frames.Count -ne 11) { throw 'Fixture RenderPlan must contain exactly six pages and eleven frames.' }
if (@($pages.page_id | Select-Object -Unique).Count -ne 6) { throw 'Fixture RenderPlan contains a duplicate page id.' }
if (@($frames.frame_id | Select-Object -Unique).Count -ne 11) { throw 'Fixture RenderPlan contains a duplicate frame id.' }

$expectedReferenceHashes = @{
    '790x1327' = 'D2A99449C920F06C4AB419A9558BBB34D332A03F55E275AEA7F738AE57A29422'
    '790x1252' = '63898394C8833DF916E0CD7EF4B3B6852519368E8AC2CC858D2A888713F01309'
    '790x1182' = 'CFDF12FB2AB9622FB462060ACD2AB89CCB18DF7CB4AFEAA9CC3BB11532604B0C'
    '790x1115' = '1377264FB8A2A8AF9F525413BE57A8D0B2FDDE01F8263662B4F3CFFA58F232F1'
    '790x2425' = '107B1613EA9CB6F63A3A8DA8DEBB6B4A1721B09A483FB00F849B4C7288CEB5B4'
    '790x2546' = 'DF12B64F9A2B897DA6B4140D28CBAF287422177C1A4C690B561C8B6686A3F8F3'
}
$templateHashes = [ordered]@{}
$sourceHashes = [ordered]@{}
$referenceHashes = [ordered]@{}
$expectedOutputNames = @()
$expectedSupersededSignatures = @(
    '2:False',
    '',
    '3:True',
    '2:True',
    '6:True',
    '10:True',
    '14:True',
    '2:True,3:True',
    '7:True,8:True',
    '2:True,3:True',
    '7:True,8:True'
)
$frameContractIndex = 0
$supersededContractCount = 0
$nonClippingSupersededCount = 0

Add-Type -AssemblyName System.Drawing
foreach ($page in $pages) {
    $templateName = Get-RelativeFileName $page.template_copy
    $templatePath = Join-Path $templateRoot $templateName
    if (-not (Test-Path -LiteralPath $templatePath -PathType Leaf)) { throw "Missing template copy: $templateName" }
    $templateHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $templatePath).Hash
    if ($templateHash -ne $page.template_sha256.ToUpperInvariant()) { throw "Template copy hash mismatch: $templateName" }
    $templateHashes[$templateName] = $templateHash

    if (@($page.expected_canvas).Count -ne 2) { throw "Page canvas is incomplete: $($page.page_id)" }
    $canvasKey = "$($page.expected_canvas[0])x$($page.expected_canvas[1])"
    $referenceName = "$($page.page_id)-manual-reference.jpg"
    $referencePath = Join-Path $referenceRoot $referenceName
    if (-not (Test-Path -LiteralPath $referencePath -PathType Leaf)) { throw "Missing manual reference: $referenceName" }
    $referenceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $referencePath).Hash
    if ($referenceHash -ne $expectedReferenceHashes[$canvasKey]) { throw "Manual reference hash mismatch: $referenceName" }
    $referenceImage = [System.Drawing.Image]::FromFile($referencePath)
    try {
        if ($referenceImage.Width -ne $page.expected_canvas[0] -or $referenceImage.Height -ne $page.expected_canvas[1]) { throw "Manual reference dimensions mismatch: $referenceName" }
    } finally {
        $referenceImage.Dispose()
    }
    $referenceHashes[$referenceName] = $referenceHash
    $expectedOutputNames += "$($page.page_id)-validation-output.jpg"

    foreach ($frame in @($page.frames)) {
        if ($frame.target.layer_path_order -ne 'psd_bottom_to_top' -or $frame.target.expected_kind -ne 'smartObject') { throw "Frame binding contract mismatch: $($frame.frame_id)" }
        if ($frame.fit -ne 'cover' -or $frame.crop.overflow_policy -ne 'clip_to_registered_frame') { throw "Frame geometry contract mismatch: $($frame.frame_id)" }
        if ($null -ne $frame.target.PSObject.Properties['superseded_paths']) { throw "Legacy superseded paths are not allowed in whole-product mode: $($frame.frame_id)" }
        if ($null -eq $frame.target.PSObject.Properties['superseded_layers']) { throw "Structured superseded-layer contracts are missing: $($frame.frame_id)" }
        $actualSupersededSignature = @(@($frame.target.superseded_layers) | ForEach-Object {
            if ($_.layer_path_order -ne 'psd_bottom_to_top' -or $_.expected_kind -ne 'smartObject' -or $_.expected_visible -ne $true) {
                throw "Superseded-layer structure is incomplete: $($frame.frame_id)"
            }
            $supersededContractCount += 1
            if ($_.expected_clipping -eq $false) { $nonClippingSupersededCount += 1 }
            "$($_.layer_path):$([bool]$_.expected_clipping)"
        }) -join ','
        if ($actualSupersededSignature -cne $expectedSupersededSignatures[$frameContractIndex]) { throw "Superseded-layer contract mismatch: $($frame.frame_id)" }
        $frameContractIndex += 1
        $sourceName = Get-RelativeFileName $frame.source.relative_path
        $sourcePath = Join-Path $sourceRoot $sourceName
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw "Missing approved source image: $sourceName" }
        $sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourcePath).Hash
        if ($sourceHash -ne $frame.source.sha256.ToUpperInvariant()) { throw "Source image hash mismatch: $sourceName" }
        if ($sourceHashes.Contains($sourceName) -and $sourceHashes[$sourceName] -ne $sourceHash) { throw "Conflicting source hash: $sourceName" }
        $sourceHashes[$sourceName] = $sourceHash
    }
}

if ($sourceHashes.Count -ne 6) { throw 'Fixture must contain exactly six approved source images.' }
if ($frameContractIndex -ne 11 -or $supersededContractCount -ne 14 -or $nonClippingSupersededCount -ne 1) { throw 'Whole-product superseded-layer contract totals are incorrect.' }

$result = [ordered]@{
    passed = $true
    fixture_root = $resolvedRoot
    input_hashes = [ordered]@{
        template_copies = $templateHashes
        sources = $sourceHashes
        manual_references = $referenceHashes
    }
    page_count = $pages.Count
    frame_count = $frames.Count
    outputs_required = [bool]$RequireOutputs
}

if ($RequireOutputs) {
    $evidenceFiles = @(Get-ChildItem -LiteralPath $outputRoot -File -Filter '*-whole-product-validation-evidence.json')
    if ($evidenceFiles.Count -ne 1) { throw 'Output must contain exactly one whole-product evidence JSON.' }
    $evidencePath = $evidenceFiles[0].FullName
    $jpgFiles = @(Get-ChildItem -LiteralPath $outputRoot -File -Filter '*.jpg')
    if ($jpgFiles.Count -ne 6) { throw 'Output must contain exactly six JPG files.' }
    if (@(Get-ChildItem -LiteralPath $outputRoot -File -Filter '*.psd').Count -ne 0) { throw 'Default whole-product output must not contain PSD files.' }
    $actualJpgNames = @($jpgFiles.Name | Sort-Object)
    if (($actualJpgNames -join '|') -cne (($expectedOutputNames | Sort-Object) -join '|')) { throw 'Whole-product JPG output names are incomplete or unexpected.' }

    [byte[]]$iccSignature = @(0x49, 0x43, 0x43, 0x5F, 0x50, 0x52, 0x4F, 0x46, 0x49, 0x4C, 0x45, 0x00)
    $jpgHashes = [ordered]@{}
    foreach ($page in $pages) {
        $jpgName = "$($page.page_id)-validation-output.jpg"
        $jpgPath = Join-Path $outputRoot $jpgName
        $jpgImage = [System.Drawing.Image]::FromFile($jpgPath)
        try {
            if ($jpgImage.Width -ne $page.expected_canvas[0] -or $jpgImage.Height -ne $page.expected_canvas[1]) { throw "Generated JPG dimensions mismatch: $jpgName" }
        } finally {
            $jpgImage.Dispose()
        }
        [byte[]]$jpgBytes = [System.IO.File]::ReadAllBytes($jpgPath)
        if (-not (Test-ByteSequence -Bytes $jpgBytes -Sequence $iccSignature)) { throw "Generated JPG has no ICC_PROFILE marker: $jpgName" }
        $jpgHashes[$jpgName] = (Get-FileHash -Algorithm SHA256 -LiteralPath $jpgPath).Hash
    }

    $evidence = Get-Content -Raw -Encoding UTF8 -LiteralPath $evidencePath | ConvertFrom-Json
    if (-not $evidence.ok -or $evidence.schema -ne 'uxp-whole-product-validation-evidence/v1') { throw 'UXP evidence does not report a successful whole-product run.' }
    if ($evidence.plugin_version -ne '0.4.6' -or $evidence.validation_mode -ne 'whole_product_copy') { throw 'UXP evidence version or validation mode is incorrect.' }
    if ($evidence.job_id -ne $plan.job_id -or $evidence.page_count -ne 6 -or $evidence.frame_count -ne 11) { throw 'UXP evidence job, page, or frame counts are incorrect.' }
    if ($evidence.output.jpg_count -ne 6 -or -not $evidence.output.all_fixed_structures_unchanged -or -not $evidence.output.all_jpg_profiles_verified) { throw 'UXP evidence aggregate output checks did not pass.' }
    if (@($evidence.page_results).Count -ne 6) { throw 'UXP evidence does not contain six page results.' }

    foreach ($page in $pages) {
        $matches = @($evidence.page_results | Where-Object { $_.page_id -eq $page.page_id })
        if ($matches.Count -ne 1) { throw "UXP evidence page result is missing or duplicated: $($page.page_id)" }
        $pageResult = $matches[0]
        if ($pageResult.expected_template_sha256.ToUpperInvariant() -ne $page.template_sha256.ToUpperInvariant()) { throw "UXP evidence template hash mismatch: $($page.page_id)" }
        if ($pageResult.open.strategy -ne 'batchplay_session_token' -or $pageResult.open.descriptor_result_count -ne 1 -or [int64]$pageResult.open.document_id -le 0) { throw "UXP evidence open strategy is incorrect: $($page.page_id)" }
        if ($null -ne $pageResult.open.descriptor_document_id -and [int64]$pageResult.open.descriptor_document_id -ne [int64]$pageResult.open.document_id) { throw "UXP evidence Action Manager and DOM document ids differ: $($page.page_id)" }
        if (@($pageResult.frame_ids).Count -ne @($page.frames).Count -or @($pageResult.sources).Count -ne @($page.frames).Count) { throw "UXP evidence frame count mismatch: $($page.page_id)" }
        if ($pageResult.output.source_template_saved -or -not $pageResult.output.jpg_profile.embedding_verified) { throw "UXP evidence output safety or ICC check failed: $($page.page_id)" }
        if (-not $pageResult.photoshop.structural_audit.passed -or @($pageResult.photoshop.structural_audit.differences).Count -ne 0) { throw "UXP structural audit failed: $($page.page_id)" }
        $expectedFrameIds = @($page.frames.frame_id | Sort-Object)
        if ((@($pageResult.photoshop.attempted_frame_ids | Sort-Object) -join '|') -cne ($expectedFrameIds -join '|')) { throw "Attempted frame evidence mismatch: $($page.page_id)" }
        if ((@($pageResult.photoshop.mutated_frame_ids | Sort-Object) -join '|') -cne ($expectedFrameIds -join '|')) { throw "Mutated frame evidence mismatch: $($page.page_id)" }
        if ((@($pageResult.photoshop.completed_frame_ids | Sort-Object) -join '|') -cne ($expectedFrameIds -join '|')) { throw "Completed frame evidence mismatch: $($page.page_id)" }
    }

    $result.outputs = [ordered]@{
        jpgs = $jpgHashes
        evidence = (Get-FileHash -Algorithm SHA256 -LiteralPath $evidencePath).Hash
    }
    $result.output_properties = [ordered]@{
        jpg_count = 6
        editable_psd_count = 0
        jpg_icc_markers_present = $true
        all_fixed_structures_unchanged = $true
        page_count = 6
        frame_count = 11
        manual_visual_confirmation_required = $true
    }
}

$result | ConvertTo-Json -Depth 12
