"use strict";
const photoshop = require("photoshop");
const { app, core, action } = photoshop;
const { versions, storage, host, entrypoints } = require("uxp");
const { AdapterClient } = require("./client");
const { createSocketTransport, UnsentRequestError } = require("./socket");
const { AdapterWorkLoop } = require("./work");
const { PhotoshopPageExecutor } = require("./photoshop");
const { recoverTransformPreference, readSnap } = require("./transform-preference");
const { inspectHost, isHostModal } = require("./host");
const { loadPairingSecret, rememberPairingSecret } = require("./pairing-storage");
const { createPairingCapabilities } = require("./pairing-capabilities");
const { loadPerformanceEnabled } = require("./performance");
let client = null;
let work = null;
let timer = null;
let inFlight = false;
let connecting = false;
let releasing = false;
let waitingTimer = null;
let waitingEnabled = true;
let recoveryRequired = false;
let disconnectRequested = false;
let destroyed = false;
let connectionEpoch = 0;
const status = document.getElementById("status");
const error = document.getElementById("error");
let hostBuild = null;
let initializing = false;
entrypoints.setup({ plugin: { create() {}, destroy() { destroyed = true; return disconnect(); } } });

function scheduleWaiting() {
  if (waitingTimer || timer || destroyed || !waitingEnabled || recoveryRequired || initializing
    || connecting || releasing || inFlight || client?.session || work?.busy) return;
  waitingTimer = setTimeout(async () => {
    waitingTimer = null;
    if (destroyed || !waitingEnabled || recoveryRequired) return;
    // Do not consume an in-progress setup code, or touch user documents/modal state.
    if (document.getElementById("pairing").value.trim() || app.documents.length || isHostModal(core)) {
      scheduleWaiting(); return;
    }
    if (!hostBuild) await retryHostInitialization();
    else await connect(true);
  }, 3000);
}
function hostFailureMessage(caught) {
  const messages = {
    record_invalid: "像素对齐恢复记录损坏，无法确认原值。记录已保留，请联系维护人员核对；不要删除记录后强行生成。",
    record_incomplete: "上次偏好记录写入未完成，无法安全确认原值。记录已保留，请联系维护人员核对。",
    record_write_failed: "偏好恢复记录保存失败，已停止生成。请检查插件数据目录是否可写，再点“重新检查并恢复”。",
    restore_failed: "Photoshop 像素对齐设置尚未恢复。请结束其他操作后点“重新检查并恢复”。"
  };
  return messages[caught?.preferenceReason] || "Photoshop 尚未就绪。请关闭文档、结束弹窗后点“重新检查并恢复”。";
}
async function initializeHost() {
  await recoverTransformPreference(photoshop, storage);
  const info = await inspectHost(action, host, versions);
  const report = { ...info, random: typeof globalThis.crypto?.getRandomValues,
    abort: typeof AbortController, websocket: typeof WebSocket,
    documents: app.documents.length, modalApi: typeof core.isModal, modal: isHostModal(core) };
  report.transforms_snap_to_pixels = await readSnap(photoshop);
  const pluginFolder = await storage.localFileSystem.getPluginFolder();
  const manifestFile = await pluginFolder.getEntry("manifest.json");
  const manifest = JSON.parse(await manifestFile.read());
  const domains = manifest.requiredPermissions?.network?.domains;
  report.network = { entries: Array.isArray(domains) ? domains.length : 0,
    rendezvous: Array.isArray(domains) && domains.length === 1 && domains[0] === "ws://localhost" };
  document.getElementById("host").textContent = `Photoshop ${info.build}`;
  const folder = await storage.localFileSystem.getDataFolder();
  const file = await folder.createFile("host-probe.json", { overwrite: true });
  await file.write(JSON.stringify(report));
  if (report.random !== "function" || report.abort !== "function" || report.websocket !== "function") throw new Error("BRIDGE_HOST_BLOCKED");
  hostBuild = info.build;
}
async function retryHostInitialization() {
  if (destroyed || recoveryRequired || initializing || connecting || releasing || inFlight || client?.session || work?.busy) return;
  if (app.documents.length || isHostModal(core)) {
    error.textContent = hostFailureMessage(); scheduleWaiting(); return;
  }
  initializing = true; hostBuild = null;
  document.getElementById("connect").disabled = true;
  document.getElementById("recover").disabled = true;
  try {
    await initializeHost(); error.textContent = "";
    status.textContent = "已就绪，等待桌面端请求；首次使用请完成设备配对";
  } catch (caught) {
    waitingEnabled = false;
    error.textContent = hostFailureMessage(caught); status.textContent = "宿主检查未通过";
  } finally {
    initializing = false;
    document.getElementById("connect").disabled = destroyed || recoveryRequired || !hostBuild;
    document.getElementById("recover").disabled = destroyed || recoveryRequired;
    if (disconnectRequested) await releaseConnection();
    scheduleWaiting();
  }
}
document.getElementById("recover").addEventListener("click", retryHostInitialization);

function randomBytes(length) {
  const result = new Uint8Array(length);
  if (!globalThis.crypto?.getRandomValues) throw new Error("BRIDGE_HOST_BLOCKED");
  globalThis.crypto.getRandomValues(result);
  return result;
}

async function connect(automatic = false) {
  if (destroyed || recoveryRequired || initializing || !hostBuild || connecting || releasing || inFlight || work?.busy || client?.session) return;
  if (automatic && !waitingEnabled) return;
  connecting = true;
  const epoch = connectionEpoch;
  if (waitingTimer) clearTimeout(waitingTimer);
  waitingTimer = null;
  document.getElementById("connect").disabled = true;
  if (timer) clearTimeout(timer);
  timer = null;
  error.textContent = "";
  const field = document.getElementById("pairing");
  let supplied = automatic ? "" : field.value.trim();
  const remember = !automatic && !!supplied && document.getElementById("remember").checked;
  const reusable = !supplied || remember;
  if (!automatic) field.value = "";
  let secret;
  let stage = "secret_decode";
  try {
    secret = await loadPairingSecret(storage.secureStorage, supplied);
    supplied = "";
    if (epoch !== connectionEpoch || destroyed) return;
    if (secret.length !== 32) throw new Error("BRIDGE_PAIRING_REJECTED");
    stage = "host_check";
    if (!hostBuild || app.documents.length !== 0 || isHostModal(core)) throw new Error("BRIDGE_HOST_BLOCKED");
    const performanceEnabled = await loadPerformanceEnabled(storage);
    if (epoch !== connectionEpoch || destroyed) return;
    await work?.stop(); work = null;
    client?.clear();
    client = null;
    stage = "client_create";
    // Pairing setup always negotiates protected-storage support. The checkbox
    // only controls whether the plugin persists the secret after success.
    const capabilities = createPairingCapabilities();
    client = new AdapterClient(createSocketTransport(WebSocket), randomBytes, { build: hostBuild,
      runtime: String(versions.uxp || "unknown") }, capabilities);
    stage = "pair";
    await client.pair(secret);
    if (epoch !== connectionEpoch || destroyed) { await releaseConnection(); return; }
    if (remember) {
      stage = "protected_storage";
      await rememberPairingSecret(storage.secureStorage, secret);
      if (epoch !== connectionEpoch || destroyed) { await releaseConnection(); return; }
    }
    const executor = new PhotoshopPageExecutor(photoshop, storage, randomBytes);
    work = new AdapterWorkLoop(client, storage, randomBytes,
      () => executor.inspect(),
      async prepared => {
        // Temporary staging evidence is not Photoshop execution or output completion.
        const folder = await storage.localFileSystem.getDataFolder();
        const file = await folder.createFile("resource-probe.json", { overwrite: true });
        await file.write(JSON.stringify({ stage: "resources_staged", recorded_at: new Date().toISOString(),
          plan_digest: prepared.plan.plan_digest, resource_count: prepared.leases.size,
          resources: prepared.plan.resources.map(resource => ({ role: resource.role, sha256: resource.sha256,
            byte_length: resource.byte_length })), output_executed: false }));
        status.textContent = prepared.plan.required_capabilities.includes("layout/absolute-placement-v1")
          ? "页面资源已验证，正在渲染" : "页面缺少明确放置数据，等待桌面端处理";
      }, { executor, fs: require("fs"), async onRendered(evidence) {
        const folder = await storage.localFileSystem.getDataFolder();
        const file = await folder.createFile("render-probe.json", { overwrite: true });
        await file.write(JSON.stringify({ ...evidence, recorded_at: new Date().toISOString(), published: false }));
        status.textContent = "页面已渲染，等待桌面端校验和提交";
      }, async onFailed(evidence) {
        status.textContent = "当前页未通过检查，已释放临时资源；请在桌面端处理";
        try {
          const folder = await storage.localFileSystem.getDataFolder();
          const file = await folder.createFile("page-failure-probe.json", { overwrite: true });
          await file.write(JSON.stringify({ ...evidence, stage: executor.stage, preflight: executor.diagnostic,
            terminal_state: "failed", recorded_at: new Date().toISOString() }));
        } catch { /* Reporting an optional diagnostic cannot undo the acknowledged terminal. */ }
      }, async onCommitted(evidence) {
        status.textContent = "页面已校验并提交";
        const folder = await storage.localFileSystem.getDataFolder();
        const file = await folder.createFile("commit-probe.json", { overwrite: true });
        await file.write(JSON.stringify({ ...evidence, temporary_resources_released: true,
          recorded_at: new Date().toISOString() }));
      }, performanceEnabled });
    // One-time probe codes are never persisted unless device setup was explicitly selected.
    status.textContent = "已连接";
    waitingEnabled = reusable;
    await writePairingProbe({ stage: "paired", code: null, protected_storage_verified: remember });
    // Delivery/commit queries must not wait for the liveness heartbeat interval.
    // Schedule only after the previous tick finishes: Photoshop and RPCs stay serial.
    timer = setTimeout(heartbeat, 0);
  } catch (caught) {
    const waiting = reusable && caught instanceof UnsentRequestError && stage === "pair"
      && client?.diagnostic?.stage === "challenge_request";
    client?.clear();
    if (epoch !== connectionEpoch || destroyed) return;
    waitingEnabled = waiting;
    status.textContent = waiting ? "等待桌面端请求" : "未连接，自动等待已停止";
    error.textContent = waiting ? "" : /^BRIDGE_[A-Z_]+$/.test(caught.message) ? caught.message : "BRIDGE_PAIRING_REJECTED";
    if (waiting) return; // An absent listener is normal; no failure-file polling.
    // Only fixed stages and coarse exception types are retained, never bodies, headers or raw messages.
    try {
      await writePairingProbe({ ...client?.diagnostic, stage, code: error.textContent,
        exception_type: ["Error", "TypeError", "ReferenceError", "RangeError"].includes(caught.name) ? caught.name : "unknown" });
    } catch {}
  } finally {
    supplied = ""; secret?.fill(0);
    if (!automatic) document.getElementById("remember").checked = false;
    connecting = false;
    document.getElementById("connect").disabled = destroyed || recoveryRequired || !hostBuild;
    if (disconnectRequested) await releaseConnection();
    scheduleWaiting();
  }
}

async function heartbeat() {
  timer = null;
  if (inFlight || !client?.session || !work || work.stopped) return;
  inFlight = true;
  let healthy = false;
  try {
    await work.tick();
    healthy = true;
  } catch (caught) {
    const safeIdleLoss = caught.message === "BRIDGE_DISCONNECTED" && work?.reconnectSafe === true;
    recoveryRequired = !safeIdleLoss;
    document.getElementById("connect").disabled = recoveryRequired;
    document.getElementById("recover").disabled = recoveryRequired;
    client?.clear();
    status.textContent = safeIdleLoss ? "等待桌面端请求"
      : "连接中断，自动等待已停止；请在桌面端查询页面状态后处理恢复";
    if (safeIdleLoss) return;
    try {
      const folder = await storage.localFileSystem.getDataFolder();
      const file = await folder.createFile("render-failure-probe.json", { overwrite: true });
      await file.write(JSON.stringify({ stage: work?.execution?.executor?.stage || "delivery",
        code: /^BRIDGE_[A-Z_]+$/.test(caught.message) ? caught.message : "BRIDGE_EXECUTION_UNKNOWN",
        exception_type: ["Error", "TypeError", "ReferenceError", "RangeError"].includes(caught.name) ? caught.name : "unknown",
        exception_value_type: typeof caught,
        transport: client?.diagnostic || null,
        output_transfer: client?.outputDiagnostic || null,
        preflight: work?.execution?.executor?.diagnostic || null,
        host_state: caught.hostState || null,
        bundle_line: Number(/main\.js:(\d+)/.exec(String(caught.stack || ""))?.[1]) || null,
        recorded_at: new Date().toISOString() }));
    } catch {}
  }
  finally {
    inFlight = false;
    if (disconnectRequested) await releaseConnection();
    // A bounded query cadence, not a retry: tick still rejects unknown delivery
    // and heartbeat() in work.js retains its five-second liveness throttle.
    if (healthy && client?.session && work && !work.stopped) timer = setTimeout(heartbeat, 250);
    else scheduleWaiting();
  }
}

document.getElementById("connect").addEventListener("click", () => connect(false));
document.getElementById("disconnect").addEventListener("click", disconnect);
async function disconnect() {
  waitingEnabled = false; disconnectRequested = true; connectionEpoch++;
  if (waitingTimer) clearTimeout(waitingTimer);
  waitingTimer = null;
  if (timer) clearTimeout(timer);
  timer = null;
  status.textContent = "自动等待已暂停";
  if (!initializing && !connecting && !inFlight) await releaseConnection();
}

async function releaseConnection() {
  if (releasing) return;
  releasing = true;
  if (timer) clearTimeout(timer);
  timer = null;
  if (work && work.reconnectSafe !== true) recoveryRequired = true;
  try { await work?.stop(); }
  catch { recoveryRequired = true; }
  work = null;
  if (client?.session) {
    try { await client.request("/release", { protocol_version: "ps-bridge/v1", envelope_kind: "request", body: {
      request_id: "request_release", correlation_id: "corr_release", session_id: client.session.session_id, token: client.session.token,
      sent_at: new Date().toISOString(), idempotency_key: "release_session", operation: "release", payload: {} } }); } catch {}
  }
  client?.clear(); disconnectRequested = false;
  releasing = false;
  document.getElementById("connect").disabled = destroyed || recoveryRequired || !hostBuild;
  document.getElementById("recover").disabled = destroyed || recoveryRequired;
  status.textContent = recoveryRequired ? "自动等待已停止，请在桌面端查询页面状态后处理恢复" : "自动等待已暂停";
}

async function writePairingProbe(value) {
  try {
    const folder = await storage.localFileSystem.getDataFolder();
    const file = await folder.createFile("pairing-probe.json", { overwrite: true });
    await file.write(JSON.stringify({ ...value, recorded_at: new Date().toISOString() }));
  } catch { /* Diagnostics must not invalidate an authenticated session. */ }
}

retryHostInitialization();
