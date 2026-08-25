const assert = require("assert");
const crypto = require("crypto");
const { SRGB_PROFILE_BYTES, SRGB_PROFILE_SHA256, inspectJpegIcc, ensureJpegHasSrgbProfile } = require("../uxp-validation-plugin/jpeg-icc");

const source = new Uint8Array([
  0xff, 0xd8,
  0xff, 0xe0, 0x00, 0x04, 0x00, 0x00,
  0xff, 0xe1, 0x00, 0x04, 0x01, 0x02,
  0xff, 0xda, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3f, 0x00,
  0x12, 0x34, 0x56, 0xff, 0xd9
]);
const scanTail = source.slice(14);

function findSequence(bytes, expected) {
  for (let offset = 0; offset <= bytes.length - expected.length; offset += 1) {
    let matches = true;
    for (let index = 0; index < expected.length; index += 1) {
      if (bytes[offset + index] !== expected[index]) {
        matches = false;
        break;
      }
    }
    if (matches) return offset;
  }
  return -1;
}

assert.strictEqual(SRGB_PROFILE_BYTES.length, 588);
assert.strictEqual(SRGB_PROFILE_SHA256, "14FB97CEB79AB10B4A72C839A16A334CAC46E7991C6A7F46DE4E56136675920D");
assert.strictEqual(
  crypto.createHash("sha256").update(Buffer.from(SRGB_PROFILE_BYTES)).digest("hex").toUpperCase(),
  SRGB_PROFILE_SHA256
);
assert.strictEqual(inspectJpegIcc(source).present, false);

const tagged = ensureJpegHasSrgbProfile(source);
assert.strictEqual(tagged.modified, true);
assert.strictEqual(tagged.after.header_valid, true);
assert.strictEqual(tagged.after.rgb_profile, true);
assert.strictEqual(tagged.after.xyz_pcs, true);
assert.strictEqual(tagged.after.description, "sRGB built-in");
assert.strictEqual(tagged.after.valid_srgb, true);
assert.strictEqual(tagged.after.canonical_srgb, true);
assert.deepStrictEqual(Array.from(tagged.jpeg.slice(-5)), Array.from(source.slice(-5)));

const iccSignatureOffset = findSequence(tagged.jpeg, new Uint8Array([0x49, 0x43, 0x43, 0x5f, 0x50, 0x52, 0x4f, 0x46, 0x49, 0x4c, 0x45, 0x00]));
assert(iccSignatureOffset >= 0);
const profileOffset = iccSignatureOffset + 14;

const alternateValidSrgb = tagged.jpeg.slice();
alternateValidSrgb[profileOffset + 35] ^= 0x01;
assert.strictEqual(inspectJpegIcc(alternateValidSrgb).valid_srgb, true);
assert.strictEqual(inspectJpegIcc(alternateValidSrgb).canonical_srgb, false);
const preserved = ensureJpegHasSrgbProfile(alternateValidSrgb);
assert.strictEqual(preserved.modified, false);
assert.deepStrictEqual(Array.from(preserved.jpeg), Array.from(alternateValidSrgb));

const invalidProfile = tagged.jpeg.slice();
invalidProfile[profileOffset + 36] ^= 0x01;
assert.strictEqual(inspectJpegIcc(invalidProfile).header_valid, false);
assert.throws(() => ensureJpegHasSrgbProfile(invalidProfile), /not a valid sRGB profile/);
assert.deepStrictEqual(Array.from(tagged.jpeg.slice(-scanTail.length)), Array.from(scanTail));

const retagged = ensureJpegHasSrgbProfile(tagged.jpeg);
assert.strictEqual(retagged.modified, false);
assert.deepStrictEqual(Array.from(retagged.jpeg), Array.from(tagged.jpeg));

assert.throws(() => ensureJpegHasSrgbProfile(new Uint8Array([0, 1, 2, 3])), /Invalid JPEG start marker/);
console.log("JPEG ICC unit test: PASS");
