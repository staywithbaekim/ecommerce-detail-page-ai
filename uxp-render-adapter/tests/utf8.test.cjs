"use strict";
const { test } = require("node:test");
const assert = require("node:assert/strict");
const { utf8ToBytes, bytesToUtf8 } = require("../src/utf8");

test("UXP UTF-8 codec matches independent native encoding over every Unicode scalar", () => {
  for (let start = 0; start <= 0x10ffff; start += 4096) {
    const points = [];
    for (let point = start; point < Math.min(start + 4096, 0x110000); point++)
      if (point < 0xd800 || point > 0xdfff) points.push(point);
    const text = String.fromCodePoint(...points);
    const bytes = utf8ToBytes(text);
    assert.deepEqual(Buffer.from(bytes), Buffer.from(text, "utf8"));
    assert.equal(bytesToUtf8(bytes), text);
  }
  for (const text of ["", "尺码\u0000换行\n", "\ud800", "\udfff", "x\ud800y", "\ud800\ud800\udfff"]) {
    assert.deepEqual(Buffer.from(utf8ToBytes(text)), Buffer.from(text, "utf8"));
    assert.equal(bytesToUtf8(utf8ToBytes(text)), Buffer.from(text, "utf8").toString("utf8"));
  }
});

test("wire UTF-8 rejects overlong, truncated, surrogate and out-of-range encodings", () => {
  for (const bytes of [[0x80], [0xc0, 0xaf], [0xc1, 0xbf], [0xc2], [0xe0, 0x80, 0x80], [0xe1, 0x80],
    [0xed, 0xa0, 0x80], [0xf0, 0x80, 0x80, 0x80], [0xf0, 0x90, 0x80], [0xf4, 0x90, 0x80, 0x80],
    [0xf5, 0x80, 0x80, 0x80], [0xfe], [0xff], [0xe1, 0x41, 0x80]])
    assert.throws(() => bytesToUtf8(Uint8Array.from(bytes)), { message: "BRIDGE_PROTOCOL_MISMATCH" });
});
