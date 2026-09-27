"use strict";

// UXP 8.3 does not expose the browser TextEncoder/TextDecoder globals.
// Match UTF-8 encoding of JS strings; reject malformed wire bytes rather than replacing them.
function utf8ToBytes(text) {
  if (typeof text !== "string") throw new Error("BRIDGE_PROTOCOL_MISMATCH");
  const bytes = new Uint8Array(text.length * 3);
  let offset = 0;
  for (let i = 0; i < text.length; i++) {
    let value = text.charCodeAt(i);
    if (value >= 0xd800 && value <= 0xdbff) {
      const next = text.charCodeAt(i + 1);
      if (next >= 0xdc00 && next <= 0xdfff) { value = 0x10000 + ((value - 0xd800) << 10) + next - 0xdc00; i++; }
      else value = 0xfffd;
    } else if (value >= 0xdc00 && value <= 0xdfff) value = 0xfffd;
    if (value < 0x80) bytes[offset++] = value;
    else if (value < 0x800) {
      bytes[offset++] = 0xc0 | (value >> 6); bytes[offset++] = 0x80 | (value & 63);
    } else if (value < 0x10000) {
      bytes[offset++] = 0xe0 | (value >> 12); bytes[offset++] = 0x80 | ((value >> 6) & 63); bytes[offset++] = 0x80 | (value & 63);
    } else {
      bytes[offset++] = 0xf0 | (value >> 18); bytes[offset++] = 0x80 | ((value >> 12) & 63);
      bytes[offset++] = 0x80 | ((value >> 6) & 63); bytes[offset++] = 0x80 | (value & 63);
    }
  }
  return bytes.subarray(0, offset);
}

function bytesToUtf8(bytes) {
  const parts = [];
  let units = [];
  for (let i = 0; i < bytes.length;) {
    const first = bytes[i++];
    let value, remaining, minimum;
    if (first < 0x80) { value = first; remaining = 0; minimum = 0; }
    else if (first >= 0xc2 && first <= 0xdf) { value = first & 31; remaining = 1; minimum = 0x80; }
    else if (first >= 0xe0 && first <= 0xef) { value = first & 15; remaining = 2; minimum = 0x800; }
    else if (first >= 0xf0 && first <= 0xf4) { value = first & 7; remaining = 3; minimum = 0x10000; }
    else throw new Error("BRIDGE_PROTOCOL_MISMATCH");
    if (i + remaining > bytes.length) throw new Error("BRIDGE_PROTOCOL_MISMATCH");
    while (remaining-- > 0) {
      const next = bytes[i++];
      if ((next & 0xc0) !== 0x80) throw new Error("BRIDGE_PROTOCOL_MISMATCH");
      value = (value << 6) | (next & 63);
    }
    if (value < minimum || value > 0x10ffff || (value >= 0xd800 && value <= 0xdfff)) throw new Error("BRIDGE_PROTOCOL_MISMATCH");
    if (value <= 0xffff) units.push(value);
    else { value -= 0x10000; units.push(0xd800 | (value >> 10), 0xdc00 | (value & 1023)); }
    if (units.length >= 4096) { parts.push(String.fromCharCode(...units)); units = []; }
  }
  parts.push(String.fromCharCode(...units));
  return parts.join("");
}

module.exports = { utf8ToBytes, bytesToUtf8 };
