"use strict";

const identityPattern = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/;
const reportPattern = /^render-performance-[A-Za-z0-9][A-Za-z0-9._%+-]{0,383}\.json$/;

function createPerformanceReport(executionId, pageId, clocks = {}) {
  if (!identityPattern.test(executionId) || !identityPattern.test(pageId)) return null;
  const performanceClock = clocks.performance === undefined ? globalThis.performance : clocks.performance;
  const wallNow = clocks.dateNow || Date.now;
  const clock = typeof performanceClock?.now === "function"
    ? { source: "performance.now", monotonic: true, now: () => performanceClock.now() }
    : { source: "Date.now", monotonic: false, now: () => wallNow.call(Date) };
  const safeNow = () => {
    try {
      const value = clock.now();
      return Number.isFinite(value) ? value : null;
    } catch { return null; }
  };
  return {
    execution_id: executionId,
    page_id: pageId,
    clock_source: clock.source,
    clock_monotonic: clock.monotonic,
    started_at: new Date().toISOString(),
    phases: [],
    totals: Object.create(null),
    measure(name, callback, fields = {}) {
      const started = safeNow();
      try { return callback(); }
      finally {
        const ended = safeNow();
        if (started !== null && ended !== null) this.add(name, Math.max(0, ended - started), fields);
      }
    },
    async measureAsync(name, callback, fields = {}) {
      const started = safeNow();
      try { return await callback(); }
      finally {
        const ended = safeNow();
        if (started !== null && ended !== null) this.add(name, Math.max(0, ended - started), fields);
      }
    },
    add(name, durationMs, fields = {}) {
      try {
        if (!/^[a-z][a-z0-9_]{0,63}$/.test(name) || !Number.isFinite(durationMs) || durationMs < 0) return;
        const safeFields = {};
        for (const key of ["resource_role", "output_kind", "bytes", "chunks", "count", "index"]) {
          const value = fields[key];
          if (typeof value === "string" && /^[a-z0-9_-]{1,40}$/.test(value)
            || Number.isSafeInteger(value) && value >= 0) safeFields[key] = value;
        }
        this.phases.push({ name, duration_ms: Number(durationMs.toFixed(3)), ...safeFields });
        if (this.phases.length > 2048) this.phases.shift();
        const total = this.totals[name] || (this.totals[name] = { duration_ms: 0, count: 0 });
        total.duration_ms = Number((total.duration_ms + durationMs).toFixed(3));
        total.count++;
        if (safeFields.bytes !== undefined) total.bytes = (total.bytes || 0) + safeFields.bytes;
        if (safeFields.chunks !== undefined) total.chunks = (total.chunks || 0) + safeFields.chunks;
      } catch { /* Diagnostics cannot affect the measured operation. */ }
    },
    snapshot() {
      return { schema_version: 1, execution_id: this.execution_id, page_id: this.page_id,
        clock_source: this.clock_source, clock_monotonic: this.clock_monotonic,
        started_at: this.started_at, completed_at: new Date().toISOString(),
        phases: this.phases.slice(), totals: JSON.parse(JSON.stringify(this.totals)) };
    }
  };
}

function isPerformanceReportName(name) { return reportPattern.test(name); }

async function loadPerformanceEnabled(storage) {
  try {
    const folder = await storage.localFileSystem.getDataFolder();
    const file = await folder.getEntry("render-performance-config.json");
    const config = JSON.parse(await file.read());
    return config && typeof config === "object" && !Array.isArray(config)
      && Object.keys(config).join(",") === "enabled" && config.enabled === true;
  } catch { return false; }
}

async function writePerformanceReport(storage, report) {
  if (!report) return false;
  try {
    const folder = await storage.localFileSystem.getDataFolder();
    if (!identityPattern.test(report.execution_id)) return false;
    const name = `render-performance-${encodeURIComponent(report.execution_id)}.json`;
    if (!isPerformanceReportName(name)) return false;
    const file = await folder.createFile(name, { overwrite: true });
    await file.write(JSON.stringify(report.snapshot()));
    return true;
  } catch { return false; }
}

module.exports = { createPerformanceReport, isPerformanceReportName, loadPerformanceEnabled, writePerformanceReport };
