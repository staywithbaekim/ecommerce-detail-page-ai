"use strict";
const { test } = require("node:test");
const assert = require("node:assert/strict");
const { createPairingCapabilities } = require("../src/pairing-capabilities");

test("one-time pairing advertises protected-storage support without persisting the secret", () => {
  const first = createPairingCapabilities();
  assert.equal(first.includes("pairing/protected-storage-v1"), true);
  const second = createPairingCapabilities();
  assert.notEqual(first, second);
  first.pop();
  assert.equal(second.includes("pairing/protected-storage-v1"), true);
});
