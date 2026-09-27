"use strict";

// The protected-storage capability describes what the adapter can negotiate.
// It is not consent to persist the one-time secret; persistence is controlled
// separately by the "remember this device" checkbox after pairing succeeds.
const capabilities = Object.freeze([
  "adapter/pull-v1",
  "psd-layer-path/psd-bottom-to-top",
  "smart-object/replace",
  "output/jpeg-srgb-icc",
  "layout/absolute-placement-v1",
  "output/verification-psd-v1",
  "size-table/calibrated-target-v1",
  "size-table/insert-layer-v1",
  "output/variant-identity-v1",
  "pairing/protected-storage-v1"
]);

function createPairingCapabilities() {
  return [...capabilities];
}

module.exports = { createPairingCapabilities };
