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

function Get-BigEndianUInt16 {
    param([byte[]]$Bytes, [int]$Offset)
    return (([int]$Bytes[$Offset] -shl 8) -bor [int]$Bytes[$Offset + 1])
}

function Get-BigEndianUInt32 {
    param([byte[]]$Bytes, [int]$Offset)
    return (([uint32]$Bytes[$Offset] -shl 24) -bor ([uint32]$Bytes[$Offset + 1] -shl 16) -bor ([uint32]$Bytes[$Offset + 2] -shl 8) -bor [uint32]$Bytes[$Offset + 3])
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

function Convert-ComparableJson {
    param($Value)
    return ($Value | ConvertTo-Json -Depth 8 -Compress)
}

$resolvedRoot = (Resolve-Path -LiteralPath $FixtureRoot).Path
$detailWord = ([string][char]0x8BE6) + ([string][char]0x60C5)
$detailSuffix = ([string][char]0x7EC6) + ([string][char]0x8282)
$templateName = "G-$detailWord-05-validation-copy.psd"
$templatePath = Join-Path $resolvedRoot "template\$templateName"
$sourceRoot = Join-Path $resolvedRoot 'source'
$outputRoot = Join-Path $resolvedRoot 'output'
$planPath = Join-Path $resolvedRoot 'multi-frame-g05-render-plan.json'
$expectedTemplateHash = '69BF44A64E47CB8D6817FA8589888163B14AB08A19A0BCFCEE02352C3392F5EF'
$expectedFrames = @(
    [ordered]@{ id = "G-$detailWord-05.frame-3"; path = '3'; audit = 'care_label'; window = '1'; superseded = '2'; source = '7Y9A3762.jpg'; hash = 'B7161B1A39447258C39464706CCED111B0736189CFC20C608CB1FAE5353B3DE4'; frame = @(406, 698, 766, 985) },
    [ordered]@{ id = "G-$detailWord-05.frame-7"; path = '7'; audit = 'collar'; window = '5'; superseded = '6'; source = '7Y9A3751.jpg'; hash = 'F2862B0162AA2FB6BF969C88E9DF9F0EC4839E5565B0A45CCF4AF8B3721E0DF3'; frame = @(23, 693, 387, 980) },
    [ordered]@{ id = "G-$detailWord-05.frame-11"; path = '11'; audit = "`tsleeve"; window = '9'; superseded = '10'; source = '7Y9A3755.jpg'; hash = '868E85930C3996E2565490A89E80154CC6EF26EDABF58044E635B475779079CA'; frame = @(443, 276, 765, 510) },
    [ordered]@{ id = "G-$detailWord-05.frame-15"; path = '15'; audit = "`tfront"; window = '13'; superseded = '14'; source = '7Y9A3750.jpg'; hash = 'C04DDDD4C7802277D9996F347BA6FD4285987147941EED5A69638E0443696D57'; frame = @(23, 188, 422, 672) }
)

foreach ($requiredPath in @($templatePath, $sourceRoot, $outputRoot, $planPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) { throw "Missing fixture path: $requiredPath" }
}

$templateHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $templatePath).Hash
if ($templateHash -ne $expectedTemplateHash) { throw 'Disposable PSD copy hash does not match the approved G-detail-05 fixture.' }

$sourceHashes = [ordered]@{}
foreach ($expected in $expectedFrames) {
    $sourcePath = Join-Path $sourceRoot $expected.source
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw "Missing approved source image: $($expected.source)" }
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourcePath).Hash
    if ($actualHash -ne $expected.hash) { throw "Source image hash mismatch: $($expected.source)" }
    $sourceHashes[$expected.source] = $actualHash
}

$plan = Get-Content -Raw -Encoding UTF8 -LiteralPath $planPath | ConvertFrom-Json
if ($plan.schema -ne 'render-plan/v1' -or $plan.validation.mode -ne 'multi_frame_copy') { throw 'Fixture RenderPlan is not the controlled multi-frame plan.' }
if (-not $plan.input_policy.source_files_read_only -or -not $plan.input_policy.template_copies_only -or $plan.input_policy.allow_generated_imagery) { throw 'Fixture RenderPlan source-safety policy is incorrect.' }
if (@($plan.pages).Count -ne 1 -or @($plan.pages[0].frames).Count -ne 4) { throw 'Fixture RenderPlan must contain exactly one page and four frames.' }
$page = $plan.pages[0]
if ($page.page_id -ne "G-$detailWord-05-$detailSuffix" -or $page.template_sha256.ToUpperInvariant() -ne $expectedTemplateHash) { throw 'Fixture RenderPlan page identity is incorrect.' }
if (($page.expected_canvas -join ',') -ne '790,1115') { throw 'Fixture RenderPlan canvas is incorrect.' }
if ($plan.output.jpg.color_profile -ne 'sRGB IEC61966-2.1' -or $plan.output.jpg.conversion_intent -ne 'relative_colorimetric') { throw 'Fixture RenderPlan JPG color policy is incorrect.' }
if (-not $plan.output.jpg.black_point_compensation -or -not $plan.output.jpg.dither -or -not $plan.output.jpg.embed_color_profile) { throw 'Fixture RenderPlan JPG conversion flags are incomplete.' }
if ($plan.output.jpg.overwrite -or $plan.output.editable_psd.overwrite) { throw 'Fixture RenderPlan must forbid output overwrite.' }

for ($index = 0; $index -lt $expectedFrames.Count; $index++) {
    $expected = $expectedFrames[$index]
    $frame = $page.frames[$index]
    if ($frame.frame_id -ne $expected.id -or $frame.target.layer_path -ne $expected.path) { throw "Frame binding mismatch at index $index." }
    if ($frame.target.audit_name -cne $expected.audit) { throw "Frame audit name mismatch at index $index." }
    if ($frame.target.layer_path_order -ne 'psd_bottom_to_top' -or $frame.target.expected_kind -ne 'smartObject') { throw "Frame layer-path contract mismatch at index $index." }
    if (-not $frame.target.expected_visible -or -not $frame.target.expected_clipping) { throw "Frame clipping contract mismatch at index $index." }
    if (($frame.target.registered_frame -join ',') -ne ($expected.frame -join ',')) { throw "Registered frame mismatch at index $index." }
    if ($frame.target.window.layer_path -ne $expected.window -or $frame.target.window.expected_clipping) { throw "Window binding mismatch at index $index." }
    if (($frame.target.superseded_paths -join ',') -ne $expected.superseded) { throw "Replacement-stack binding mismatch at index $index." }
    if (-not $frame.source.relative_path.EndsWith($expected.source) -or $frame.source.sha256.ToUpperInvariant() -ne $expected.hash) { throw "Source mapping mismatch at index $index." }
    if ($frame.fit -ne 'cover' -or $frame.crop.overflow_policy -ne 'clip_to_registered_frame') { throw "Geometry policy mismatch at index $index." }
}

$result = [ordered]@{
    passed = $true
    fixture_root = $resolvedRoot
    input_hashes = [ordered]@{
        template_copy = $templateHash
        sources = $sourceHashes
    }
    outputs_required = [bool]$RequireOutputs
}

if ($RequireOutputs) {
    $psdPath = Join-Path $outputRoot "G-$detailWord-05-validation-output.psd"
    $jpgPath = Join-Path $outputRoot "G-$detailWord-05-validation-output.jpg"
    $evidencePath = Join-Path $outputRoot "G-$detailWord-05-validation-evidence.json"
    foreach ($requiredOutput in @($psdPath, $jpgPath, $evidencePath)) {
        if (-not (Test-Path -LiteralPath $requiredOutput -PathType Leaf)) { throw "Missing validation output: $requiredOutput" }
        if ((Get-Item -LiteralPath $requiredOutput).Length -le 0) { throw "Validation output is empty: $requiredOutput" }
    }

    [byte[]]$psdBytes = [System.IO.File]::ReadAllBytes($psdPath)
    if ($psdBytes.Length -lt 26 -or [Text.Encoding]::ASCII.GetString($psdBytes, 0, 4) -ne '8BPS') { throw 'Generated PSD has an invalid header.' }
    $psdHeight = Get-BigEndianUInt32 -Bytes $psdBytes -Offset 14
    $psdWidth = Get-BigEndianUInt32 -Bytes $psdBytes -Offset 18
    $psdDepth = Get-BigEndianUInt16 -Bytes $psdBytes -Offset 22
    $psdColorMode = Get-BigEndianUInt16 -Bytes $psdBytes -Offset 24
    if ($psdWidth -ne 790 -or $psdHeight -ne 1115 -or $psdDepth -ne 8 -or $psdColorMode -ne 3) { throw 'Generated PSD canvas or color mode is incorrect.' }

    Add-Type -AssemblyName System.Drawing
    $jpgImage = [System.Drawing.Image]::FromFile($jpgPath)
    try {
        if ($jpgImage.Width -ne 790 -or $jpgImage.Height -ne 1115) { throw 'Generated JPG dimensions are incorrect.' }
    } finally {
        $jpgImage.Dispose()
    }
    [byte[]]$jpgBytes = [System.IO.File]::ReadAllBytes($jpgPath)
    [byte[]]$iccSignature = @(0x49, 0x43, 0x43, 0x5F, 0x50, 0x52, 0x4F, 0x46, 0x49, 0x4C, 0x45, 0x00)
    if (-not (Test-ByteSequence -Bytes $jpgBytes -Sequence $iccSignature)) { throw 'Generated JPG does not contain an ICC_PROFILE marker.' }

    $evidence = Get-Content -Raw -Encoding UTF8 -LiteralPath $evidencePath | ConvertFrom-Json
    if (-not $evidence.ok -or $evidence.schema -ne 'uxp-multi-frame-validation-evidence/v1') { throw 'UXP evidence does not report a successful multi-frame run.' }
    if ($evidence.plugin_version -ne '0.3.5' -or $evidence.validation_mode -ne 'multi_frame_copy') { throw 'UXP evidence version or validation mode is incorrect.' }
    if ($evidence.job_id -ne 'uxp-g05-multi-frame-001' -or $evidence.page_id -ne "G-$detailWord-05-$detailSuffix") { throw 'UXP evidence job identity is incorrect.' }
    if (@($evidence.frame_ids).Count -ne 4 -or @($evidence.input.sources).Count -ne 4 -or @($evidence.bindings).Count -ne 4) { throw 'UXP evidence does not contain all four frames.' }
    if ($evidence.output.source_template_saved) { throw 'UXP evidence indicates that the input PSD copy was saved.' }
    if ($evidence.output.jpg_color_profile -ne 'sRGB IEC61966-2.1' -or -not $evidence.output.jpg_profile.embedding_verified) { throw 'UXP evidence does not report a verified sRGB JPG.' }
    if ($evidence.output.jpg_profile.profile_bytes -lt 128) { throw 'UXP evidence reports an invalid ICC profile length.' }

    $structuralAudit = $evidence.photoshop.structural_audit
    if (-not $structuralAudit.passed -or -not $structuralAudit.non_target_structure_unchanged -or @($structuralAudit.differences).Count -ne 0) { throw 'UXP structural audit did not pass.' }
    $before = @($evidence.photoshop.structure_before)
    $after = @($evidence.photoshop.structure_after)
    if ($before.Count -eq 0 -or $before.Count -ne $after.Count) { throw 'UXP structural snapshots are incomplete.' }
    $targetPaths = @('3', '7', '11', '15')
    $invariantProperties = @('id', 'name', 'kind', 'visible', 'clipping', 'has_user_mask', 'has_vector_mask', 'opacity', 'blend_mode')
    foreach ($beforeLayer in $before) {
        $afterMatches = @($after | Where-Object { $_.layer_path -eq $beforeLayer.layer_path })
        if ($afterMatches.Count -ne 1) { throw "Structural snapshot path mismatch: $($beforeLayer.layer_path)" }
        $afterLayer = $afterMatches[0]
        foreach ($property in $invariantProperties) {
            if ((Convert-ComparableJson $beforeLayer.$property) -cne (Convert-ComparableJson $afterLayer.$property)) { throw "Structural property changed at $($beforeLayer.layer_path): $property" }
        }
        if ($targetPaths -notcontains $beforeLayer.layer_path) {
            if ((Convert-ComparableJson $beforeLayer.bounds) -cne (Convert-ComparableJson $afterLayer.bounds)) { throw "Non-target bounds changed at $($beforeLayer.layer_path)." }
        }
    }

    $frameResults = @($evidence.photoshop.frame_results)
    if ($frameResults.Count -ne 4) { throw 'UXP evidence does not contain four frame results.' }
    for ($index = 0; $index -lt $expectedFrames.Count; $index++) {
        $expected = $expectedFrames[$index]
        $frameResult = $frameResults[$index]
        if ($frameResult.frame_id -ne $expected.id -or $frameResult.source_name -ne $expected.source) { throw "Frame result mismatch at index $index." }
        if (-not $frameResult.binding.target.clipping -or $frameResult.binding.window.clipping -or @($frameResult.binding.superseded).Count -ne 1 -or -not $frameResult.binding.superseded[0].clipping) { throw "Frame clipping evidence mismatch at index $index." }
        $fitted = $frameResult.geometry.fitted
        if ($fitted.left -gt ($expected.frame[0] + 1.5) -or $fitted.top -gt ($expected.frame[1] + 1.5) -or $fitted.right -lt ($expected.frame[2] - 1.5) -or $fitted.bottom -lt ($expected.frame[3] - 1.5)) { throw "Frame does not cover its registered window at index $index." }
    }

    $result.outputs = [ordered]@{
        editable_psd = (Get-FileHash -Algorithm SHA256 -LiteralPath $psdPath).Hash
        jpg = (Get-FileHash -Algorithm SHA256 -LiteralPath $jpgPath).Hash
        evidence = (Get-FileHash -Algorithm SHA256 -LiteralPath $evidencePath).Hash
    }
    $result.output_properties = [ordered]@{
        canvas = '790x1115'
        psd = '8-bit RGB'
        jpg_icc_marker_present = $true
        fixed_structure_unchanged = $true
        frame_count = 4
        manual_visual_confirmation_required = $true
    }
}

$result | ConvertTo-Json -Depth 10
