const test = require("node:test");
const assert = require("node:assert/strict");
const { loadPairingSecret, rememberPairingSecret } = require("../src/pairing-storage");
const auth = require("../src/auth");

function storage(initial) {
  const values = new Map(initial ? [["render-pairing-v1", new Uint8Array(initial)]] : []);
  return { values, get length() { return values.size; }, async key(index) { return [...values.keys()][index]; },
    async getItem(key) { if (!values.has(key)) throw new Error("missing"); return new Uint8Array(values.get(key)); },
    async setItem(key, bytes) { values.set(key, new Uint8Array(bytes)); },
    async removeItem(key) { values.delete(key); } };
}

test("one-time input bypasses storage and never persists", async () => {
  const secret = new Uint8Array(32).fill(7);
  const unavailable = { get length() { throw new Error("must not read"); } };
  const loaded = await loadPairingSecret(unavailable, auth.encode(secret));
  assert.deepEqual(loaded, secret);
  loaded.fill(0);
});

test("device rotation preserves protected previous value and reads new value", async () => {
  const old = new Uint8Array(32).fill(5), next = new Uint8Array(32).fill(9);
  const store = storage(old);
  await rememberPairingSecret(store, next);
  assert.deepEqual(store.values.get("render-pairing-v1.previous"), old);
  assert.deepEqual(await loadPairingSecret(store, ""), next);
  assert.deepEqual(next, new Uint8Array(32).fill(9));
});

test("missing and malformed protected values fail closed without secret text", async () => {
  await assert.rejects(loadPairingSecret(storage(), ""), /^Error: BRIDGE_PAIRING_REQUIRED$/);
  await assert.rejects(loadPairingSecret(storage(new Uint8Array(31)), ""), /^Error: BRIDGE_PAIRING_STORAGE_UNAVAILABLE$/);
  await assert.rejects(loadPairingSecret({ get length() { throw new Error("sensitive raw error"); } }, ""),
    /^Error: BRIDGE_PAIRING_STORAGE_UNAVAILABLE$/);
});

test("backup failure never replaces existing device secret", async () => {
  const old = new Uint8Array(32).fill(5), store = storage(old);
  store.setItem = async () => { throw new Error("sensitive raw error"); };
  await assert.rejects(rememberPairingSecret(store, new Uint8Array(32).fill(9)), /^Error: BRIDGE_PAIRING_STORAGE_UNAVAILABLE$/);
  assert.deepEqual(store.values.get("render-pairing-v1"), old);
});

test("corrupt protected backup blocks rotation before touching current value", async () => {
  const old = new Uint8Array(32).fill(5), store = storage(old);
  const write = store.setItem.bind(store);
  store.setItem = async (key, bytes) => write(key, key.endsWith(".previous") ? new Uint8Array(32) : bytes);
  await assert.rejects(rememberPairingSecret(store, new Uint8Array(32).fill(9)), /^Error: BRIDGE_PAIRING_STORAGE_UNAVAILABLE$/);
  assert.deepEqual(store.values.get("render-pairing-v1"), old);
});

for (const hadPrevious of [false, true]) test(`corrupt readback restores prior protected state (${hadPrevious})`, async () => {
  const old = new Uint8Array(32).fill(5), next = new Uint8Array(32).fill(9);
  const store = storage(hadPrevious ? old : null);
  const write = store.setItem.bind(store);
  let corrupt = true;
  store.setItem = async (key, bytes) => {
    if (key === "render-pairing-v1" && corrupt) { corrupt = false; await write(key, new Uint8Array(32)); }
    else await write(key, bytes);
  };
  await assert.rejects(rememberPairingSecret(store, next), /^Error: BRIDGE_PAIRING_STORAGE_UNAVAILABLE$/);
  assert.equal(store.values.has("render-pairing-v1"), hadPrevious);
  if (hadPrevious) assert.deepEqual(store.values.get("render-pairing-v1"), old);
});
