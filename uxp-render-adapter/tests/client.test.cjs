"use strict";
const { test } = require("node:test");
const assert = require("node:assert/strict");
const { randomBytes } = require("node:crypto");
const { AdapterClient, AuthenticatedCancellation } = require("../src/client");
const auth = require("../src/auth");

for (const mismatch of [false, true]) test(`pairing uses manifest version and rejects mismatched challenge (${mismatch})`, async () => {
  const manifest = require("../manifest.json"), secret = randomBytes(32);
  let calls = 0, challenge;
  const client = new AdapterClient(async (_, options) => {
    calls++;
    const sent = JSON.parse(options.body).body;
    if (calls === 1) {
      assert.equal(sent.plugin_version, manifest.version);
      assert.equal(sent.plugin_id, manifest.id);
      const now = Math.floor(Date.now() / 1000);
      challenge = { request_id: sent.request_id, client_nonce: sent.client_nonce, nonce: auth.encode(randomBytes(32)),
        session_id: "session_version", plugin_instance_id: sent.plugin_instance_id, task_binding: auth.encode(randomBytes(32)),
        app_version: "0.1.0", plugin_id: manifest.id, plugin_version: mismatch ? "0.1.0" : manifest.version,
        photoshop_build: sent.photoshop_build, uxp_runtime_version: sent.uxp_runtime_version,
        issued_at_unix_seconds: now, expires_at_unix_seconds: now + 30, capabilities: ["adapter/pull-v1"], server_proof: "" };
      challenge.server_proof = auth.challengeProof(secret, challenge);
      return response(JSON.stringify({ transport_version: "ps-adapter/v1", kind: "challenge_response", body: challenge }));
    }
    assert.equal(mismatch, false);
    assert.equal(sent.host.plugin_manifest_version, manifest.version);
    assert.equal(sent.pairing_proof, auth.pairingProof(secret, sent));
    assert.equal(options.headers["X-Adapter-Handshake-Proof"], auth.handshakeContextProof(secret, challenge, options.body));
    throw Error("TEST_VERSION_HANDSHAKE_OBSERVED");
  }, randomBytes, { build: "26.8.1", runtime: "8.3" }, ["adapter/pull-v1"]);
  await assert.rejects(client.pair(secret), { message: mismatch ? "BRIDGE_PAIRING_REJECTED" : "TEST_VERSION_HANDSHAKE_OBSERVED" });
  assert.equal(calls, mismatch ? 1 : 2);
  assert.equal(client.session, null);
});

function response(text, declared = Buffer.byteLength(text), mac = "") {
  return { status: 200, headers: { get: name => name === "Content-Length" ? declared : mac }, text: async () => text };
}

const pairingFailures = [
  ["valid", null], ["challenge_json", "challenge_json"], ["challenge_status", "challenge_status"],
  ["challenge_contract", "challenge_contract"],
  ...["request_id", "client_nonce", "plugin_instance_id", "plugin_id", "plugin_version", "photoshop_build", "uxp_runtime_version"]
    .map(field => [field, field === "plugin_id" ? "challenge_contract" : "challenge_binding"]),
  ...["expired", "future", "duration"].map(name => [name, "challenge_time"]),
  ["unsupported", "challenge_capabilities"], ["missing_pull", "challenge_capabilities"],
  ["challenge_proof", "challenge_proof"], ["handshake_mac", "handshake_mac"],
  ["handshake_json", "handshake_json"], ["handshake_status", "handshake_status"],
  ["handshake_contract", "handshake_contract"], ["handshake_rejected", "handshake_rejected"],
  ...["session_id", "request_id", "correlation_id"].map(field => ["handshake_" + field, "handshake_binding"]),
  ["handshake_capabilities", "handshake_capabilities"]
];
for (const [mutation, reason] of pairingFailures) test(`pairing rejection classification preserves all gates: ${mutation}`, async () => {
  // Public synthetic key only. Never obtain real protected credentials in tests.
  const secret = new Uint8Array(32).fill(17), before = secret.slice();
  const capabilities = ["adapter/pull-v1", "template/page-instance-identity-v1"];
  const manifest = require("../manifest.json");
  let challenge, calls = 0;
  const client = new AdapterClient(async (_, options) => {
    const sent = JSON.parse(options.body).body; calls++;
    if (calls === 1) {
      const now = Math.floor(Date.now() / 1000);
      challenge = { request_id: sent.request_id, client_nonce: sent.client_nonce, nonce: auth.encode(new Uint8Array(32).fill(2)),
        session_id: "session_diagnostics", plugin_instance_id: sent.plugin_instance_id, task_binding: auth.encode(new Uint8Array(32).fill(3)),
        app_version: "0.1.0", plugin_id: manifest.id, plugin_version: manifest.version,
        photoshop_build: sent.photoshop_build, uxp_runtime_version: sent.uxp_runtime_version,
        issued_at_unix_seconds: now, expires_at_unix_seconds: now + 30, capabilities: [...capabilities], server_proof: "" };
      if (reason === "challenge_binding" || mutation === "plugin_id") challenge[mutation] = mutation === "client_nonce" ? auth.encode(new Uint8Array(32).fill(4))
        : mutation === "plugin_version" ? "0.1.0" : "other_value";
      if (mutation === "expired") { challenge.issued_at_unix_seconds = now - 31; challenge.expires_at_unix_seconds = now - 1; }
      if (mutation === "future") { challenge.issued_at_unix_seconds = now + 10; challenge.expires_at_unix_seconds = now + 30; }
      if (mutation === "duration") challenge.expires_at_unix_seconds = now + 60;
      if (mutation === "unsupported") challenge.capabilities.push("test/unsupported");
      if (mutation === "missing_pull") challenge.capabilities = [capabilities[1]];
      if (mutation === "challenge_contract") challenge.unexpected = "private-extra";
      challenge.server_proof = auth.challengeProof(secret, challenge);
      if (mutation === "challenge_proof") challenge.server_proof = auth.encode(new Uint8Array(32).fill(5));
      const text = mutation === "challenge_json" ? "{" : JSON.stringify({ transport_version: "ps-adapter/v1", kind: "challenge_response", body: challenge });
      return { ...response(text), status: mutation === "challenge_status" ? 403 : 200 };
    }
    assert.equal(calls, 2);
    assert.equal(sent.pairing_proof, auth.pairingProof(secret, sent));
    assert.equal(options.headers["X-Adapter-Handshake-Proof"], auth.handshakeContextProof(secret, challenge, options.body));
    const body = { ok: true, request_id: sent.request_id, correlation_id: sent.correlation_id, status: "paired",
      server_time: new Date().toISOString(), session_id: challenge.session_id, execution_port: 51000,
      token: auth.encode(new Uint8Array(32).fill(6)), expires_at: new Date(Date.now() + 900000).toISOString(), capabilities: [...capabilities] };
    if (reason === "handshake_binding") body[mutation.slice("handshake_".length)] = "other_identity";
    if (mutation === "handshake_capabilities") body.capabilities = [capabilities[0]];
    if (mutation === "handshake_contract") body.unexpected = "private-extra";
    if (mutation === "handshake_rejected") {
      body.ok = false; body.status = "rejected";
      body.error = { code: "BRIDGE_PAIRING_REJECTED", retryable: false, scope: "session", user_action: "private-message" };
    }
    const text = mutation === "handshake_json" ? "{" : JSON.stringify({ protocol_version: "ps-bridge/v1", envelope_kind: "handshake_response", body });
    const key = auth.sessionKey(secret, challenge);
    const mac = mutation === "handshake_mac" ? auth.encode(new Uint8Array(32).fill(7)) : auth.handshakeResponseProof(key, sent.request_id, text);
    key.fill(0);
    return { ...response(text, Buffer.byteLength(text), mac), status: mutation === "handshake_status" ? 403 : 200 };
  }, randomBytes, { build: "26.8.1 (20250624.r.8 5e0d05d)", runtime: "uxp-8.3.0-uxp-b1d8c85" }, capabilities);
  if (reason) {
    await assert.rejects(client.pair(secret), { message: reason.endsWith("_json") ? "BRIDGE_PROTOCOL_MISMATCH" : "BRIDGE_PAIRING_REJECTED" });
    assert.equal(client.diagnostic.rejection_reason, reason);
    assert.equal(client.session, null); assert.equal(client.key, null);
    assert.equal(calls, reason.startsWith("handshake_") ? 2 : 1);
  } else {
    await client.pair(secret);
    assert.deepEqual(client.session.capabilities, capabilities); assert.equal(client.diagnostic.stage, "paired");
    const key = client.key; client.clear(); assert.ok(key.every(byte => byte === 0));
  }
  assert.deepEqual(secret, before);
  assert.equal(JSON.stringify(client.diagnostic).includes("private"), false);
});

test("missing, oversized and mismatched response framing is rejected", async () => {
  for (const [text, length, error] of [["{}", null, "BRIDGE_REQUEST_TOO_LARGE"], ["{}", "1048577", "BRIDGE_REQUEST_TOO_LARGE"],
    ["{}", "1", "BRIDGE_PROTOCOL_MISMATCH"], ["{}", "invalid", "BRIDGE_REQUEST_TOO_LARGE"]]) {
    const client = new AdapterClient(async () => response(text, length), randomBytes, {}, []);
    await assert.rejects(client.raw("http://127.0.0.1:17842/handshake", "{}"), { message: error });
  }
});

test("request deadline includes reading the response body", async context => {
  context.mock.timers.enable({ apis: ["setTimeout"] });
  const fetcher = async () => ({ ...response("{}", "2"), text: () => new Promise(() => {}) });
  const client = new AdapterClient(fetcher, randomBytes, {}, []);
  const pending = assert.rejects(client.raw("http://127.0.0.1:17842/handshake", "{}"), { message: "BRIDGE_DISCONNECTED" });
  await Promise.resolve();
  context.mock.timers.tick(5000);
  await pending;
});

test("tampered or mismatched authenticated replies invalidate the session and never retry", async () => {
  for (const mutation of ["mac", "correlation", "request", "duplicate", "kind"]) {
    let calls = 0;
    const key = randomBytes(32);
    const client = new AdapterClient(async (_, options) => {
      calls++;
      const sent = JSON.parse(options.body);
      const a = { request_id: options.headers["X-Bridge-Request-Id"], session_id: "session_test",
        plugin_instance_id: client.instance, sequence: 1 };
      const body = { protocol_version: "ps-bridge/v1", envelope_kind: mutation === "kind" ? "request" : "response",
        body: { ok: true, request_id: mutation === "request" ? "wrong_request" : a.request_id,
          correlation_id: mutation === "correlation" ? "wrong_correlation" : sent.body.correlation_id,
          status: "ok", server_time: new Date().toISOString() } };
      let text = JSON.stringify(body);
      if (mutation === "duplicate") text = text.replace('"ok":true', '"ok":false,"ok":true');
      const mac = mutation === "mac" ? auth.encode(randomBytes(32)) : auth.responseMac(key, a, 200, text);
      return response(text, String(Buffer.byteLength(text)), mac);
    }, randomBytes, {}, []);
    client.key = key;
    client.session = { session_id: "session_test", token: auth.encode(randomBytes(32)), execution_port: 51000 };
    const request = { protocol_version: "ps-bridge/v1", envelope_kind: "request", body: {
      correlation_id: "corr_test", sent_at: new Date().toISOString(), idempotency_key: "heartbeat_test",
      operation: "heartbeat", payload: { host_state: "ready", modal_state: "idle", last_event_seq: 0 } } };
    await assert.rejects(client.request("/heartbeat", request));
    assert.equal(client.session, null);
    assert.equal(client.key, null);
    assert.equal(key.every(value => value === 0), true);
    await assert.rejects(client.request("/heartbeat", request), { message: "BRIDGE_DISCONNECTED" });
    assert.equal(calls, 1);
  }
});

test("only authenticated known rejection codes enter diagnostics without response text or retry", async () => {
  for (const mutation of ["valid", "unsigned", "unknown", "extra"]) {
    const key = randomBytes(32); let calls = 0;
    const client = new AdapterClient(async (_, options) => {
      calls++;
      const a = { request_id: options.headers["X-Bridge-Request-Id"], session_id: "session_test",
        plugin_instance_id: client.instance, sequence: 1 };
      const text = JSON.stringify({ code: mutation === "unknown" ? "BRIDGE_PRIVATE_LOG" : "BRIDGE_RESOURCE_BINDING_INVALID",
        retryable: false, scope: "request", user_action: "private response text",
        ...(mutation === "extra" ? { private: "private response text" } : {}) });
      return { ...response(text, String(Buffer.byteLength(text)), mutation === "unsigned" ? "" : auth.responseMac(key, a, 400, text)), status: 400 };
    }, randomBytes, {}, []);
    client.key = key;
    client.session = { session_id: "session_test", token: auth.encode(randomBytes(32)), execution_port: 51000 };
    await assert.rejects(client.request("/adapter/v1/claims", { transport_version: "ps-adapter/v1", kind: "claim_request", body: {} }),
      { message: "BRIDGE_EXECUTION_UNKNOWN" });
    assert.equal(client.diagnostic.rejection_code, mutation === "valid" ? "BRIDGE_RESOURCE_BINDING_INVALID" : null);
    assert.equal(JSON.stringify(client.diagnostic).includes("private"), false);
    assert.equal(client.session, null); assert.equal(calls, 1);
  }
});

for (const [route, kind] of [["/adapter/v1/resources/read", "resource_read"],
  ["/adapter/v1/outputs/write", "output_write"], ["/adapter/v1/outputs/seal", "output_seal"]])
for (const mutation of ["valid", "unsigned", "status", "scope", "retryable", "extra", "message", "route", "kind", "duplicate", "code"]) {
  test(`${kind} cancellation ${mutation} preserves authentication only for the exact verified pending reply`, async () => {
    const key = randomBytes(32); let calls = 0;
    const client = new AdapterClient(async (_, options) => {
      calls++;
      const a = { request_id: options.headers["X-Bridge-Request-Id"], session_id: "session_test",
        plugin_instance_id: client.instance, sequence: Number(options.headers["X-Adapter-Sequence"]) };
      const status = mutation === "status" ? 403 : 400;
      let text = JSON.stringify({ code: mutation === "code" ? "BRIDGE_RESOURCE_BINDING_INVALID" : "BRIDGE_CANCEL_PENDING",
        retryable: mutation === "retryable", scope: mutation === "scope" ? "session" : "execution",
        user_action: mutation === "message" ? "untrusted details" : "当前页正在取消，等待安全释放确认。",
        ...(mutation === "extra" ? { details: "private" } : {}) });
      if (mutation === "duplicate") text = text.replace('"retryable":false', '"retryable":true,"retryable":false');
      return { ...response(text, String(Buffer.byteLength(text)), mutation === "unsigned" ? "" : auth.responseMac(key, a, status, text)), status };
    }, randomBytes, {}, []);
    client.key = key;
    client.session = { session_id: "session_test", token: auth.encode(randomBytes(32)), execution_port: 51000 };
    const request = { transport_version: "ps-adapter/v1", kind: mutation === "kind" ? "claim_request" : kind,
      body: { claim_id: "claim_test", execution_id: "execution_test", plan_digest: "sha256:" + "a".repeat(64),
        session_handle: "A".repeat(43), offset: 0, count: 8 } };
    await assert.rejects(client.request(mutation === "route" ? "/adapter/v1/claims" : route, request),
      error => mutation === "valid" ? error instanceof AuthenticatedCancellation : !(error instanceof AuthenticatedCancellation));
    assert.equal(calls, 1); assert.equal(client.busy, false);
    assert.equal(client.session !== null, mutation === "valid");
    assert.equal(key.every(value => value === 0), mutation !== "valid");
    assert.equal(client.sequence, mutation === "valid" ? 1 : 0);
    client.clear();
  });
}
