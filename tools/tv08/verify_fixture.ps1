param(
    [Parameter(Mandatory = $false)]
    [string]$FixtureRoot = '',

    [Parameter(Mandatory = $false)]
    [switch]$Prepare,

    [Parameter(Mandatory = $false)]
    [switch]$RequireEdited
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($FixtureRoot)) {
    $FixtureRoot = $PSScriptRoot
}

if ($Prepare -and $RequireEdited) {
    throw 'Choose either -Prepare or -RequireEdited, not both.'
}

$resolvedRoot = (Resolve-Path -LiteralPath $FixtureRoot).Path
$baselineRoot = Join-Path $resolvedRoot 'baseline'
$sourceRoot = Join-Path $resolvedRoot 'source'
$workRoot = Join-Path $resolvedRoot 'work'
$baselinePsd = Join-Path $baselineRoot 'G-detail-03-validation-output.psd'
$baselineJpg = Join-Path $baselineRoot 'G-detail-03-validation-output.jpg'
$baselineEvidence = Join-Path $baselineRoot 'G-detail-03-validation-evidence.json'
$replacementJpg = Join-Path $sourceRoot '7Y9A3750.jpg'
$workingPsd = Join-Path $workRoot 'G-detail-03-TV08-working.psd'
$beforeEvidence = Join-Path $workRoot 'tv08-before.json'
$editedEvidence = Join-Path $workRoot 'tv08-editability-evidence.json'

$fixtureVersion = '0.2.3.1-tv08.2'
$markerText = 'TV08 EDIT OK'
$expectedBaselinePsdHash = 'DD997E7F47B8792744C4EBF57261A5109B50D195BEDFCC8B89FE836DCE7F6949'
$expectedBaselineJpgHash = 'D18AD1D8389289E06D9F0A66E544DA15D85F181F9D49F2229BA410A9245180A4'
$expectedBaselineEvidenceHash = '358EE8A940A6217BB7A8EF815D2246EEAC3BC3EEABE8E2151E3674E9603CFB0F'
$expectedReplacementHash = 'C04DDDD4C7802277D9996F347BA6FD4285987147941EED5A69638E0443696D57'
$expectedTypeLayerRecords = 16
$expectedSoldRecords = 2
$expectedPlldRecords = 2

if (-not ('Tv08BinaryInspector' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;

public sealed class Tv08PsdFacts
{
    public bool HasPsdHeader { get; set; }
    public int TypeLayerRecords { get; set; }
    public int SoldRecords { get; set; }
    public int PlldRecords { get; set; }
    public int BackMainAscii { get; set; }
    public int BackMainUtf16Be { get; set; }
    public int MarkerAscii { get; set; }
    public int MarkerUtf16Be { get; set; }
}

public static class Tv08BinaryInspector
{
    private static int Count(byte[] haystack, byte[] needle, int excludedStart, int excludedLength)
    {
        if (needle.Length == 0) return 0;
        int count = 0;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            int matchEnd = i + needle.Length;
            int excludedEnd = excludedStart + excludedLength;
            if (excludedStart >= 0 && i < excludedEnd && matchEnd > excludedStart)
            {
                i = excludedEnd - 1;
                continue;
            }
            int j = 0;
            for (; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) break;
            }
            if (j == needle.Length)
            {
                count++;
                i += needle.Length - 1;
            }
        }
        return count;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int startOffset)
    {
        if (needle.Length == 0) return startOffset;
        if (needle.Length > haystack.Length) return -1;

        int[] shifts = new int[256];
        for (int i = 0; i < shifts.Length; i++) shifts[i] = needle.Length;
        for (int i = 0; i < needle.Length - 1; i++)
        {
            shifts[needle[i]] = needle.Length - 1 - i;
        }

        int offset = startOffset;
        int limit = haystack.Length - needle.Length;
        while (offset <= limit)
        {
            int index = needle.Length - 1;
            while (index >= 0 && haystack[offset + index] == needle[index]) index--;
            if (index < 0) return offset;
            offset += shifts[haystack[offset + needle.Length - 1]];
        }
        return -1;
    }

    private static Tv08PsdFacts InspectBytes(byte[] bytes, string marker, int excludedStart, int excludedLength)
    {
        byte[] signature = Encoding.ASCII.GetBytes("8BPS");
        bool header = bytes.Length >= 6;
        for (int i = 0; header && i < signature.Length; i++)
        {
            if (bytes[i] != signature[i]) header = false;
        }
        header = header && bytes[4] == 0 && bytes[5] == 1;

        return new Tv08PsdFacts
        {
            HasPsdHeader = header,
            TypeLayerRecords = Count(bytes, Encoding.ASCII.GetBytes("TySh"), excludedStart, excludedLength),
            SoldRecords = Count(bytes, Encoding.ASCII.GetBytes("SoLd"), excludedStart, excludedLength),
            PlldRecords = Count(bytes, Encoding.ASCII.GetBytes("PlLd"), excludedStart, excludedLength),
            BackMainAscii = Count(bytes, Encoding.ASCII.GetBytes("back_main"), excludedStart, excludedLength),
            BackMainUtf16Be = Count(bytes, Encoding.BigEndianUnicode.GetBytes("back_main"), excludedStart, excludedLength),
            MarkerAscii = Count(bytes, Encoding.ASCII.GetBytes(marker), excludedStart, excludedLength),
            MarkerUtf16Be = Count(bytes, Encoding.BigEndianUnicode.GetBytes(marker), excludedStart, excludedLength)
        };
    }

    public static Tv08PsdFacts Inspect(string path, string marker)
    {
        return InspectBytes(File.ReadAllBytes(path), marker, -1, 0);
    }

    public static Tv08PsdFacts InspectExcludingPayload(string path, string payloadPath, string marker)
    {
        byte[] bytes = File.ReadAllBytes(path);
        byte[] payload = File.ReadAllBytes(payloadPath);
        int offset = IndexOf(bytes, payload, 0);
        if (offset < 0) throw new InvalidDataException("Payload is not embedded in the PSD.");
        return InspectBytes(bytes, marker, offset, payload.Length);
    }

    public static long IndexOfFile(string containerPath, string payloadPath)
    {
        return IndexOf(File.ReadAllBytes(containerPath), File.ReadAllBytes(payloadPath), 0);
    }

    public static int CountFileOccurrences(string containerPath, string payloadPath)
    {
        byte[] bytes = File.ReadAllBytes(containerPath);
        byte[] payload = File.ReadAllBytes(payloadPath);
        int count = 0;
        int offset = 0;
        while (offset <= bytes.Length - payload.Length)
        {
            int match = IndexOf(bytes, payload, offset);
            if (match < 0) break;
            count++;
            offset = match + payload.Length;
        }
        return count;
    }
}
'@
}

function Get-CheckedHash {
    param(
        [string]$Path,
        [string]$Expected,
        [string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Missing $Label file: $Path"
    }
    if ((Get-Item -LiteralPath $Path).Length -le 0) {
        throw "$Label file is empty: $Path"
    }
    $actual = Get-Sha256 -Path $Path
    if ($actual -ne $Expected) {
        throw "$Label SHA-256 does not match the approved fixture. Expected $Expected, got $actual."
    }
    return $actual
}

function Get-Sha256 {
    param([string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            return ([BitConverter]::ToString($sha256.ComputeHash($stream))).Replace('-', '')
        } finally {
            $sha256.Dispose()
        }
    } finally {
        $stream.Dispose()
    }
}

function Convert-Facts {
    param([object]$Facts)

    return [ordered]@{
        has_psd_header = [bool]$Facts.HasPsdHeader
        type_layer_records = [int]$Facts.TypeLayerRecords
        sold_records = [int]$Facts.SoldRecords
        plld_records = [int]$Facts.PlldRecords
        back_main_ascii = [int]$Facts.BackMainAscii
        back_main_utf16be = [int]$Facts.BackMainUtf16Be
        marker_ascii = [int]$Facts.MarkerAscii
        marker_utf16be = [int]$Facts.MarkerUtf16Be
    }
}

function Write-JsonAtomic {
    param(
        [object]$Value,
        [string]$Path
    )

    $temporaryPath = "$Path.tmp"
    $Value | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temporaryPath -Encoding UTF8
    Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
}

if (-not (Test-Path -LiteralPath $workRoot -PathType Container)) {
    New-Item -ItemType Directory -Path $workRoot | Out-Null
}

$baselinePsdHash = Get-CheckedHash -Path $baselinePsd -Expected $expectedBaselinePsdHash -Label 'baseline PSD'
$baselineJpgHash = Get-CheckedHash -Path $baselineJpg -Expected $expectedBaselineJpgHash -Label 'baseline JPG'
$baselineEvidenceHash = Get-CheckedHash -Path $baselineEvidence -Expected $expectedBaselineEvidenceHash -Label 'baseline evidence'
$replacementHash = Get-CheckedHash -Path $replacementJpg -Expected $expectedReplacementHash -Label 'replacement JPG'

$sourceEvidence = Get-Content -Raw -Encoding UTF8 -LiteralPath $baselineEvidence | ConvertFrom-Json
if (-not $sourceEvidence.ok -or $sourceEvidence.plugin_version -ne '0.2.3') {
    throw 'Baseline evidence is not the approved successful 0.2.3 result.'
}

$baselineFacts = [Tv08BinaryInspector]::Inspect($baselinePsd, $markerText)
if (-not $baselineFacts.HasPsdHeader) { throw 'Baseline file is not a supported PSD v1 file.' }
if ($baselineFacts.TypeLayerRecords -ne $expectedTypeLayerRecords) { throw 'Baseline PSD TypeLayer record count is unexpected.' }
if ($baselineFacts.SoldRecords -ne $expectedSoldRecords -or $baselineFacts.PlldRecords -ne $expectedPlldRecords) { throw 'Baseline PSD smart-object record count is unexpected.' }
if ($baselineFacts.BackMainAscii -lt 1 -or $baselineFacts.BackMainUtf16Be -lt 1) { throw 'Baseline PSD does not contain the approved back_main binding.' }
if ($baselineFacts.MarkerAscii -ne 0 -or $baselineFacts.MarkerUtf16Be -ne 0) { throw 'Baseline PSD already contains the TV08 marker.' }
if ([Tv08BinaryInspector]::IndexOfFile($baselinePsd, $replacementJpg) -ge 0) { throw 'Baseline PSD already embeds the replacement JPG.' }

$integrity = [ordered]@{
    baseline_psd = $baselinePsdHash
    baseline_jpg = $baselineJpgHash
    baseline_evidence = $baselineEvidenceHash
    replacement_jpg = $replacementHash
}

if ($Prepare) {
    if (Test-Path -LiteralPath $workingPsd) {
        throw 'Working PSD already exists. Use a newly extracted fixture folder for a clean retry.'
    }
    if (Test-Path -LiteralPath $beforeEvidence) {
        throw 'Preparation evidence already exists. Use a newly extracted fixture folder for a clean retry.'
    }

    Copy-Item -LiteralPath $baselinePsd -Destination $workingPsd
    $workingHash = Get-Sha256 -Path $workingPsd
    if ($workingHash -ne $expectedBaselinePsdHash) {
        throw 'Working PSD copy hash changed during preparation.'
    }

    $before = [ordered]@{
        schema = 'uxp-tv08-before/v1'
        fixture_version = $fixtureVersion
        prepared_at_utc = [DateTime]::UtcNow.ToString('o')
        integrity = $integrity
        working_psd = [ordered]@{
            path = $workingPsd
            sha256 = $workingHash
        }
        expected_manual_edit = [ordered]@{
            smart_object = 'back_main'
            replacement_file = '7Y9A3750.jpg'
            text_layer = 'upper-body comfort text'
            text_marker = $markerText
        }
    }
    Write-JsonAtomic -Value $before -Path $beforeEvidence

    [ordered]@{
        fixture_ready = $true
        prepared = $true
        fixture_root = $resolvedRoot
        working_psd = $workingPsd
        before_evidence = $beforeEvidence
        integrity = $integrity
        next = 'Follow README.md, then run VERIFY-TV08.bat after save, close, and reopen.'
    } | ConvertTo-Json -Depth 10
    exit 0
}

if ($RequireEdited) {
    if (-not (Test-Path -LiteralPath $beforeEvidence -PathType Leaf)) {
        throw 'Missing tv08-before.json. Run START-TV08.bat before editing.'
    }
    if (-not (Test-Path -LiteralPath $workingPsd -PathType Leaf)) {
        throw 'Missing working PSD. Run START-TV08.bat before editing.'
    }

    $before = Get-Content -Raw -Encoding UTF8 -LiteralPath $beforeEvidence | ConvertFrom-Json
    if ($before.schema -ne 'uxp-tv08-before/v1' -or $before.working_psd.sha256 -ne $expectedBaselinePsdHash) {
        throw 'Preparation evidence is inconsistent with this fixture.'
    }

    $workingHash = Get-Sha256 -Path $workingPsd
    if ($workingHash -eq $expectedBaselinePsdHash) {
        throw 'Working PSD hash has not changed. Complete both Photoshop edits and save before verification.'
    }

    $editedRawFacts = [Tv08BinaryInspector]::Inspect($workingPsd, $markerText)
    if (-not $editedRawFacts.HasPsdHeader) { throw 'Edited working file is not a supported PSD v1 file.' }
    $replacementOffset = [Tv08BinaryInspector]::IndexOfFile($workingPsd, $replacementJpg)
    if ($replacementOffset -lt 0) { throw 'Edited PSD does not embed the approved 7Y9A3750.jpg payload.' }
    $replacementOccurrences = [Tv08BinaryInspector]::CountFileOccurrences($workingPsd, $replacementJpg)
    if ($replacementOccurrences -ne 1) { throw 'Edited PSD must contain exactly one complete 7Y9A3750.jpg payload.' }

    $editedStructureFacts = [Tv08BinaryInspector]::InspectExcludingPayload($workingPsd, $replacementJpg, $markerText)
    if ($editedStructureFacts.TypeLayerRecords -ne $baselineFacts.TypeLayerRecords) { throw 'Edited PSD did not preserve the TypeLayer record count outside the JPG payload.' }
    if ($editedStructureFacts.SoldRecords -ne $baselineFacts.SoldRecords -or $editedStructureFacts.PlldRecords -ne $baselineFacts.PlldRecords) { throw 'Edited PSD did not preserve the smart-object record count outside the JPG payload.' }
    if ($editedStructureFacts.BackMainAscii -lt 1 -or $editedStructureFacts.BackMainUtf16Be -lt 1) { throw 'Edited PSD no longer contains the back_main binding.' }
    if ($editedStructureFacts.MarkerUtf16Be -lt 1) { throw 'Edited PSD does not contain the TV08 EDIT OK text payload. Edit the text contents, not only the layer name.' }

    $result = [ordered]@{
        schema = 'uxp-tv08-editability-evidence/v1'
        fixture_version = $fixtureVersion
        checked_at_utc = [DateTime]::UtcNow.ToString('o')
        automated_checks_passed = $true
        tv08_final_status = 'pending_manual_review'
        fixture_root = $resolvedRoot
        integrity = $integrity
        working_psd = [ordered]@{
            path = $workingPsd
            sha256_before = $expectedBaselinePsdHash
            sha256_after = $workingHash
            changed = $true
        }
        structural_checks = [ordered]@{
            before = Convert-Facts -Facts $baselineFacts
            after_raw = Convert-Facts -Facts $editedRawFacts
            after_outside_replacement_payload = Convert-Facts -Facts $editedStructureFacts
            replacement_payload_filename = '7Y9A3750.jpg'
            replacement_payload_sha256 = $replacementHash
            replacement_payload_offset = $replacementOffset
            replacement_payload_length = (Get-Item -LiteralPath $replacementJpg).Length
            replacement_payload_occurrences = $replacementOccurrences
            text_marker = $markerText
        }
        manual_visual_confirmation_required = $true
        pending_manual_checks = @(
            'The same working PSD was saved, closed, and reopened in Photoshop.'
            'The reopened canvas shows the front-view replacement and visible TV08 EDIT OK text.'
            'back_main still has a smart-object icon and can be opened.'
            'The edited text still has a Type-layer icon.'
        )
        return_items = @(
            'G-detail-03-TV08-working.psd'
            'tv08-editability-evidence.json'
            'One screenshot captured after reopening, including canvas and Layers panel.'
        )
    }
    Write-JsonAtomic -Value $result -Path $editedEvidence
    $result | ConvertTo-Json -Depth 10
    exit 0
}

[ordered]@{
    fixture_ready = $true
    prepared = $false
    fixture_version = $fixtureVersion
    fixture_root = $resolvedRoot
    integrity = $integrity
    baseline_facts = Convert-Facts -Facts $baselineFacts
    next = 'Run START-TV08.bat on the Photoshop computer.'
} | ConvertTo-Json -Depth 10
