"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const { createPerformanceReport, isPerformanceReportName, loadPerformanceEnabled, writePerformanceReport } = require("../src/performance");

test("performance report uses monotonic time when available and excludes unapproved fields", async () => {
  let tick = 10;
  const report = createPerformanceReport("execution:one", "page.one", { performance: { now: () => tick++ } });
  await report.measureAsync("resource_read_rpc", async () => {}, { resource_role: "high_resolution_render_source",
    bytes: 131072, chunks: 1, file_name: "private.jpg", token: "secret" });
  const snapshot = report.snapshot();
  assert.equal(snapshot.clock_source, "performance.now");
  assert.equal(snapshot.clock_monotonic, true);
  assert.deepEqual(snapshot.phases, [{ name: "resource_read_rpc", duration_ms: 1,
    resource_role: "high_resolution_render_source", bytes: 131072, chunks: 1 }]);
  assert.equal(JSON.stringify(snapshot).includes("private.jpg"), false);
  assert.equal(JSON.stringify(snapshot).includes("secret"), false);
  assert.equal(snapshot.totals.resource_read_rpc.bytes, 131072);
});

test("Date.now fallback is labeled as wall clock and failed clock reads cannot alter work", () => {
  const report = createPerformanceReport("execution_2", "page_2", { performance: null, dateNow: () => 50 });
  assert.equal(report.clock_source, "Date.now");
  assert.equal(report.clock_monotonic, false);
  const result = report.measure("phase", () => 42);
  assert.equal(result, 42);
  const broken = createPerformanceReport("execution_3", "page_3", { performance: { now: () => { throw Error("clock"); } } });
  assert.equal(broken.measure("phase", () => "ok"), "ok");
  assert.deepEqual(broken.phases, []);
});

test("local config must be the exact opt-in and all configuration errors disable diagnostics", async () => {
  const enabled = { localFileSystem: { getDataFolder: async () => ({ getEntry: async name => {
    assert.equal(name, "render-performance-config.json");
    return { read: async () => '{"enabled":true}' };
  } }) } };
  assert.equal(await loadPerformanceEnabled(enabled), true);
  const extra = { localFileSystem: { getDataFolder: async () => ({ getEntry: async () => ({
    read: async () => '{"enabled":true,"upload":true}' }) }) } };
  assert.equal(await loadPerformanceEnabled(extra), false);
  assert.equal(await loadPerformanceEnabled({ localFileSystem: { getDataFolder: async () => { throw Error("offline"); } } }), false);
});

test("report output has a constrained execution-derived name and failures are non-fatal", async () => {
  assert.equal(isPerformanceReportName("render-performance-execution.1.json"), true);
  assert.equal(isPerformanceReportName("render-performance-C:\\private.json"), false);
  assert.equal(createPerformanceReport("../private", "page"), null);
  const report = createPerformanceReport("execution:one", "page.one");
  const writes = [];
  const storage = { localFileSystem: { getDataFolder: async () => ({ createFile: async (name, options) => {
    writes.push({ name, options }); return { write: async value => writes.push(JSON.parse(value)) };
  } }) } };
  assert.equal(await writePerformanceReport(storage, report), true);
  assert.equal(writes[0].name, "render-performance-execution%3Aone.json");
  assert.deepEqual(writes[0].options, { overwrite: true });
  const unavailable = { localFileSystem: { getDataFolder: async () => { throw Error("offline"); } } };
  assert.equal(await writePerformanceReport(unavailable, report), false);
});
