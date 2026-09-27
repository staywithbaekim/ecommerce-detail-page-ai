"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const { inspectHost, isHostModal } = require("../src/host");

test("modal function is evaluated and unsupported results block execution", () => {
  assert.equal(isHostModal({ isModal: () => false }), false);
  assert.equal(isHostModal({ isModal: () => true }), true);
  assert.equal(isHostModal({ isModal: false }), false);
  assert.equal(isHostModal({}), true);
  assert.equal(isHostModal({ isModal: () => Promise.resolve(false) }), true);
});

test("runtime version and host build must agree without relying on absent app.version", async () => {
  const action = { batchPlay: async requests => {
    assert.deepEqual(requests.map(r => r._target[0]._property), ["hostVersion", "buildNumber"]);
    return [{ hostVersion: { versionMajor: 26, versionMinor: 8, versionFix: 1 } }, { buildNumber: "26.8 (20250624.r.8 5e0d05d)" }];
  } };
  assert.deepEqual(await inspectHost(action, { version: "26.8.1" }, { uxp: "uxp-test" }),
    { build: "26.8.1 (20250624.r.8 5e0d05d)", runtime: "uxp-test" });
  await assert.rejects(inspectHost(action, { version: "26.8.0" }, { uxp: "uxp-test" }), /BRIDGE_HOST_BLOCKED/);
  await assert.rejects(inspectHost({ batchPlay: async () => [{ _obj: "error" }] }, { version: "26.8.1" }, { uxp: "uxp-test" }), /BRIDGE_HOST_BLOCKED/);
});
