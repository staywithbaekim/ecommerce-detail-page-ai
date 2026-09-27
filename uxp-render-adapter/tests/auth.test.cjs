"use strict";
const { test } = require("node:test");
const assert = require("node:assert/strict");
const { createHmac, createHash } = require("node:crypto");
const auth = require("../src/auth");

test("transcript matches independent Node HMAC for unicode, separators and empty values", () => {
  const key = Uint8Array.from({ length: 32 }, (_, i) => i);
  const fields = ["ps-adapter/v1", "request", "中文", "a\nb", "", "123"];
  const transcript = Buffer.concat(fields.flatMap(value => {
    const bytes = Buffer.from(value, "utf8"), prefix = Buffer.alloc(4);
    prefix.writeUInt32BE(bytes.length); return [prefix, bytes];
  }));
  assert.equal(auth.sign(key, ...fields), createHmac("sha256", key).update(transcript).digest("base64url"));
  assert.notEqual(auth.sign(key, "ab", "c"), auth.sign(key, "a", "bc"));
  assert.equal(auth.hash("中文"), createHash("sha256").update("中文").digest("hex"));
});

test("base64url strictly roundtrips every byte and rejects noncanonical padding bits", () => {
  for (let i = 0; i < 256; i++) {
    const bytes = new Uint8Array(32).fill(i);
    assert.equal(auth.encode(bytes), Buffer.from(bytes).toString("base64url"));
    assert.deepEqual(auth.decode32(auth.encode(bytes)), bytes);
  }
  assert.throws(() => auth.decode32("A".repeat(42) + "B"));
  assert.throws(() => auth.decode32("A".repeat(43) + "="));
  assert.equal(auth.fixedEquals("A".repeat(43), "A".repeat(43)), true);
  assert.equal(auth.fixedEquals("A".repeat(43), "B".repeat(43)), false);
});

test("legacy pairing proof uses decoded nonce followed by UTF-8 fields", () => {
  const key = new Uint8Array(32).fill(2);
  const request = { nonce: auth.encode(new Uint8Array(32).fill(7)), session_id: "session_1",
    host: { plugin_manifest_version: "0.1.0", photoshop_build: "26.8.1" } };
  const data = Buffer.concat([Buffer.alloc(32, 7), Buffer.from("session_10.1.026.8.1")]);
  assert.equal(auth.pairingProof(key, request), createHmac("sha256", key).update(data).digest("base64url"));
});
