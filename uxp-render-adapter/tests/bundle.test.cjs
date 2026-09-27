"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { UnsentRequestError } = require("../src/socket");

function lifecycleFixture(options = {}) {
  const timers = new Map(), elements = new Map(), hooks = {}, writes = [], secrets = [];
  const calls = { secretLoads: 0, clientCreates: 0, pairs: 0, workCreates: 0, ticks: 0 };
  let nextId = 0;
  const client = { session: options.idle ? null : {}, diagnostic: {}, clear() { this.session = null; }, async request() {},
    async pair() { calls.pairs++; this.diagnostic.stage = "challenge_request"; await options.pair?.(this); this.session = {}; } };
  const work = { stopped: false, busy: false, reconnectSafe: true,
    async tick() { calls.ticks++; await options.tick?.(); }, async stop() { this.stopped = true; } };
  const photoshop = { app: { documents: [] }, core: { isModal: false }, action: {} };
  const context = vm.createContext({
    lifecycleClient: client, lifecycleWork: work,
    require(name) {
      if (name === "photoshop") return photoshop;
      if (name === "uxp") return { versions: {}, entrypoints: { setup(value) {
        assert.equal(typeof value.plugin.create, "function");
        assert.equal(typeof value.plugin.destroy, "function");
        Object.assign(hooks, value);
      } }, storage: { localFileSystem: {
        getPluginFolder: async () => ({ getEntry: async () => ({ read: async () => JSON.stringify({}) }) }),
        getDataFolder: async () => ({ createFile: async () => ({ write: async value => writes.push(JSON.parse(value)) }) })
      } }, host: {} };
      if (name === "./host") return { inspectHost: async () => ({ build: "test" }), isHostModal: () => photoshop.core.isModal };
      if (name === "./transform-preference") return { recoverTransformPreference: options.recover || (async () => {}), readSnap: async () => false };
      if (name === "./pairing-storage") return { async loadPairingSecret(_storage, supplied) {
        calls.secretLoads++; const secret = options.loadSecret ? await options.loadSecret(supplied) : new Uint8Array(32).fill(7);
        secrets.push(secret); return secret;
      }, async rememberPairingSecret() { await options.remember?.(); } };
      if (name === "./client") return { AdapterClient: class { constructor() { calls.clientCreates++; return client; } } };
      if (name === "./socket") return { createSocketTransport: () => ({}), UnsentRequestError };
      if (name === "./pairing-capabilities") return { createPairingCapabilities: () => [] };
      if (name === "./performance") return { loadPerformanceEnabled: async () => false };
      if (name === "./photoshop") return { PhotoshopPageExecutor: class {} };
      if (name === "./work") return { AdapterWorkLoop: class {
        constructor() { calls.workCreates++; work.stopped = false; return work; }
      } };
      return {};
    },
    document: { getElementById(id) {
      if (!elements.has(id)) elements.set(id, { handlers: {}, value: "", checked: false, textContent: "",
        addEventListener(event, handler) { this.handlers[event] = handler; } });
      return elements.get(id);
    } },
    setTimeout(callback, delay) { const id = ++nextId; timers.set(id, { callback, delay }); return id; },
    clearTimeout(id) { timers.delete(id); },
    AbortController, WebSocket: function () {}, crypto: { getRandomValues() {} }
  });
  for (const id of ["pairing", "remember"]) context.document.getElementById(id);
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../src/main.js"), "utf8"), context);
  if (!options.idle) vm.runInContext("client = lifecycleClient; work = lifecycleWork;", context);
  return { client, work, timers, context, elements, photoshop, calls, hooks, writes, secrets, async runNext() {
    assert.equal(timers.size, 1);
    const [id, timer] = [...timers][0]; timers.delete(id);
    await timer.callback();
  } };
}

test("work scheduler re-arms at 250ms only after completion, never overlaps, and disconnect clears it", async () => {
  const f = lifecycleFixture();
  await new Promise(resolve => setImmediate(resolve));
  let finish, ticks = 0;
  f.work.tick = () => { ticks++; return new Promise(resolve => { finish = resolve; }); };
  const running = f.context.heartbeat();
  await f.context.heartbeat();
  assert.equal(ticks, 1);
  assert.equal(f.timers.size, 0);
  finish(); await running;
  assert.equal(f.timers.size, 1);
  assert.equal([...f.timers.values()][0].delay, 250);
  f.work.tick = async () => { ticks++; };
  await f.runNext();
  assert.equal(ticks, 2);
  assert.equal(f.timers.size, 1);
  await f.elements.get("disconnect").handlers.click();
  assert.equal(f.timers.size, 0);
  assert.equal(f.work.stopped, true);
  assert.equal(f.client.session, null);
});

test("startup recovery gives actionable diagnostics and retry neither pairs nor replays work", async () => {
  let calls = 0, failing = true;
  const f = lifecycleFixture({ recover: async () => {
    calls++; if (failing) throw Object.assign(Error("BRIDGE_HOST_BLOCKED"), { preferenceReason: "record_invalid" });
  } });
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(f.elements.get("connect").disabled, true);
  assert.match(f.elements.get("error").textContent, /记录损坏.*不要删除/);
  f.client.clear(); failing = false;
  await f.elements.get("recover").handlers.click();
  assert.equal(calls, 2); assert.equal(f.elements.get("error").textContent, "");
  assert.equal(f.elements.get("connect").disabled, false); assert.equal(f.timers.size, 0);
  assert.equal(f.client.session, null);
});

test("host retry is unavailable during a session or with user documents open", async () => {
  let calls = 0;
  const f = lifecycleFixture({ recover: async () => { calls++; } });
  await new Promise(resolve => setImmediate(resolve));
  await f.elements.get("recover").handlers.click(); assert.equal(calls, 1);
  f.client.clear(); f.photoshop.app.documents.push({ id: 7 });
  await f.elements.get("recover").handlers.click(); assert.equal(calls, 1);
  assert.match(f.elements.get("error").textContent, /关闭文档/);
});

test("pending recovery rejects repeated recovery and connection without pairing or dispatching work", async () => {
  let recoveries = 0, hold = false, finish;
  const gate = new Promise(resolve => { finish = resolve; });
  const f = lifecycleFixture({ recover: async () => { recoveries++; if (hold) await gate; } });
  await new Promise(resolve => setImmediate(resolve));
  f.client.clear(); hold = true;
  const recovering = f.elements.get("recover").handlers.click();
  try {
    assert.equal(recoveries, 2);
    assert.equal(f.elements.get("connect").disabled, true);
    assert.equal(f.elements.get("recover").disabled, true);
    await f.elements.get("recover").handlers.click();
    await f.elements.get("connect").handlers.click();
    await f.context.heartbeat();
    assert.equal(recoveries, 2);
    assert.deepEqual(f.calls, { secretLoads: 0, clientCreates: 0, pairs: 0, workCreates: 0, ticks: 0 });
    assert.equal(f.client.session, null); assert.equal(f.timers.size, 0);
  } finally { finish(); await recovering; }
  assert.equal(f.elements.get("connect").disabled, false);
  assert.equal(f.elements.get("recover").disabled, false);
  assert.deepEqual(f.calls, { secretLoads: 0, clientCreates: 0, pairs: 0, workCreates: 0, ticks: 0 });
  assert.equal(f.client.session, null); assertWaiting(f);
});

test("recovery cannot overlap a pending connection or create a second pairing", async () => {
  let recoveries = 0, finish;
  const gate = new Promise(resolve => { finish = resolve; });
  const f = lifecycleFixture({ recover: async () => { recoveries++; }, loadSecret: () => gate });
  await new Promise(resolve => setImmediate(resolve));
  f.client.clear();
  const connecting = f.elements.get("connect").handlers.click();
  try {
    assert.equal(f.calls.secretLoads, 1);
    await f.elements.get("recover").handlers.click();
    await f.elements.get("connect").handlers.click();
    assert.equal(recoveries, 1);
    assert.deepEqual(f.calls, { secretLoads: 1, clientCreates: 0, pairs: 0, workCreates: 0, ticks: 0 });
    assert.equal(f.timers.size, 0);
  } finally { finish(new Uint8Array(32)); await connecting; }
  assert.deepEqual(f.calls, { secretLoads: 1, clientCreates: 1, pairs: 1, workCreates: 1, ticks: 0 });
  assert.equal(f.timers.size, 1);
  const scheduled = [...f.timers];
  await f.elements.get("recover").handlers.click();
  assert.equal(recoveries, 1); assert.deepEqual([...f.timers], scheduled);
  assert.equal(f.calls.pairs, 1); assert.equal(f.calls.ticks, 0);
  await f.elements.get("disconnect").handlers.click();
  assert.equal(f.timers.size, 0);
});

test("session loss during an in-flight tick still blocks recovery and reconnection until it settles", async () => {
  let recoveries = 0, finish;
  const gate = new Promise(resolve => { finish = resolve; });
  const f = lifecycleFixture({ recover: async () => { recoveries++; }, tick: () => gate });
  await new Promise(resolve => setImmediate(resolve));
  const ticking = f.context.heartbeat();
  f.client.clear();
  try {
    assert.equal(f.calls.ticks, 1);
    await f.elements.get("recover").handlers.click();
    await f.elements.get("connect").handlers.click();
    assert.equal(recoveries, 1);
    assert.deepEqual(f.calls, { secretLoads: 0, clientCreates: 0, pairs: 0, workCreates: 0, ticks: 1 });
    assert.equal(f.timers.size, 0);
  } finally { finish(); await ticking; }
  await f.elements.get("recover").handlers.click();
  assert.equal(recoveries, 2);
  assert.deepEqual(f.calls, { secretLoads: 0, clientCreates: 0, pairs: 0, workCreates: 0, ticks: 1 });
  assert.equal(f.client.session, null); assertWaiting(f);
});

test("a busy executor without a session blocks recovery and connection without replay", async () => {
  let recoveries = 0;
  const f = lifecycleFixture({ recover: async () => { recoveries++; } });
  await new Promise(resolve => setImmediate(resolve));
  f.client.clear(); f.work.busy = true;
  await f.elements.get("recover").handlers.click();
  await f.elements.get("connect").handlers.click();
  assert.equal(recoveries, 1);
  assert.deepEqual(f.calls, { secretLoads: 0, clientCreates: 0, pairs: 0, workCreates: 0, ticks: 0 });
  assert.equal(f.timers.size, 0);
  f.work.busy = false;
  await f.elements.get("recover").handlers.click();
  assert.equal(recoveries, 2);
  assert.deepEqual(f.calls, { secretLoads: 0, clientCreates: 0, pairs: 0, workCreates: 0, ticks: 0 });
  assert.equal(f.client.session, null); assertWaiting(f);
});

function assertWaiting(f) {
  assert.equal(f.timers.size, 1);
  assert.equal([...f.timers.values()][0].delay, 3000);
}

test("startup waits serially for desktop with only saved pairing, zeroed secrets and no absent-listener logs", async () => {
  let ready = false;
  const f = lifecycleFixture({ idle: true, loadSecret: supplied => {
    assert.equal(supplied, ""); return new Uint8Array(32).fill(9);
  }, pair: () => { if (!ready) throw new UnsentRequestError(); } });
  await new Promise(resolve => setImmediate(resolve)); assertWaiting(f);
  const startupWrites = f.writes.length;
  for (let i = 0; i < 3; i++) {
    await f.runNext(); assertWaiting(f);
    assert.equal(f.calls.workCreates, 0); assert.equal(f.writes.length, startupWrites);
    assert.equal(f.elements.get("error").textContent, "");
  }
  assert.ok(f.secrets.every(secret => secret.every(byte => byte === 0)));
  ready = true; await f.runNext();
  assert.equal(f.calls.workCreates, 1); assert.equal(f.calls.pairs, 4);
  assert.equal([...f.timers.values()][0].delay, 0);
  await f.runNext(); assert.equal(f.calls.ticks, 1);
  assert.equal([...f.timers.values()][0].delay, 250);
  await f.hooks.plugin.destroy(); assert.equal(f.timers.size, 0);
});

for (const blocker of ["document", "modal", "setup_input"]) {
  test(`automatic waiting leaves ${blocker} untouched without loading secrets`, async () => {
    const f = lifecycleFixture({ idle: true });
    await new Promise(resolve => setImmediate(resolve));
    if (blocker === "document") f.photoshop.app.documents.push({ id: 7 });
    if (blocker === "modal") f.photoshop.core.isModal = true;
    if (blocker === "setup_input") { f.elements.get("pairing").value = "pending code"; f.elements.get("remember").checked = true; }
    await f.runNext(); assertWaiting(f); assert.equal(f.calls.secretLoads, 0); assert.equal(f.calls.pairs, 0);
    if (blocker === "setup_input") {
      assert.equal(f.elements.get("pairing").value, "pending code"); assert.equal(f.elements.get("remember").checked, true);
    }
    await f.hooks.plugin.destroy(); assert.equal(f.timers.size, 0);
  });
}

for (const code of ["BRIDGE_PAIRING_REQUIRED", "BRIDGE_PAIRING_STORAGE_UNAVAILABLE", "BRIDGE_PAIRING_REJECTED", "BRIDGE_PROTOCOL_MISMATCH"]) {
  test(`automatic connection stops and exposes ${code}, without a retry loop`, async () => {
    const f = lifecycleFixture({ idle: true, loadSecret: () => {
      if (code !== "BRIDGE_PAIRING_REJECTED" && code !== "BRIDGE_PROTOCOL_MISMATCH") throw Error(code);
      return new Uint8Array(32).fill(9);
    }, pair: () => { throw Error(code); } });
    await new Promise(resolve => setImmediate(resolve)); await f.runNext();
    assert.equal(f.timers.size, 0); assert.equal(f.calls.workCreates, 0);
    assert.equal(f.elements.get("error").textContent, code);
    assert.ok(f.secrets.every(secret => secret.every(byte => byte === 0)));
  });
}

for (const phase of ["secret", "pair"]) {
  test(`manual pause invalidates a delayed ${phase} completion without scheduling or starting work`, async () => {
    let finish;
    const gate = new Promise(resolve => { finish = resolve; });
    const f = lifecycleFixture({ idle: true, loadSecret: async () => {
      if (phase === "secret") await gate; return new Uint8Array(32).fill(9);
    }, pair: async () => { if (phase === "pair") await gate; } });
    await new Promise(resolve => setImmediate(resolve));
    const connecting = f.runNext(); await new Promise(resolve => setImmediate(resolve));
    await f.elements.get("connect").handlers.click();
    assert.equal(f.calls.secretLoads, 1);
    await f.elements.get("disconnect").handlers.click();
    finish(); await connecting;
    assert.equal(f.calls.workCreates, 0); assert.equal(f.client.session, null); assert.equal(f.timers.size, 0);
    assert.ok(f.secrets.every(secret => secret.every(byte => byte === 0)));
    assert.match(f.elements.get("status").textContent, /暂停/);
  });
}

for (const safe of [true, false]) {
  test(`idle disconnect reconnect safety ${safe} survives cleared prepared state`, async () => {
    const f = lifecycleFixture({ idle: true });
    await new Promise(resolve => setImmediate(resolve)); await f.runNext();
    f.work.reconnectSafe = safe;
    f.work.tick = async () => { f.client.clear(); throw Error("BRIDGE_DISCONNECTED"); };
    await f.runNext();
    if (safe) {
      assertWaiting(f); await f.runNext(); assert.equal(f.calls.workCreates, 2);
    } else {
      assert.equal(f.elements.get("connect").disabled, true);
      assert.equal(f.elements.get("recover").disabled, true);
      assert.equal(f.timers.size, 0); await f.elements.get("connect").handlers.click();
      await f.elements.get("recover").handlers.click();
      assert.equal(f.calls.workCreates, 1); assert.match(f.elements.get("status").textContent, /查询页面状态/);
    }
    await f.hooks.plugin.destroy(); assert.equal(f.timers.size, 0);
  });
}

test("a one-time code does not enable background reconnection or get remembered", async () => {
  let saves = 0;
  const f = lifecycleFixture({ idle: true, remember: () => { saves++; } });
  await new Promise(resolve => setImmediate(resolve));
  f.elements.get("pairing").value = "one-time";
  await f.elements.get("connect").handlers.click();
  f.work.tick = async () => { f.client.clear(); throw Error("BRIDGE_DISCONNECTED"); };
  await f.runNext(); assert.equal(saves, 0); assert.equal(f.timers.size, 0);
});

test("work scheduler stops after an unknown execution rather than scheduling a retry", async () => {
  const f = lifecycleFixture();
  let ticks = 0;
  f.work.tick = async () => { ticks++; f.client.clear(); throw Error("BRIDGE_EXECUTION_UNKNOWN"); };
  await f.context.heartbeat();
  await f.context.heartbeat();
  assert.equal(ticks, 1);
  assert.equal(f.timers.size, 0);
  assert.match(f.elements.get("status").textContent, /连接中断/);
});

for (const action of ["pause", "destroy"]) {
  test(`${action} during host initialization remains paused after its late completion`, async () => {
    let finish;
    const gate = new Promise(resolve => { finish = resolve; });
    const f = lifecycleFixture({ idle: true, recover: () => gate });
    await new Promise(resolve => setImmediate(resolve));
    if (action === "destroy") await f.hooks.plugin.destroy();
    else await f.elements.get("disconnect").handlers.click();
    assert.equal(f.elements.get("connect").disabled, true);
    assert.equal(f.timers.size, 0);
    finish(); await new Promise(resolve => setImmediate(resolve));
    assert.match(f.elements.get("status").textContent, /暂停/);
    assert.equal(f.elements.get("connect").disabled, action === "destroy");
    assert.equal(f.elements.get("recover").disabled, action === "destroy");
    assert.equal(f.calls.secretLoads, 0); assert.equal(f.calls.pairs, 0);
    assert.equal(f.calls.ticks, 0); assert.equal(f.timers.size, 0);
  });
}

test("built manifest restricts network access to the localhost WebSocket carrier", () => {
  const manifest = JSON.parse(fs.readFileSync(path.join(__dirname, "../dist/manifest.json"), "utf8"));
  const source = require("../manifest.json");
  assert.equal(manifest.version, source.version);
  const desktop = fs.readFileSync(path.join(__dirname, "../../src/Desktop.Contracts/Bridge/BridgeAdapterV1.cs"), "utf8");
  assert.equal(/PluginVersion = "([^"]+)"/.exec(desktop)[1], manifest.version);
  assert.deepEqual(manifest.requiredPermissions, { localFileSystem: "request", network: { domains:
    ["ws://localhost"] } });
  assert.equal(manifest.host[0].data.loadEvent, "startup");
  assert.equal(manifest.loadEvent, undefined);
});

test("packaging includes both declared plugin and panel icon scales without unrelated assets", () => {
  const manifest = JSON.parse(fs.readFileSync(path.join(__dirname, "../dist/manifest.json"), "utf8"));
  for (const icons of [manifest.icons, manifest.entrypoints[0].icons]) {
    assert.equal(icons.length, 1);
    const icon = icons[0];
    assert.equal(icon.path, "icons/renderer.png");
    assert.deepEqual(icon.scale, [1, 2]);
    for (const scale of icon.scale) {
      const name = icon.path.replace(/\.png$/, scale === 1 ? ".png" : `@${scale}x.png`);
      const source = fs.readFileSync(path.join(__dirname, "..", name));
      const built = fs.readFileSync(path.join(__dirname, "../dist", name));
      assert.deepEqual(built, source);
      assert.equal(built.subarray(0, 8).toString("hex"), "89504e470d0a1a0a");
      assert.equal(built.readUInt32BE(16), icon.width * scale);
      assert.equal(built.readUInt32BE(20), icon.height * scale);
      assert.ok(built.length < 1024 * 1024);
    }
  }
  assert.deepEqual(fs.readdirSync(path.join(__dirname, "../dist/icons")).sort(), ["renderer.png", "renderer@2x.png"]);
});

test("built plugin resolves all dependencies with only declared UXP host modules", async () => {
  const elements = new Map();
  const writes = [];
  const context = {
    require(name) {
      if (name === "photoshop") return { app: { version: "26.8.1", documents: [] },
        core: { isModal: false, getHostEnvironment: async () => ({ appVersion: "26.8.1", appBuild: "test" }) },
        action: { batchPlay: async commands => commands[0]._target[0]._property === "toolsPreferences"
          ? [{ toolsPreferences: { transformsSnapToPixels: false } }]
          : [{ hostVersion: { versionMajor: 26, versionMinor: 8, versionFix: 1 } },
            { buildNumber: "26.8 (20250624.r.8 5e0d05d)" }] } };
      if (name === "uxp") return { versions: { uxp: "test" }, host: { version: "26.8.1" }, entrypoints: { setup() {} },
        storage: { localFileSystem: { getPluginFolder: async () => ({ getEntry: async name => {
          assert.equal(name, "manifest.json");
          return { read: async () => fs.readFileSync(path.join(__dirname, "../dist/manifest.json"), "utf8") };
        } }), getDataFolder: async () => ({
          getEntries: async () => [],
          createFile: async () => ({ write: async value => writes.push(JSON.parse(value)) })
        }) } } };
      throw new Error("Undeclared runtime dependency: " + name);
    },
    document: { getElementById(id) {
      if (!elements.has(id)) elements.set(id, { addEventListener() {}, textContent: "", value: "" });
      return elements.get(id);
    } },
    setTimeout() { return 1; }, clearTimeout() {}, AbortController, WebSocket: function () {},
    crypto: { getRandomValues() {} }
  };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, "../dist/main.js"), "utf8"), context);
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(writes.length, 1);
  assert.equal(writes[0].network.entries, 1);
  assert.equal(writes[0].network.rendezvous, true);
  assert.equal(elements.get("error").textContent, "");
});
