const SRGB_PROFILE_BASE64 = "AAACTGxjbXMEQAAAbW50clJHQiBYWVogB+oACAAXAA4AKQAXYWNzcE1TRlQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAPbWAAEAAAAA0y1sY21zAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAALZGVzYwAAAQgAAAA2Y3BydAAAAUAAAABMd3RwdAAAAYwAAAAUY2hhZAAAAaAAAAAsclhZWgAAAcwAAAAUYlhZWgAAAeAAAAAUZ1hZWgAAAfQAAAAUclRSQwAAAggAAAAgZ1RSQwAAAggAAAAgYlRSQwAAAggAAAAgY2hybQAAAigAAAAkbWx1YwAAAAAAAAABAAAADGVuVVMAAAAaAAAAHABzAFIARwBCACAAYgB1AGkAbAB0AC0AaQBuAABtbHVjAAAAAAAAAAEAAAAMZW5VUwAAADAAAAAcAE4AbwAgAGMAbwBwAHkAcgBpAGcAaAB0ACwAIAB1AHMAZQAgAGYAcgBlAGUAbAB5WFlaIAAAAAAAAPbWAAEAAAAA0y1zZjMyAAAAAAABDEIAAAXe///zJQAAB5MAAP2Q///7of///aIAAAPcAADAblhZWiAAAAAAAABvoAAAOPUAAAOQWFlaIAAAAAAAACSfAAAPhAAAtsNYWVogAAAAAAAAYpcAALeHAAAY2XBhcmEAAAAAAAMAAAACZmYAAPKnAAANWQAAE9AAAApbY2hybQAAAAAAAwAAAACj1wAAVHsAAEzNAACZmgAAJmYAAA9c";
const SRGB_PROFILE_SHA256 = "14FB97CEB79AB10B4A72C839A16A334CAC46E7991C6A7F46DE4E56136675920D";
const BASE64_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
const ICC_SIGNATURE = [0x49, 0x43, 0x43, 0x5f, 0x50, 0x52, 0x4f, 0x46, 0x49, 0x4c, 0x45, 0x00];
const MAX_ICC_CHUNK_BYTES = 65519;

function decodeBase64(value) {
  const clean = String(value).replace(/\s/g, "");
  const output = [];
  let buffer = 0;
  let bits = 0;
  for (let index = 0; index < clean.length; index += 1) {
    const character = clean[index];
    if (character === "=") break;
    const decoded = BASE64_ALPHABET.indexOf(character);
    if (decoded < 0) throw new Error("Invalid base64 profile data.");
    buffer = (buffer << 6) | decoded;
    bits += 6;
    if (bits >= 8) {
      bits -= 8;
      output.push((buffer >> bits) & 0xff);
    }
  }
  return new Uint8Array(output);
}

function asBytes(value) {
  if (value instanceof Uint8Array) return value;
  if (value instanceof ArrayBuffer) return new Uint8Array(value);
  if (ArrayBuffer.isView(value)) return new Uint8Array(value.buffer, value.byteOffset, value.byteLength);
  throw new Error("Expected binary JPEG data.");
}

function readUint16(bytes, offset) {
  return (bytes[offset] << 8) | bytes[offset + 1];
}

function readUint32(bytes, offset) {
  return (((bytes[offset] << 24) >>> 0) + (bytes[offset + 1] << 16) + (bytes[offset + 2] << 8) + bytes[offset + 3]) >>> 0;
}

function bytesEqual(left, right) {
  if (left.length !== right.length) return false;
  for (let index = 0; index < left.length; index += 1) {
    if (left[index] !== right[index]) return false;
  }
  return true;
}

function bytesMatch(bytes, offset, expected) {
  if (offset + expected.length > bytes.length) return false;
  for (let index = 0; index < expected.length; index += 1) {
    if (bytes[offset + index] !== expected[index]) return false;
  }
  return true;
}

function concatBytes(parts) {
  const length = parts.reduce((total, part) => total + part.length, 0);
  const output = new Uint8Array(length);
  let offset = 0;
  for (const part of parts) {
    output.set(part, offset);
    offset += part.length;
  }
  return output;
}

function readAscii(bytes, offset, length) {
  let value = "";
  for (let index = 0; index < length; index += 1) value += String.fromCharCode(bytes[offset + index]);
  return value;
}

function readUtf16Be(bytes, offset, length) {
  let value = "";
  for (let index = 0; index + 1 < length; index += 2) {
    value += String.fromCharCode((bytes[offset + index] << 8) | bytes[offset + index + 1]);
  }
  return value.replace(/\0+$/g, "").trim();
}

function findIccTag(profile, signature) {
  if (profile.length < 132) return null;
  const tagCount = readUint32(profile, 128);
  if (tagCount > Math.floor((profile.length - 132) / 12)) return null;
  for (let index = 0; index < tagCount; index += 1) {
    const recordOffset = 132 + index * 12;
    if (readAscii(profile, recordOffset, 4) !== signature) continue;
    const dataOffset = readUint32(profile, recordOffset + 4);
    const dataLength = readUint32(profile, recordOffset + 8);
    if (dataOffset > profile.length || dataLength > profile.length - dataOffset) return null;
    return profile.subarray(dataOffset, dataOffset + dataLength);
  }
  return null;
}

function readIccDescription(profile) {
  const tag = findIccTag(profile, "desc");
  if (!tag || tag.length < 12) return null;
  const type = readAscii(tag, 0, 4);
  if (type === "desc") {
    const length = readUint32(tag, 8);
    if (length < 1 || length > tag.length - 12) return null;
    return readAscii(tag, 12, length).replace(/\0+$/g, "").trim();
  }
  if (type === "mluc") {
    if (tag.length < 28) return null;
    const recordCount = readUint32(tag, 8);
    const recordSize = readUint32(tag, 12);
    if (recordCount < 1 || recordSize < 12 || 16 + recordCount * recordSize > tag.length) return null;
    const textLength = readUint32(tag, 20);
    const textOffset = readUint32(tag, 24);
    if (textLength < 2 || textOffset > tag.length || textLength > tag.length - textOffset) return null;
    return readUtf16Be(tag, textOffset, textLength);
  }
  return null;
}

function parseJpegHeader(value) {
  const bytes = asBytes(value);
  if (bytes.length < 4 || bytes[0] !== 0xff || bytes[1] !== 0xd8) throw new Error("Invalid JPEG start marker.");

  const segments = [];
  let offset = 2;
  while (offset < bytes.length) {
    const markerStart = offset;
    if (bytes[offset] !== 0xff) throw new Error("Invalid JPEG marker sequence before scan data.");
    while (offset < bytes.length && bytes[offset] === 0xff) offset += 1;
    if (offset >= bytes.length) throw new Error("Truncated JPEG marker.");

    const marker = bytes[offset];
    if (marker === 0xda || marker === 0xd9) return { bytes, segments, tail_start: markerStart };
    if (marker === 0x00 || marker === 0xd8 || marker === 0x01 || (marker >= 0xd0 && marker <= 0xd7)) {
      offset += 1;
      segments.push({ marker, start: markerStart, end: offset, is_icc: false });
      continue;
    }

    if (offset + 2 >= bytes.length) throw new Error("Truncated JPEG segment length.");
    const segmentLength = readUint16(bytes, offset + 1);
    if (segmentLength < 2) throw new Error("Invalid JPEG segment length.");
    const segmentEnd = offset + 1 + segmentLength;
    if (segmentEnd > bytes.length) throw new Error("JPEG segment exceeds file bounds.");
    const payloadStart = offset + 3;
    const isIcc = marker === 0xe2 && bytesMatch(bytes, payloadStart, ICC_SIGNATURE);
    segments.push({ marker, start: markerStart, end: segmentEnd, payload_start: payloadStart, is_icc: isIcc });
    offset = segmentEnd;
  }

  throw new Error("JPEG has no scan or end marker.");
}

function buildIccSegments(profileBytes) {
  const profile = asBytes(profileBytes);
  const count = Math.ceil(profile.length / MAX_ICC_CHUNK_BYTES);
  if (count < 1 || count > 255) throw new Error("ICC profile requires an unsupported number of JPEG segments.");
  const segments = [];
  for (let index = 0; index < count; index += 1) {
    const chunk = profile.subarray(index * MAX_ICC_CHUNK_BYTES, Math.min(profile.length, (index + 1) * MAX_ICC_CHUNK_BYTES));
    const payloadLength = ICC_SIGNATURE.length + 2 + chunk.length;
    const segment = new Uint8Array(4 + payloadLength);
    segment[0] = 0xff;
    segment[1] = 0xe2;
    const declaredLength = payloadLength + 2;
    segment[2] = (declaredLength >> 8) & 0xff;
    segment[3] = declaredLength & 0xff;
    segment.set(ICC_SIGNATURE, 4);
    segment[4 + ICC_SIGNATURE.length] = index + 1;
    segment[5 + ICC_SIGNATURE.length] = count;
    segment.set(chunk, 6 + ICC_SIGNATURE.length);
    segments.push(segment);
  }
  return segments;
}

function extractIccProfile(value) {
  const parsed = parseJpegHeader(value);
  const chunks = [];
  let expectedCount = null;
  for (const segment of parsed.segments) {
    if (!segment.is_icc) continue;
    const sequenceOffset = segment.payload_start + ICC_SIGNATURE.length;
    const sequence = parsed.bytes[sequenceOffset];
    const count = parsed.bytes[sequenceOffset + 1];
    if (!sequence || !count || sequence > count) throw new Error("Invalid JPEG ICC segment sequence.");
    if (expectedCount === null) expectedCount = count;
    if (expectedCount !== count || chunks[sequence - 1]) throw new Error("Inconsistent JPEG ICC segments.");
    chunks[sequence - 1] = parsed.bytes.slice(sequenceOffset + 2, segment.end);
  }
  if (expectedCount === null) return null;
  if (chunks.length !== expectedCount || chunks.some((chunk) => !chunk)) throw new Error("Incomplete JPEG ICC profile.");
  return concatBytes(chunks);
}

function inspectIccProfile(profileBytes) {
  if (!profileBytes) return { present: false, header_valid: false, valid_srgb: false, canonical_srgb: false, profile_bytes: 0 };
  const profile = asBytes(profileBytes);
  const declaredLength = profile.length >= 4 ? readUint32(profile, 0) : 0;
  const headerValid = profile.length >= 128 && declaredLength === profile.length && bytesMatch(profile, 36, [0x61, 0x63, 0x73, 0x70]);
  const rgbProfile = headerValid && bytesMatch(profile, 16, [0x52, 0x47, 0x42, 0x20]);
  const xyzPcs = headerValid && bytesMatch(profile, 20, [0x58, 0x59, 0x5a, 0x20]);
  const description = headerValid ? readIccDescription(profile) : null;
  const srgbDescription = Boolean(description && /srgb/i.test(description));
  return {
    present: true,
    header_valid: headerValid,
    rgb_profile: rgbProfile,
    xyz_pcs: xyzPcs,
    description,
    srgb_description: srgbDescription,
    valid_srgb: headerValid && rgbProfile && xyzPcs && srgbDescription,
    canonical_srgb: headerValid && rgbProfile && xyzPcs && bytesEqual(profile, SRGB_PROFILE_BYTES),
    profile_bytes: profile.length,
    canonical_profile_sha256: SRGB_PROFILE_SHA256
  };
}

function inspectJpegIcc(value) {
  return inspectIccProfile(extractIccProfile(value));
}

function ensureJpegHasSrgbProfile(value) {
  const parsed = parseJpegHeader(value);
  const before = inspectJpegIcc(parsed.bytes);
  if (before.present) {
    if (!before.valid_srgb) throw new Error("Existing JPEG ICC profile is not a valid sRGB profile.");
    return { jpeg: parsed.bytes, modified: false, before, after: before };
  }

  const output = [parsed.bytes.slice(0, 2)];
  const iccSegments = buildIccSegments(SRGB_PROFILE_BYTES);
  let inserted = false;
  for (const segment of parsed.segments) {
    if (segment.is_icc) continue;
    if (!inserted && segment.marker !== 0xe0 && segment.marker !== 0xe1) {
      output.push(...iccSegments);
      inserted = true;
    }
    output.push(parsed.bytes.slice(segment.start, segment.end));
  }
  if (!inserted) output.push(...iccSegments);
  output.push(parsed.bytes.slice(parsed.tail_start));

  const jpeg = concatBytes(output);
  const after = inspectJpegIcc(jpeg);
  if (!after.canonical_srgb) throw new Error("Canonical sRGB profile injection could not be verified.");
  return { jpeg, modified: true, before, after };
}

const SRGB_PROFILE_BYTES = decodeBase64(SRGB_PROFILE_BASE64);
const builtInProfile = inspectIccProfile(SRGB_PROFILE_BYTES);
if (!builtInProfile.canonical_srgb) throw new Error("Bundled sRGB profile is invalid.");

module.exports = {
  SRGB_PROFILE_BYTES,
  SRGB_PROFILE_SHA256,
  inspectJpegIcc,
  ensureJpegHasSrgbProfile
};
