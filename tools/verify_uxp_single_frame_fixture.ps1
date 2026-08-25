param(
    [Parameter(Mandatory = $false)]
    [string]$FixtureRoot = $PSScriptRoot,

    [Parameter(Mandatory = $false)]
    [switch]$RequireOutputs
)

$ErrorActionPreference = 'Stop'

$resolvedRoot = (Resolve-Path -LiteralPath $FixtureRoot).Path
$detailWord = ([string][char]0x8BE6) + ([string][char]0x60C5)
$templateName = "G-$detailWord-03-validation-copy.psd"
$outputStem = "G-$detailWord-03-validation-output"
$templatePath = Join-Path $resolvedRoot "template/$templateName"
$sourcePath = Join-Path $resolvedRoot 'source/7Y9A3760.jpg'
$planPath = Join-Path $resolvedRoot 'single-frame-g03-render-plan.json'
$outputRoot = Join-Path $resolvedRoot 'output'
$expectedTemplateHash = 'B94E7458A33F645E322F30EFDE675CCB268BE5A72CA2FF4B7618F241EED14A35'
$expectedSourceHash = '04720404A97FBE6DFD6EB138946C129545F37020F81346D98AA0616668C0B0DB'
$expectedJpgProfile = 'sRGB IEC61966-2.1'
$expectedIccProfileBytes = 588
$expectedIccProfileHash = '14FB97CEB79AB10B4A72C839A16A334CAC46E7991C6A7F46DE4E56136675920D'

function Test-BytesAt {
    param(
        [byte[]]$Bytes,
        [int]$Offset,
        [byte[]]$Expected
    )

    if ($Offset -lt 0 -or ($Offset + $Expected.Length) -gt $Bytes.Length) { return $false }
    for ($index = 0; $index -lt $Expected.Length; $index++) {
        if ($Bytes[$Offset + $index] -ne $Expected[$index]) { return $false }
    }
    return $true
}

function Get-JpegIccProfile {
    param([string]$Path)

    [byte[]]$bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 4 -or $bytes[0] -ne 0xFF -or $bytes[1] -ne 0xD8) {
        throw 'Generated JPG has an invalid start marker.'
    }

    [byte[]]$iccSignature = @(0x49, 0x43, 0x43, 0x5F, 0x50, 0x52, 0x4F, 0x46, 0x49, 0x4C, 0x45, 0x00)
    $chunks = @{}
    $expectedChunkCount = $null
    $offset = 2

    while ($offset -lt $bytes.Length) {
        if ($bytes[$offset] -ne 0xFF) { throw 'Generated JPG has an invalid marker sequence before scan data.' }
        while ($offset -lt $bytes.Length -and $bytes[$offset] -eq 0xFF) { $offset++ }
        if ($offset -ge $bytes.Length) { throw 'Generated JPG ends in a truncated marker.' }

        $marker = $bytes[$offset]
        $offset++
        if ($marker -eq 0xDA -or $marker -eq 0xD9) { break }
        if ($marker -eq 0x00 -or $marker -eq 0x01 -or ($marker -ge 0xD0 -and $marker -le 0xD7)) { continue }
        if (($offset + 2) -gt $bytes.Length) { throw 'Generated JPG has a truncated segment length.' }

        $segmentLength = ([int]$bytes[$offset] -shl 8) -bor [int]$bytes[$offset + 1]
        if ($segmentLength -lt 2) { throw 'Generated JPG has an invalid segment length.' }
        $payloadStart = $offset + 2
        $segmentEnd = $offset + $segmentLength
        if ($segmentEnd -gt $bytes.Length) { throw 'Generated JPG segment exceeds the file bounds.' }

        if ($marker -eq 0xE2 -and ($segmentEnd - $payloadStart) -ge 14 -and (Test-BytesAt -Bytes $bytes -Offset $payloadStart -Expected $iccSignature)) {
            $sequence = [int]$bytes[$payloadStart + 12]
            $chunkCount = [int]$bytes[$payloadStart + 13]
            if ($sequence -lt 1 -or $chunkCount -lt 1 -or $sequence -gt $chunkCount) { throw 'Generated JPG has an invalid ICC chunk sequence.' }
            if ($null -eq $expectedChunkCount) { $expectedChunkCount = $chunkCount }
            if ($expectedChunkCount -ne $chunkCount -or $chunks.ContainsKey($sequence)) { throw 'Generated JPG has inconsistent ICC chunks.' }
            $chunkLength = $segmentEnd - ($payloadStart + 14)
            $chunk = New-Object byte[] $chunkLength
            [Array]::Copy($bytes, $payloadStart + 14, $chunk, 0, $chunkLength)
            $chunks[$sequence] = $chunk
        }

        $offset = $segmentEnd
    }

    if ($null -eq $expectedChunkCount) { return $null }
    if ($chunks.Count -ne $expectedChunkCount) { throw 'Generated JPG has an incomplete ICC profile.' }

    $profileLength = 0
    for ($sequence = 1; $sequence -le $expectedChunkCount; $sequence++) {
        if (-not $chunks.ContainsKey($sequence)) { throw 'Generated JPG has an incomplete ICC profile.' }
        $profileLength += $chunks[$sequence].Length
    }
    $profile = New-Object byte[] $profileLength
    $profileOffset = 0
    for ($sequence = 1; $sequence -le $expectedChunkCount; $sequence++) {
        $chunk = [byte[]]$chunks[$sequence]
        [Array]::Copy($chunk, 0, $profile, $profileOffset, $chunk.Length)
        $profileOffset += $chunk.Length
    }
    return ,$profile
}

function Get-ByteArraySha256 {
    param([byte[]]$Bytes)

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha256.ComputeHash($Bytes))).Replace('-', '')
    } finally {
        $sha256.Dispose()
    }
}

function Get-BigEndianUInt32 {
    param(
        [byte[]]$Bytes,
        [int]$Offset
    )

    return (([uint32]$Bytes[$Offset] -shl 24) -bor ([uint32]$Bytes[$Offset + 1] -shl 16) -bor ([uint32]$Bytes[$Offset + 2] -shl 8) -bor [uint32]$Bytes[$Offset + 3])
}

function Test-IccIsSrgb {
    param([byte[]]$Profile)

    if ($Profile.Length -lt 132) { return $false }
    if ((Get-BigEndianUInt32 -Bytes $Profile -Offset 0) -ne $Profile.Length) { return $false }
    if (-not (Test-BytesAt -Bytes $Profile -Offset 16 -Expected ([byte[]]@(0x52, 0x47, 0x42, 0x20)))) { return $false }
    if (-not (Test-BytesAt -Bytes $Profile -Offset 20 -Expected ([byte[]]@(0x58, 0x59, 0x5A, 0x20)))) { return $false }
    if (-not (Test-BytesAt -Bytes $Profile -Offset 36 -Expected ([byte[]]@(0x61, 0x63, 0x73, 0x70)))) { return $false }

    [byte[]]$asciiSrgb = @(0x73, 0x52, 0x47, 0x42)
    [byte[]]$utf16BeSrgb = @(0x00, 0x73, 0x00, 0x52, 0x00, 0x47, 0x00, 0x42)
    for ($offset = 0; $offset -lt $Profile.Length; $offset++) {
        if ((Test-BytesAt -Bytes $Profile -Offset $offset -Expected $asciiSrgb) -or (Test-BytesAt -Bytes $Profile -Offset $offset -Expected $utf16BeSrgb)) {
            return $true
        }
    }
    return $false
}

foreach ($requiredPath in @($templatePath, $sourcePath, $planPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Missing fixture file: $requiredPath"
    }
}

$templateHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $templatePath).Hash
$sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourcePath).Hash
if ($templateHash -ne $expectedTemplateHash) { throw 'Disposable PSD copy hash does not match the approved G-detail-03 fixture.' }
if ($sourceHash -ne $expectedSourceHash) { throw 'Source JPG hash does not match the approved back-view fixture.' }

$plan = Get-Content -Raw -Encoding UTF8 -LiteralPath $planPath | ConvertFrom-Json
if ($plan.validation.mode -ne 'single_frame_copy') { throw 'Fixture RenderPlan is not the single-frame copy plan.' }
if ($plan.pages.Count -ne 1 -or $plan.pages[0].frames.Count -ne 1) { throw 'Fixture RenderPlan must contain exactly one page and one frame.' }
if ($plan.pages[0].template_sha256.ToUpperInvariant() -ne $expectedTemplateHash) { throw 'Fixture RenderPlan template hash is inconsistent.' }
if ($plan.pages[0].frames[0].source.sha256.ToUpperInvariant() -ne $expectedSourceHash) { throw 'Fixture RenderPlan source hash is inconsistent.' }
if ($plan.pages[0].frames[0].target.layer_path_order -ne 'psd_bottom_to_top') { throw 'Fixture RenderPlan layer path order is inconsistent.' }
if ($plan.output.jpg.color_profile -ne $expectedJpgProfile) { throw 'Fixture RenderPlan JPG profile is not explicit sRGB.' }
if ($plan.output.jpg.conversion_intent -ne 'relative_colorimetric') { throw 'Fixture RenderPlan JPG conversion intent is incorrect.' }
if (-not $plan.output.jpg.black_point_compensation -or -not $plan.output.jpg.dither -or -not $plan.output.jpg.embed_color_profile) { throw 'Fixture RenderPlan JPG sRGB conversion flags are incomplete.' }

$result = [ordered]@{
    passed = $true
    fixture_root = $resolvedRoot
    input_hashes = [ordered]@{
        template_copy = $templateHash
        source_jpg = $sourceHash
    }
    outputs_required = [bool]$RequireOutputs
}

if ($RequireOutputs) {
    $psdPath = Join-Path $outputRoot "$outputStem.psd"
    $jpgPath = Join-Path $outputRoot "$outputStem.jpg"
    $evidencePath = Join-Path $outputRoot "G-$detailWord-03-validation-evidence.json"
    foreach ($requiredOutput in @($psdPath, $jpgPath, $evidencePath)) {
        if (-not (Test-Path -LiteralPath $requiredOutput -PathType Leaf)) {
            throw "Missing validation output: $requiredOutput"
        }
        if ((Get-Item -LiteralPath $requiredOutput).Length -le 0) {
            throw "Validation output is empty: $requiredOutput"
        }
    }

    $evidence = Get-Content -Raw -Encoding UTF8 -LiteralPath $evidencePath | ConvertFrom-Json
    if (-not $evidence.ok) { throw 'UXP evidence does not report success.' }
    if ($evidence.schema -ne 'uxp-validation-evidence/v2') { throw 'UXP evidence schema is not v2.' }
    if ($evidence.plugin_version -ne '0.2.3') { throw 'UXP evidence was not produced by plugin 0.2.3.' }
    if ($evidence.job_id -ne 'uxp-g03-single-frame-001') { throw 'UXP evidence job id is incorrect.' }
    if ($evidence.binding.layer_path -ne '2') { throw 'UXP evidence layer binding is incorrect.' }
    if ($evidence.binding.layer_path_order -ne 'psd_bottom_to_top') { throw 'UXP evidence layer path order is incorrect.' }
    if (($evidence.binding.registered_frame -join ',') -ne '0,0,790,1252') { throw 'UXP evidence registered frame is incorrect.' }
    if ($evidence.output.source_template_saved) { throw 'UXP evidence indicates that the input PSD copy was saved.' }
    if ($evidence.output.jpg_color_profile -ne $expectedJpgProfile) { throw 'UXP evidence does not report the required sRGB JPG profile.' }
    if (-not $evidence.output.jpg_profile.embedding_requested) { throw 'UXP evidence does not report that profile embedding was requested.' }
    if (-not $evidence.output.jpg_profile.embedding_verified) { throw 'UXP evidence does not report a verified JPG profile.' }
    if ($evidence.output.jpg_profile.profile_bytes -lt 128) { throw 'UXP evidence reports an invalid ICC profile length.' }
    if ($evidence.output.jpg_profile.profile_description -notmatch 'sRGB') { throw 'UXP evidence does not identify the embedded profile as sRGB.' }
    if ($evidence.photoshop.jpg_color.destination_profile -ne $expectedJpgProfile) { throw 'Photoshop evidence has the wrong JPG destination profile.' }
    if ($evidence.photoshop.jpg_color.intent -ne 'relative_colorimetric') { throw 'Photoshop evidence has the wrong JPG conversion intent.' }
    if (-not $evidence.photoshop.jpg_color.black_point_compensation -or -not $evidence.photoshop.jpg_color.dither -or -not $evidence.photoshop.jpg_color.embedding_requested) { throw 'Photoshop evidence has incomplete JPG conversion flags.' }
    if (-not $evidence.photoshop.jpg_color.document_profile_after.assigned -or $evidence.photoshop.jpg_color.document_profile_after.name -notmatch 'sRGB') { throw 'Photoshop did not report an assigned sRGB document profile after conversion.' }
    if ($evidence.jpg_icc.adapter_injected -ne $evidence.output.jpg_profile.adapter_injected) { throw 'UXP evidence is inconsistent about ICC adapter injection.' }

    [byte[]]$iccProfile = Get-JpegIccProfile -Path $jpgPath
    if ($null -eq $iccProfile) { throw 'Generated JPG has no ICC_PROFILE APP2 segment.' }
    if (-not (Test-IccIsSrgb -Profile $iccProfile)) { throw 'Generated JPG ICC profile is incomplete, malformed, or not identified as sRGB.' }
    if ($iccProfile.Length -ne $evidence.output.jpg_profile.profile_bytes) { throw 'Generated JPG ICC profile length does not match UXP evidence.' }
    $iccProfileHash = Get-ByteArraySha256 -Bytes $iccProfile
    $actualCanonicalProfile = $iccProfile.Length -eq $expectedIccProfileBytes -and $iccProfileHash -eq $expectedIccProfileHash
    if ([bool]$evidence.output.jpg_profile.canonical_profile -ne $actualCanonicalProfile) { throw 'UXP evidence is inconsistent about the canonical ICC profile identity.' }
    if ($evidence.output.jpg_profile.adapter_injected) {
        if (-not $actualCanonicalProfile) { throw 'Adapter-injected JPG does not contain the approved canonical sRGB profile.' }
        if ($evidence.output.jpg_profile.profile_sha256 -ne $expectedIccProfileHash) { throw 'UXP evidence reports an unexpected injected ICC profile hash.' }
    } else {
        if (-not $evidence.output.jpg_profile.photoshop_embedded_before_postprocess) { throw 'JPG profile was neither embedded by Photoshop nor injected by the adapter.' }
    }

    $result.outputs = [ordered]@{
        editable_psd = (Get-FileHash -Algorithm SHA256 -LiteralPath $psdPath).Hash
        jpg = (Get-FileHash -Algorithm SHA256 -LiteralPath $jpgPath).Hash
        evidence = (Get-FileHash -Algorithm SHA256 -LiteralPath $evidencePath).Hash
    }
    $result.jpg_color = [ordered]@{
        profile = $evidence.output.jpg_color_profile
        photoshop_embedded_before_postprocess = [bool]$evidence.output.jpg_profile.photoshop_embedded_before_postprocess
        adapter_injected = [bool]$evidence.output.jpg_profile.adapter_injected
        embedding_verified = $true
        icc_profile_bytes = $iccProfile.Length
        icc_profile_sha256 = $iccProfileHash
    }
}

$result | ConvertTo-Json -Depth 8
