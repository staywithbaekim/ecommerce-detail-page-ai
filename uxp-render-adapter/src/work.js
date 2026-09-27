"use strict";
const { validateClaim } = require("./plan");
const { stageResource } = require("./resources");
const { encode } = require("./auth");
const { uploadOutput } = require("./outputs");
const { UnsentRequestError } = require("./socket");
const { createPerformanceReport, writePerformanceReport } = require("./performance");

// The loop owns only authenticated delivery and temporary resources. The Photoshop execution
// gate is separate: a prepared page is never reported as started/completed or re-executed.
class AdapterWorkLoop {
  constructor(client, storage, randomBytes, inspect, onPrepared = async () => {}, execution = null) {
    this.client = client; this.storage = storage; this.randomBytes = randomBytes;
    this.inspect = inspect; this.onPrepared = onPrepared;
    this.execution = execution;
    this.prepared = null; this.busy = false; this.stopped = false;
    // Independent of resource cleanup: losing a claim/ACK must remain sticky.
    this.reconnectSafe = true;
    this.lastHeartbeat = 0; this.lastEvent = 0;
    this.performanceEnabled = execution?.performanceEnabled === true;
  }

  async heartbeat(force = false) {
    if (!this.client.session || (!force && Date.now() - this.lastHeartbeat < 5000)) return;
    const host = this.inspect();
    const reply = await this.client.request("/heartbeat", { protocol_version: "ps-bridge/v1", envelope_kind: "request", body: {
      correlation_id: "corr_heartbeat", sent_at: new Date().toISOString(), idempotency_key: "heartbeat_session",
      operation: "heartbeat", payload: { host_state: host.ownedModal ? "busy" : host.ready ? "ready" : "blocked",
        modal_state: host.ownedModal ? "running" : host.modal ? "unknown" : "idle", last_event_seq: this.lastEvent } } });
    if (!reply.body.ok) throw new Error("BRIDGE_HOST_BLOCKED");
    this.lastHeartbeat = Date.now();
  }

  async tick() {
    if (this.busy || this.stopped) return;
    this.busy = true;
    try {
      if (!this.client.session) { await this.releasePrepared(); return; }
      await this.heartbeat();
      // Never claim a new page or touch a resource while an external document/modal blocks us.
      if (!this.inspect().ready) {
        if (this.prepared) throw new Error("BRIDGE_HOST_BLOCKED");
        return;
      }
      if (this.prepared?.rendered && await this.observeCommit()) return;
      const wasSafe = this.reconnectSafe;
      this.reconnectSafe = false;
      let response;
      try {
        response = await this.client.request("/adapter/v1/claims", {
          transport_version: "ps-adapter/v1", kind: "claim_request", body: {} });
      } catch (error) {
        if (wasSafe && error instanceof UnsentRequestError) this.reconnectSafe = true;
        throw error;
      }
      if (!response.body.has_work) {
        // Commit may land after the preceding query but before this claim.
        // Only the authenticated completion event can reconcile that race;
        // an empty claim by itself never proves success or permits replay.
        if (this.prepared?.rendered && await this.observeCommit()) return;
        if (this.prepared) throw new Error("BRIDGE_EXECUTION_UNKNOWN");
        this.reconnectSafe = wasSafe;
        return;
      }
      const claim = response.body.claim;
      if (this.prepared) {
        const previous = this.prepared.claim;
        if (claim.claim_id !== previous.claim_id || claim.execution_id !== previous.execution_id
          || claim.plan_digest.toLowerCase() !== previous.plan_digest.toLowerCase() || claim.state !== previous.state)
          throw new Error("BRIDGE_EXECUTION_UNKNOWN");
        if (claim.cancel_requested) await this.cancel(claim);
        return;
      }
      if (claim.state !== "claimed") throw new Error("BRIDGE_EXECUTION_UNKNOWN");
      const validated = validateClaim(claim, this.client.session);
      if (claim.cancel_requested) { await this.cancel(claim); return; }
      const acknowledgement = await this.client.request(`/adapter/v1/claims/${claim.claim_id}/ack`, {
        transport_version: "ps-adapter/v1", kind: "claim_ack", body: {
          claim_id: claim.claim_id, execution_id: claim.execution_id, plan_digest: claim.plan_digest } });
      const authorized = acknowledgement.body.claim;
      if (!acknowledgement.body.has_work || authorized.claim_id !== claim.claim_id || authorized.execution_id !== claim.execution_id
        || authorized.plan_digest.toLowerCase() !== claim.plan_digest.toLowerCase()) throw new Error("BRIDGE_EXECUTION_UNKNOWN");
      if (authorized.cancel_requested) { await this.cancel(authorized); return; }
      if (authorized.state !== "authorized" || !this.inspect().ready) throw new Error("BRIDGE_HOST_BLOCKED");
      const leases = new Map();
      const performance = this.performanceEnabled
        ? createPerformanceReport(authorized.execution_id, validated.plan.document.page_id) : null;
      performance?.add("claim_ack", 0);
      this.prepared = { ...validated, claim: authorized, leases, outputs: new Map(), eventSeq: 1, performance };
      for (const resource of validated.plan.resources) {
        if (this.stopped || !this.inspect().ready) throw new Error("BRIDGE_HOST_BLOCKED");
        const stage = () => stageResource(this.client, authorized, validated.bindings.get(resource.resource_id), resource,
          this.storage, this.randomBytes, async () => {
            if (this.stopped || !this.inspect().ready) throw new Error("BRIDGE_HOST_BLOCKED");
            await this.heartbeat();
          }, performance);
        const lease = performance
          ? await performance.measureAsync("resource_stage", stage, { resource_role: resource.role, bytes: resource.byte_length })
          : await stage();
        leases.set(resource.resource_id, lease);
      }
      if (this.stopped || !this.inspect().ready) throw new Error("BRIDGE_HOST_BLOCKED");
      await this.onPrepared(this.prepared);
      if (this.execution && validated.plan.required_capabilities.includes("layout/absolute-placement-v1"))
        await this.executePrepared();
    } catch (error) {
      if (error.message === "BRIDGE_EXECUTION_CANCELLED" && this.client.session && this.prepared) {
        try { await this.cancel(this.prepared.claim); return; }
        catch (cancellationError) { error = cancellationError; }
      }
      // Clear authentication before cleanup: a lost ACK or failed download is not retryable work.
      if (error.message !== "BRIDGE_DISCONNECTED") this.reconnectSafe = false;
      this.client.clear();
      await this.releasePrepared();
      throw error;
    } finally { this.busy = false; }
  }

  async cancel(claim) {
    const sequence = (this.prepared?.eventSeq ?? 1) + 1;
    await this.releasePrepared();
    const event = { event_id: "event_" + encode(this.randomBytes(32)), event_seq: sequence,
      occurred_at: new Date().toISOString(), event_type: "execution_cancelled", execution_id: claim.execution_id, status: "cancelled" };
    const reply = await this.client.request("/adapter/v1/events", { transport_version: "ps-adapter/v1", kind: "event_report",
      body: { claim_id: claim.claim_id, event: { protocol_version: "ps-bridge/v1", envelope_kind: "event", body: event } } });
    if (reply.body.execution_id !== claim.execution_id || reply.body.event_seq !== event.event_seq) throw new Error("BRIDGE_EXECUTION_UNKNOWN");
    this.lastEvent = event.event_seq;
    this.reconnectSafe = true;
  }

  async observeCommit() {
    const prepared = this.prepared, id = prepared.claim.execution_id;
    const reply = await this.client.request(`/executions/${id}?after_seq=${prepared.eventSeq}`, {
      protocol_version: "ps-bridge/v1", envelope_kind: "request", body: {
        correlation_id: "corr_commit", sent_at: new Date().toISOString(), idempotency_key: "query_commit",
        operation: "get_execution", execution_id: id, payload: {} } }, "GET");
    const body = reply.body;
    if (!body.ok || body.execution_id !== id) throw new Error("BRIDGE_EXECUTION_UNKNOWN");
    if (body.execution_status === "running" && body.event_seq === prepared.eventSeq && body.events.length === 0) return false;
    const event = body.events?.[0];
    if (body.execution_status !== "completed" || body.event_seq !== prepared.eventSeq + 1 || body.events?.length !== 1
      || event.execution_id !== id || event.event_seq !== body.event_seq || event.event_type !== "execution_completed"
      || event.status !== "completed" || event.error != null) throw new Error("BRIDGE_EXECUTION_UNKNOWN");
    await this.releasePrepared();
    this.lastEvent = body.event_seq;
    // The next authenticated ready heartbeat acknowledges release before the desktop closes the session.
    await this.heartbeat(true);
    this.reconnectSafe = true;
    await this.execution.onCommitted?.({ execution_id: id, event_seq: body.event_seq });
    return true;
  }

  async executionBoundary(openingPulse = false) {
    const host = this.inspect();
    if (this.stopped || !this.client.session || (!host.ready
      && !(openingPulse && host.openingCopy === true && host.ownedModal === true)))
      throw Object.assign(new Error("BRIDGE_HOST_BLOCKED"), { hostState: {
        ready: host.ready === true, modal: host.modal === true, ownedModal: host.ownedModal === true,
        ownedDocument: host.ownedDocument === true, openingCopy: host.openingCopy === true } });
    await this.heartbeat(true);
    const response = await this.client.request("/adapter/v1/claims", {
      transport_version: "ps-adapter/v1", kind: "claim_request", body: {} });
    const claim = response.body.claim, expected = this.prepared.claim;
    if (!response.body.has_work || claim.claim_id !== expected.claim_id || claim.execution_id !== expected.execution_id
      || claim.plan_digest.toLowerCase() !== expected.plan_digest.toLowerCase() || claim.state !== expected.state)
      throw new Error("BRIDGE_EXECUTION_UNKNOWN");
    if (claim.cancel_requested) throw new Error("BRIDGE_EXECUTION_CANCELLED");
  }

  async report(type, status, error = null) {
    const prepared = this.prepared;
    const event = { event_id: "event_" + encode(this.randomBytes(32)), event_seq: prepared.eventSeq + 1,
      occurred_at: new Date().toISOString(), event_type: type, execution_id: prepared.claim.execution_id,
      page_id: prepared.plan.document.page_id, status };
    if (error) event.error = error;
    const response = await this.client.request("/adapter/v1/events", { transport_version: "ps-adapter/v1", kind: "event_report",
      body: { claim_id: prepared.claim.claim_id, event: { protocol_version: "ps-bridge/v1", envelope_kind: "event", body: event } } });
    if (response.body.execution_id !== event.execution_id || response.body.event_seq !== event.event_seq)
      throw new Error("BRIDGE_EXECUTION_UNKNOWN");
    prepared.eventSeq = event.event_seq; this.lastEvent = event.event_seq;
  }

  async executePrepared() {
    const prepared = this.prepared;
    if (!prepared.plan.required_capabilities.every(capability => this.client.session.capabilities?.includes(capability)))
      throw new Error("BRIDGE_HOST_BLOCKED");
    await this.executionBoundary();
    await this.report("page_started", "running");
    prepared.claim = { ...prepared.claim, state: "running" };
    let result;
    try {
      result = prepared.performance
        ? await prepared.performance.measureAsync("photoshop_execute", () => this.execution.executor.execute(prepared,
          () => this.executionBoundary(), () => this.executionBoundary(true)))
        : await this.execution.executor.execute(prepared, () => this.executionBoundary(), () => this.executionBoundary(true));
    } catch (error) {
      const failure = this.execution.executor.failure;
      // Only an explicit local plan rejection with proven Photoshop cleanup can
      // be terminal. Lost ACKs, disconnects, unexpected exceptions and uncertain
      // document/output cleanup remain unknown and are never retried here.
      if (error?.message !== "BRIDGE_PLAN_INVALID" || failure?.code !== error.message
        || failure.cleanupConfirmed !== true || !this.client.session || !this.inspect().ready) throw error;
      const cleanup = await Promise.allSettled([...prepared.leases.values()].map(lease => lease.dispose()));
      if (cleanup.some(item => item.status === "rejected")) throw new Error("BRIDGE_RESOURCE_BINDING_INVALID");
      prepared.leases.clear();
      await this.report("page_failed", "failed", { code: "BRIDGE_PLAN_INVALID", retryable: false,
        scope: "request", user_action: "请返回生成前检查并重新准备计划。" });
      this.prepared = null;
      this.reconnectSafe = true;
      await this.execution.onFailed?.({ execution_id: prepared.claim.execution_id,
        event_seq: prepared.eventSeq, code: failure.code, temporary_resources_released: true });
      return;
    }
    prepared.outputs = result.outputs;
    const receipts = [];
    for (const [kind, lease] of prepared.outputs) {
      const upload = () => uploadOutput(this.client, prepared, kind, lease, this.storage, this.execution.fs,
        () => this.executionBoundary(), prepared.performance);
      receipts.push(prepared.performance
        ? await prepared.performance.measureAsync("output_upload", upload, { output_kind: kind })
        : await upload());
    }
    if (prepared.performance) await prepared.performance.measureAsync("completion_boundary", () => this.executionBoundary());
    else await this.executionBoundary();
    if (prepared.performance) await prepared.performance.measureAsync("page_completed_event", () => this.report("page_completed", "running"));
    else await this.report("page_completed", "running");
    await this.execution.onRendered?.({ ...result.evidence, outputs: receipts });
    // Keep the delivery identity until the desktop commits/checkpoints or cancels it. Never re-execute it.
    await Promise.all([...prepared.outputs.values()].map(lease => lease.dispose()));
    prepared.outputs.clear();
    prepared.rendered = true;
    if (prepared.performance) {
      const performance = prepared.performance;
      prepared.performance = null;
      await writePerformanceReport(this.storage, performance);
    }
  }

  async releasePrepared() {
    const prepared = this.prepared;
    this.prepared = null;
    if (!prepared) return;
    const results = await Promise.allSettled([...prepared.leases.values(), ...prepared.outputs.values()].map(lease => lease.dispose()));
    if (results.some(result => result.status === "rejected")) throw new Error("BRIDGE_RESOURCE_BINDING_INVALID");
  }

  async stop() {
    this.stopped = true;
    // A running tick owns cleanup until its next boundary; never delete a file being written.
    if (!this.busy) await this.releasePrepared();
  }
}

module.exports = { AdapterWorkLoop };
