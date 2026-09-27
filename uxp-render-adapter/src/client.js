"use strict";
const auth = require("./auth");
const validators = require("../dist/validators.cjs");
const { utf8ToBytes } = require("./utf8");
const { parseStrict } = require("./json");
const { responseByteLimit, UnsentRequestError } = require("./socket");
const pluginManifest = require("../manifest.json");

class AdapterClient {
  constructor(fetcher, randomBytes, host, capabilities) {
    this.fetcher = fetcher; this.randomBytes = randomBytes; this.host = host; this.capabilities = capabilities;
    this.instance = "instance_" + auth.encode(randomBytes(32));
    this.session = null; this.key = null; this.sequence = 0; this.busy = false;
    this.diagnostic = { stage: "created", transport: "idle", http_status: null };
  }

  async pair(secret) {
    this.clear();
    this.diagnostic = { stage: "challenge_create", transport: "idle", http_status: null };
    const requestId = "request_" + auth.encode(this.randomBytes(32));
    const clientNonce = auth.encode(this.randomBytes(32));
    const request = { transport_version: "ps-adapter/v1", kind: "challenge_request", body: {
      request_id: requestId, client_nonce: clientNonce, plugin_instance_id: this.instance,
      plugin_id: pluginManifest.id, plugin_version: pluginManifest.version, photoshop_build: this.host.build,
      uxp_runtime_version: this.host.runtime, capabilities: this.capabilities } };
    this.diagnostic.stage = "challenge_request";
    const challengeReply = await this.raw("http://127.0.0.1:17842/adapter/v1/challenges", JSON.stringify(request));
    this.diagnostic.stage = "challenge_validate";
    const envelope = parseStrict(challengeReply.text);
    if (challengeReply.status !== 200 || !validators.adapter(envelope) || envelope.kind !== "challenge_response")
      throw new Error("BRIDGE_PAIRING_REJECTED");
    const c = envelope.body;
    this.diagnostic.stage = "challenge_proof";
    const now = Math.floor(Date.now() / 1000);
    if (c.request_id !== requestId || c.client_nonce !== clientNonce || c.plugin_instance_id !== this.instance
      || c.plugin_id !== request.body.plugin_id || c.plugin_version !== pluginManifest.version || c.photoshop_build !== this.host.build
      || c.uxp_runtime_version !== this.host.runtime || c.expires_at_unix_seconds <= now || c.issued_at_unix_seconds > now + 5
      || c.expires_at_unix_seconds - c.issued_at_unix_seconds > 30 || !c.capabilities.includes("adapter/pull-v1")
      || c.capabilities.some(value => !this.capabilities.includes(value)) || !auth.fixedEquals(auth.challengeProof(secret, c), c.server_proof))
      throw new Error("BRIDGE_PAIRING_REJECTED");
    this.diagnostic.stage = "handshake_create";
    const handshake = { protocol_version: "ps-bridge/v1", envelope_kind: "handshake_request", body: {
      request_id: requestId, correlation_id: "corr_" + auth.encode(this.randomBytes(32)), sent_at: new Date().toISOString(),
      session_id: c.session_id, nonce: c.nonce, client: { app_version: c.app_version, protocol_version: "ps-bridge/v1", requested_capabilities: c.capabilities },
      host: { plugin_manifest_version: pluginManifest.version, photoshop_build: this.host.build, uxp_runtime_version: this.host.runtime }, pairing_proof: "" } };
    handshake.body.pairing_proof = auth.pairingProof(secret, handshake.body);
    const json = JSON.stringify(handshake);
    const key = auth.sessionKey(secret, c);
    try {
      this.diagnostic.stage = "handshake_request";
      const paired = await this.raw("http://127.0.0.1:17842/handshake", json,
        { "X-Adapter-Handshake-Proof": auth.handshakeContextProof(secret, c, json) });
      this.diagnostic.stage = "handshake_validate";
      if (!auth.fixedEquals(auth.handshakeResponseProof(key, requestId, paired.text), paired.mac)) throw new Error("BRIDGE_PAIRING_REJECTED");
      const response = parseStrict(paired.text);
      if (paired.status !== 200 || !validators.bridge(response) || response.envelope_kind !== "handshake_response"
        || !response.body.ok || response.body.session_id !== c.session_id || response.body.request_id !== requestId
        || response.body.correlation_id !== handshake.body.correlation_id
        || response.body.capabilities.join("\n") !== c.capabilities.join("\n")) throw new Error("BRIDGE_PAIRING_REJECTED");
      this.key = key; this.session = response.body; this.sequence = 0;
      this.diagnostic.stage = "paired";
    } catch (error) { key.fill(0); throw error; }
  }

  async query(executionId, afterSequence = 0) {
    if (!/^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/.test(executionId)
      || !Number.isInteger(afterSequence) || afterSequence < 0 || afterSequence > 2147483647)
      throw new Error("BRIDGE_PROTOCOL_MISMATCH");
    return this.request(`/executions/${executionId}?after_seq=${afterSequence}`,
      { protocol_version: "ps-bridge/v1", envelope_kind: "request", body: {
        correlation_id: "corr_" + auth.encode(this.randomBytes(32)), sent_at: new Date().toISOString(),
        idempotency_key: "query_" + executionId, operation: "get_execution", execution_id: executionId, payload: {} } }, "GET");
  }

  async request(path, envelope, method = "POST") {
    if (!this.session || !this.key || this.busy) throw new Error("BRIDGE_DISCONNECTED");
    let cancellationVerified = false;
    this.busy = true;
    this.diagnostic.operation = envelope.kind || envelope.body?.operation || "unknown";
    this.diagnostic.stage = "request_send";
    this.diagnostic.rejection_code = null;
    try {
      const a = { request_id: "request_" + auth.encode(this.randomBytes(32)), session_id: this.session.session_id,
        plugin_instance_id: this.instance, sequence: this.sequence + 1, token: this.session.token };
      if (envelope.envelope_kind === "request") {
        envelope.body.request_id = a.request_id;
        envelope.body.session_id = a.session_id;
        envelope.body.token = a.token;
      }
      if (method === "GET") a.query = { protocol_version: envelope.protocol_version,
        correlation_id: envelope.body.correlation_id, sent_at: envelope.body.sent_at, idempotency_key: envelope.body.idempotency_key };
      const body = method === "GET" ? "" : JSON.stringify(envelope);
      const response = await this.raw(`http://127.0.0.1:${this.session.execution_port}${path}`, body, {
        Authorization: "Bearer " + a.token, "X-Adapter-Version": "ps-adapter/v1", "X-Bridge-Request-Id": a.request_id,
        "X-Bridge-Session-Id": a.session_id, "X-Adapter-Instance-Id": a.plugin_instance_id,
        "X-Adapter-Sequence": String(a.sequence), "X-Adapter-Mac": auth.requestMac(this.key, a, method, path, body),
        ...(a.query ? { "X-Bridge-Protocol-Version": a.query.protocol_version, "X-Bridge-Correlation-Id": a.query.correlation_id,
          "X-Bridge-Sent-At": a.query.sent_at, "X-Bridge-Idempotency-Key": a.query.idempotency_key } : {})
      }, method);
      this.diagnostic.stage = "response_mac";
      if (!auth.fixedEquals(auth.responseMac(this.key, a, response.status, response.text), response.mac))
        throw new Error("BRIDGE_EXECUTION_UNKNOWN");
      this.sequence = a.sequence;
      this.diagnostic.stage = "response_status";
      if (response.status !== 200) {
        // Only a verified response MAC may contribute a fixed diagnostic code.
        // Do not retain its body/user action or interpret rejection as safe to retry.
        let rejected;
        try {
          rejected = parseStrict(response.text);
          if (Object.keys(rejected).sort().join(",") === "code,retryable,scope,user_action"
            && ["BRIDGE_PROTOCOL_MISMATCH", "BRIDGE_PAIRING_REJECTED", "BRIDGE_TOKEN_INVALID",
              "BRIDGE_PORT_UNAVAILABLE", "BRIDGE_REQUEST_TOO_LARGE", "BRIDGE_PLAN_INVALID",
              "BRIDGE_RESOURCE_BINDING_INVALID", "BRIDGE_EXECUTION_NOT_FOUND", "BRIDGE_DISCONNECTED",
              "BRIDGE_HOST_BLOCKED", "BRIDGE_EXECUTION_UNKNOWN", "BRIDGE_BUSY"].includes(rejected.code))
            this.diagnostic.rejection_code = rejected.code;
        } catch { /* Malformed error responses remain unknown. */ }
        // This is only a MAC-bound acknowledgement of the desktop cancellation request,
        // not an execution terminal or permission to retry. Keep the session for cleanup/ACK.
        const cancellableTransfer = path === "/adapter/v1/resources/read" && envelope.kind === "resource_read"
          || path === "/adapter/v1/outputs/write" && envelope.kind === "output_write"
          || path === "/adapter/v1/outputs/seal" && envelope.kind === "output_seal";
        if (response.status === 400 && method === "POST" && cancellableTransfer && rejected
          && Object.keys(rejected).sort().join(",") === "code,retryable,scope,user_action"
          && rejected.code === "BRIDGE_CANCEL_PENDING" && rejected.retryable === false
          && rejected.scope === "execution" && rejected.user_action === "当前页正在取消，等待安全释放确认。") {
          cancellationVerified = true;
          this.diagnostic.rejection_code = "BRIDGE_CANCEL_PENDING";
          throw new AuthenticatedCancellation();
        }
        throw new Error("BRIDGE_EXECUTION_UNKNOWN");
      }
      this.diagnostic.stage = "response_contract";
      const value = parseStrict(response.text);
      if (!(value.transport_version ? validators.adapter(value) : validators.bridge(value))) throw new Error("BRIDGE_PROTOCOL_MISMATCH");
      if (envelope.envelope_kind === "request" && (value.envelope_kind !== "response"
        || value.body.request_id !== a.request_id || value.body.correlation_id !== envelope.body.correlation_id))
        throw new Error("BRIDGE_PROTOCOL_MISMATCH");
      const expected = envelope.kind === "event_report" ? "event_receipt"
        : ["output_write", "output_seal"].includes(envelope.kind) ? "output_receipt"
        : envelope.kind === "resource_read" ? "resource_chunk"
        : ["claim_request", "claim_ack"].includes(envelope.kind) ? "claim_response" : null;
      if (expected && value.kind !== expected) throw new Error("BRIDGE_PROTOCOL_MISMATCH");
      this.diagnostic.stage = "response_verified";
      return value;
    } catch (error) { if (!cancellationVerified) this.clear(); throw error; }
    finally { this.busy = false; }
  }

  async raw(url, body, headers = {}, method = "POST") {
    const controller = typeof AbortController === "function" ? new AbortController() : null;
    const deliveryEvidence = {};
    let timer;
    const deadline = new Promise((_, reject) => { timer = setTimeout(() => {
      controller?.abort();
      // Both deadlines are five seconds. The outer timer can win the promise race;
      // preserve the carrier's local zero-byte proof, never infer it from a timeout.
      reject(deliveryEvidence.requestStarted === false ? new UnsentRequestError() : new Error("BRIDGE_DISCONNECTED"));
    }, 5000); });
    const operation = async () => {
      this.diagnostic.transport = "connect";
      this.diagnostic.http_status = null;
      let response;
      try {
        response = await this.fetcher(url, { method, redirect: "error", credentials: "omit", signal: controller?.signal, deliveryEvidence,
          headers: { "Content-Type": "application/json", ...headers }, ...(method === "GET" ? {} : { body }) });
      } catch (error) {
        const message = String(error?.message || error).toLowerCase();
        this.diagnostic.fetch_failure = ["redirect", "signal", "permission", "manifest", "network", "refused", "fetch", "cors", "unsupported"]
          .filter(word => message.includes(word));
        this.diagnostic.manifest_entry_missing = message.includes("manifest entry not found");
        throw error;
      }
      this.diagnostic.http_status = Number.isInteger(response.status) ? response.status : null;
      this.diagnostic.transport = "headers";
      const limit = responseByteLimit(url, method);
      const length = response.headers.get("Content-Length");
      if (length === null || !/^\d+$/.test(length) || Number(length) > limit) throw new Error("BRIDGE_REQUEST_TOO_LARGE");
      this.diagnostic.transport = "body";
      const text = await response.text();
      if (utf8ToBytes(text).length > limit) throw new Error("BRIDGE_REQUEST_TOO_LARGE");
      if (utf8ToBytes(text).length !== Number(length)) throw new Error("BRIDGE_PROTOCOL_MISMATCH");
      this.diagnostic.transport = "received";
      return { status: response.status, text, mac: response.headers.get("X-Adapter-Mac") || "" };
    };
    try { return await Promise.race([operation(), deadline]); }
    finally { clearTimeout(timer); }
  }

  clear() { if (this.key) this.key.fill(0); this.key = null; this.session = null; this.sequence = 0; }
}

class AuthenticatedCancellation extends Error {
  constructor() { super("BRIDGE_EXECUTION_CANCELLED"); }
}

module.exports = { AdapterClient, AuthenticatedCancellation };
