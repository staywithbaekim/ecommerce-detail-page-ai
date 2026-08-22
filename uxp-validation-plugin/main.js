const { app, action, core } = require("photoshop");
const { storage } = require("uxp");

const localFileSystem = storage.localFileSystem;
const BRIDGE_URL = "http://127.0.0.1:17842";
let loadedPlan = null;

const statusNode = document.getElementById("status");
const outputNode = document.getElementById("output");
const executeButton = document.getElementById("execute-plan");

function showStatus(message, result) {
  statusNode.textContent = message;
  outputNode.textContent = JSON.stringify(result, null, 2);
}

function structuredError(code, message, details = {}) {
  return { ok: false, error: { code, message, details } };
}

function assertRenderPlan(plan) {
  if (!plan || plan.schema !== "render-plan/v1") {
    throw new Error("RenderPlan schema must be render-plan/v1.");
  }
  if (!plan.job_id || !Array.isArray(plan.pages) || plan.pages.length === 0) {
    throw new Error("RenderPlan requires job_id and at least one page.");
  }
  for (const page of plan.pages) {
    if (!page.page_id || !page.template_copy || !Array.isArray(page.frames)) {
      throw new Error("Each page requires page_id, template_copy, and frames.");
    }
    for (const frame of page.frames) {
      if (!frame.frame_id || !frame.target || !frame.source || !frame.transform) {
        throw new Error("Each frame requires a registered target, source, and transform.");
      }
      if (!frame.source.relative_path || !frame.source.sha256) {
        throw new Error("Frame source must carry a relative path and SHA-256.");
      }
    }
  }
}

async function writeEvidence(name, value) {
  const folder = await localFileSystem.getDataFolder();
  const file = await folder.createFile(name, { overwrite: true });
  await file.write(JSON.stringify(value, null, 2));
}

async function recordPhotoshopProbe() {
  try {
    const result = await core.executeAsModal(async () => {
      const documentResult = await action.batchPlay([
        {
          _obj: "get",
          _target: [{ _ref: "document", _enum: "ordinal", _value: "targetEnum" }],
          _options: { dialogOptions: "dontDisplay" }
        }
      ], { synchronousExecution: true, modalBehavior: "execute" });
      return {
        ok: true,
        probe: {
          photoshop_version: app.version || null,
          active_document: documentResult[0] || null,
          uxp_manifest: "v5",
          plugin_version: "0.1.0",
          recorded_at: new Date().toISOString()
        }
      };
    }, { commandName: "UXP validation probe" });
    await writeEvidence("uxp-validation-probe.json", result);
    showStatus("Probe recorded", result);
  } catch (error) {
    const result = structuredError("PROBE_FAILED", error.message);
    showStatus("Probe failed safely", result);
  }
}

async function handshakeWithDesktop() {
  const request = {
    protocol: "render-plan/v1",
    plugin_id: "local.ecommerce.uxp-validation",
    plugin_version: "0.1.0",
    photoshop_version: app.version || null,
    capabilities: ["probe", "render-plan-receive", "structured-errors"]
  };
  try {
    const response = await fetch(`${BRIDGE_URL}/handshake`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(request)
    });
    const body = await response.json();
    const result = { ok: response.ok, request, response: body };
    showStatus(response.ok ? "Bridge connected" : "Bridge rejected handshake", result);
  } catch (error) {
    showStatus("Bridge unavailable", structuredError("BRIDGE_UNAVAILABLE", error.message, { url: BRIDGE_URL }));
  }
}

async function loadRenderPlan() {
  try {
    const file = await localFileSystem.getFileForOpening({ types: ["json"] });
    if (!file) return;
    const plan = JSON.parse(await file.read());
    assertRenderPlan(plan);
    loadedPlan = plan;
    executeButton.disabled = false;
    showStatus("RenderPlan loaded", { ok: true, job_id: plan.job_id, pages: plan.pages.length });
  } catch (error) {
    loadedPlan = null;
    executeButton.disabled = true;
    showStatus("RenderPlan rejected", structuredError("INVALID_RENDER_PLAN", error.message));
  }
}

async function executeGatedPlan() {
  if (!loadedPlan) {
    showStatus("No plan loaded", structuredError("PLAN_NOT_LOADED", "Load a RenderPlan before execution."));
    return;
  }
  try {
    assertRenderPlan(loadedPlan);
    const result = await core.executeAsModal(async (executionContext) => {
      // Mutation is deliberately gated until the Photoshop 2022 mapping is observed on a copy.
      // This prevents an unverified layer operation from touching a user document.
      executionContext.reportProgress({ value: 1, commandName: "RenderPlan accepted" });
      throw new Error("RENDER_ADAPTER_NOT_VERIFIED");
    }, { commandName: "UXP gated RenderPlan" });
    showStatus("Plan completed", result);
  } catch (error) {
    showStatus("Plan stopped safely", structuredError("RENDER_ADAPTER_NOT_VERIFIED", error.message, {
      job_id: loadedPlan.job_id
    }));
  }
}

document.getElementById("probe").addEventListener("click", recordPhotoshopProbe);
document.getElementById("handshake").addEventListener("click", handshakeWithDesktop);
document.getElementById("load-plan").addEventListener("click", loadRenderPlan);
executeButton.addEventListener("click", executeGatedPlan);
