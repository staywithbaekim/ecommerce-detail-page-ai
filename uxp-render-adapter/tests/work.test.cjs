"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const { createHash, randomBytes } = require("node:crypto");
const { AdapterWorkLoop } = require("../src/work");
const { AuthenticatedCancellation } = require("../src/client");
const { UnsentRequestError } = require("../src/socket");
const { computePlanDigest, validateClaim } = require("../src/plan");
const { parseStrict } = require("../src/json");
const validators = require("../dist/validators.cjs");
const hash = bytes => createHash("sha256").update(bytes).digest("hex");
const example = JSON.parse(fs.readFileSync(require("node:path").join(__dirname, "../../docs/技术方案/schemas/render-plan-v1.valid.example.json"), "utf8"));

for (const mode of ["valid", "missing_capability", "missing_identity"]) {
  test(`explicit output variant ${mode} is validated before ACK`, async () => {
    const f = fixture(true), plan = f.claim.plan.body.payload.render_plan;
    if (mode !== "missing_identity") plan.document.output_variant_id = "color_a";
    if (mode !== "missing_capability") plan.required_capabilities.push("output/variant-identity-v1");
    plan.plan_digest = computePlanDigest(JSON.stringify(plan));
    f.claim.plan_digest = plan.plan_digest; f.claim.plan.body.payload.plan_digest = plan.plan_digest;
    if (mode === "valid") {
      await f.loop.tick(); assert.equal(f.state.authorized, true); assert.equal(f.state.executions, 1);
      await f.loop.stop();
    } else {
      await assert.rejects(f.loop.tick()); assert.equal(f.state.authorized, false); assert.equal(f.state.creates, 0);
    }
  });
}

function fixture(executing = false) {
  const plan = structuredClone(example);
  if (executing) {
    plan.required_capabilities.push("layout/absolute-placement-v1");
    plan.targets[0].layout.placement = { coordinate_space: "document_pixels", source_width: 4000, source_height: 6000,
      left: 0, top: 0, width: 800, height: 1200, clipping: "preserve_template_masks_and_canvas" };
  }
  const content = plan.resources.map((_, i) => Buffer.alloc(128 * 1024 + 13, i + 1));
  const bindings = plan.resources.map((resource, i) => {
    resource.sha256 = hash(content[i]); resource.byte_length = content[i].length;
    return { resource_id: resource.resource_id, session_handle: String.fromCharCode(65 + i).repeat(43),
      sha256: resource.sha256, purpose: resource.role, read_only: true, byte_length: resource.byte_length };
  });
  plan.plan_digest = computePlanDigest(JSON.stringify(plan));
  const session = { session_id: "session_test", token: "T".repeat(43), capabilities: plan.required_capabilities };
  const claim = { claim_id: "claim_test", execution_id: "execution_test", plan_digest: plan.plan_digest,
    state: "claimed", cancel_requested: false, plan: { protocol_version: "ps-bridge/v1", envelope_kind: "request", body: {
      request_id: "request_test", correlation_id: plan.correlation_id, session_id: session.session_id, token: session.token, sent_at: new Date().toISOString(),
      idempotency_key: plan.idempotency_key, operation: "submit_plan", payload: {
        execution_scope: "page", render_plan: plan, plan_digest: plan.plan_digest, resource_bindings: bindings,
        output_bindings: [{ artifact_set_id: plan.output.artifact_set_id, output_root_handle: "Z".repeat(43),
          overwrite_policy: "never", confirmed_target_ids: [] }] } } } };
  const state = { authorized: false, requests: [], creates: 0, deletes: 0, prepared: 0, ready: true, mutate: () => {},
    files: [], source: Buffer.from("unrelated original"), cancelled: false, running: false, events: [], executions: 0, rendered: 0 };
  const client = { session, clear() { this.session = null; }, async request(path, envelope) {
    state.requests.push(path);
    let response;
    if (path === "/heartbeat") response = { body: { ok: true } };
    else if (path === "/adapter/v1/claims" || path.endsWith("/ack")) {
      if (path.endsWith("/ack")) state.authorized = true;
      const returned = state.authorized ? { ...claim, state: state.running ? "running" : "authorized", plan: undefined } : claim;
      response = { transport_version: "ps-adapter/v1", kind: "claim_response",
        body: { has_work: true, claim: { ...returned, cancel_requested: state.cancelled } } };
      state.mutate(response, path);
      assert.equal(validators.adapter(response), true, JSON.stringify(validators.adapter.errors));
      return parseStrict(JSON.stringify(response));
    } else if (path === "/adapter/v1/resources/read") {
      assert.equal(state.authorized, true);
      const b = envelope.body, index = bindings.findIndex(binding => binding.session_handle === b.session_handle);
      const bytes = content[index].subarray(b.offset, b.offset + b.count);
      response = { body: { execution_id: claim.execution_id, plan_digest: claim.plan_digest, session_handle: b.session_handle,
        offset: b.offset, count: b.count, sha256: hash(bytes), data: bytes.toString("base64url") }, kind: "resource_chunk" };
    } else if (path === "/adapter/v1/events") {
      const event = envelope.body.event.body;
      assert.equal(event.event_seq, state.events.length + 2);
      state.events.push(event);
      if (event.event_type === "page_started") state.running = true;
      response = { body: { execution_id: claim.execution_id, event_seq: event.event_seq } };
    } else if (path.startsWith("/executions/")) {
      response = { body: { ok: true, execution_id: claim.execution_id, execution_status: "running", event_seq: 3, events: [] } };
    } else throw new Error("Unexpected request");
    state.mutate(response, path);
    return response;
  } };
  const storage = { formats: { binary: "binary" }, localFileSystem: { getTemporaryFolder: async () => ({
    async createFile(name, options) {
      assert.equal(options.overwrite, false);
      state.creates++;
      const file = { bytes: Buffer.alloc(0), deleted: false,
        async write(data, options) {
          this.bytes = options.append ? Buffer.concat([this.bytes, Buffer.from(data)]) : Buffer.from(data);
          return data.byteLength;
        },
        async getMetadata() { return { size: this.bytes.length }; },
        async delete() { assert.equal(this.deleted, false); this.deleted = true; state.deletes++; }
      };
      state.files.push(file);
      return file;
    }
  }) } };
  const loop = new AdapterWorkLoop(client, storage, randomBytes, () => ({ ready: state.ready, modal: !state.ready }),
    async prepared => { state.prepared++; assert.equal(prepared.leases.size, content.length); }, executing ? {
      executor: { async execute(prepared, boundary) {
        state.executions++;
        await boundary();
        return { outputs: new Map(), evidence: { output_executed: true } };
      } }, onRendered: async () => { state.rendered++; }
    } : null);
  return { loop, client, state, claim, content, session };
}

for (const mode of ["valid", "missing_capability", "old_executor", "wrong_source", "wrong_asset", "duplicate_slot", "variant_collision", "wrong_digest", "unknown_field"]) {
  test(`frozen instance ${mode} is checked before ACK and resource creation`, async () => {
    const f = fixture(true), plan = f.claim.plan.body.payload.render_plan;
    plan.required_capabilities.push("template/page-instance-identity-v1");
    plan.document.instance_identity = {
      page_instance_id: "instance_" + "a".repeat(64), snapshot_id: "snapshot_test",
      rule_digest: "sha256:" + "b".repeat(64), input_facts_digest: "sha256:" + "c".repeat(64),
      projection_digest: "sha256:" + "d".repeat(64), source_page_id: plan.document.page_id,
      branch_id: "branch", recipe_id: "recipe", repetition_index: 0, stable_order: 0,
      slots: [{ slot_id: "slot", variant_id: "red" }], frames: plan.targets.map(target => ({
        frame_id: target.frame_id, slot_id: "slot", variant_id: "red", asset_id: target.asset_resource_id }))
    };
    const identity = plan.document.instance_identity;
    if (mode === "missing_capability") plan.required_capabilities.pop();
    if (mode === "old_executor") f.client.session.capabilities = plan.required_capabilities.filter(item => item !== "template/page-instance-identity-v1");
    if (mode === "wrong_source") identity.source_page_id = "page_other";
    if (mode === "wrong_asset") identity.frames[0].asset_id = "asset_other";
    if (mode === "duplicate_slot") identity.slots.push({ ...identity.slots[0] });
    if (mode === "variant_collision") plan.document.output_variant_id = identity.page_instance_id;
    if (mode === "wrong_digest") identity.projection_digest = "sha256:bad";
    if (mode === "unknown_field") identity.loop = "all_colors";
    plan.plan_digest = computePlanDigest(JSON.stringify(plan));
    f.claim.plan_digest = plan.plan_digest; f.claim.plan.body.payload.plan_digest = plan.plan_digest;
    if (mode === "valid") {
      await f.loop.tick(); assert.equal(f.state.authorized, true); assert.equal(f.state.executions, 1); await f.loop.stop();
    } else {
      await assert.rejects(f.loop.tick()); assert.equal(f.state.authorized, false); assert.equal(f.state.creates, 0);
    }
  });
}

function insertionFixture() {
  const f = fixture(true), plan = f.claim.plan.body.payload.render_plan;
  const reference = { layer_path: [2], layer_path_order: "psd_bottom_to_top", layer_id: 2, kind: "group",
    name: "old size table", structure_signature: plan.targets[0].expected_layer.structure_signature };
  plan.document.content_type = "dynamic_size_table"; plan.targets = [];
  plan.required_capabilities.push("size-table/insert-layer-v1");
  plan.resources[1].role = "deterministic_size_table"; plan.resources[1].media_type = "image/png";
  f.claim.plan.body.payload.resource_bindings[1].purpose = "deterministic_size_table";
  plan.size_table = { size_table_id: "size_insert", grid_digest: "sha256:" + plan.resources[1].sha256,
    structured_data_digest: "sha256:" + "c".repeat(64), style_version: "style_v1", layout_version: "layout_v1",
    render_resource_id: plan.resources[1].resource_id, embed_as: "smart_object",
    columns: [{column_id:"col_1", label:"尺码",index:0}],rows:[{row_id:"row_1",index:0,cells:{col_1:"L"}}],
    insertion: { layer_name: "inserted size table", above_layer: reference, hidden_layers: [structuredClone(reference)],
      safe_bounds: { left:10,top:20,right:780,bottom:1200 }, placement: { coordinate_space:"document_pixels",
        source_width:1000,source_height:400,left:10,top:20,width:750,height:300,clipping:"preserve_template_masks_and_canvas" } } };
  return { ...f, plan };
}

for (const mode of ["pulse", "boundary", "foreign_modal", "not_opening", "stopped", "session_lost", "cancelled", "wrong_claim"]) {
  test(`pending owned open permits authenticated liveness only: ${mode}`, async () => {
    const f = fixture();
    f.loop.prepared = { claim: f.claim };
    f.loop.inspect = () => ({ ready: false, modal: true,
      ownedModal: mode !== "foreign_modal", openingCopy: mode !== "not_opening" });
    if (mode === "stopped") f.loop.stopped = true;
    if (mode === "session_lost") f.client.clear();
    if (mode === "cancelled") f.state.cancelled = true;
    if (mode === "wrong_claim") f.state.mutate = (reply, path) => {
      if (path === "/adapter/v1/claims") reply.body.claim.execution_id = "execution_other";
    };
    if (mode === "pulse") await f.loop.executionBoundary(true);
    else await assert.rejects(f.loop.executionBoundary(mode !== "boundary"),
      mode === "cancelled" ? /BRIDGE_EXECUTION_CANCELLED/
        : mode === "wrong_claim" ? /BRIDGE_EXECUTION_UNKNOWN/ : /BRIDGE_HOST_BLOCKED/);
    assert.equal(f.state.executions, 0);
    assert.equal(f.state.creates, 0);
    assert.deepEqual(f.state.events, []);
    if (!["pulse", "cancelled", "wrong_claim"].includes(mode)) assert.deepEqual(f.state.requests, []);
  });
}

for (const mutation of ["valid", "capability", "both_modes", "missing", "nested", "duplicate", "crop", "stretch", "market", "grid", "ordinary"]) {
  test(`size insertion claim ${mutation} is checked before ACK or resource creation`, async () => {
    const f = insertionFixture(), p = f.plan, i = p.size_table.insertion;
    if (mutation === "capability") p.required_capabilities.pop();
    if (mutation === "both_modes") p.required_capabilities.push("size-table/calibrated-target-v1");
    if (mutation === "missing") delete p.size_table.insertion;
    if (mutation === "nested") i.above_layer.layer_path.push(1);
    if (mutation === "duplicate") i.hidden_layers.push(structuredClone(i.hidden_layers[0]));
    if (mutation === "crop") i.placement.left=0;
    if (mutation === "stretch") i.placement.width=700;
    if (mutation === "market") p.resources[1].media_type="image/jpeg";
    if (mutation === "grid") p.size_table.grid_digest="sha256:"+"f".repeat(64);
    if (mutation === "ordinary") p.document.content_type="ordinary_set";
    p.plan_digest=computePlanDigest(JSON.stringify(p)); f.claim.plan_digest=p.plan_digest;
    f.claim.plan.body.payload.plan_digest=p.plan_digest;
    if (mutation === "valid") {
      await f.loop.tick(); assert.equal(f.state.authorized,true); assert.equal(f.state.executions,1);
      await f.loop.stop();
    } else {
      await assert.rejects(f.loop.tick()); assert.equal(f.state.authorized,false); assert.equal(f.state.creates,0);
    }
  });
}

test("desktop committed event releases temporary leases without repeating Photoshop work", async () => {
  const f = fixture(true);
  await f.loop.tick();
  let observed = 0;
  f.loop.execution.onCommitted = async evidence => { observed++; assert.equal(evidence.event_seq, 4); };
  f.state.mutate = (response, path) => {
    if (path.startsWith("/executions/")) Object.assign(response.body, { execution_status: "completed", event_seq: 4,
      events: [{ execution_id: "execution_test", event_seq: 4, event_type: "execution_completed", status: "completed" }] });
  };
  await f.loop.tick();
  assert.equal(observed, 1); assert.equal(f.state.executions, 1);
  assert.equal(f.loop.prepared, null); assert.equal(f.state.deletes, f.state.creates);
  assert.notEqual(f.client.session, null);
  assert.equal(f.loop.reconnectSafe, true);
});

test("faster idle work queries retain the five-second heartbeat throttle", async t => {
  const f = fixture();
  let now = 10000;
  t.mock.method(Date, "now", () => now);
  f.state.mutate = (response, path) => { if (path === "/adapter/v1/claims") response.body = { has_work: false }; };
  for (let i = 0; i <= 20; i++, now += 250) await f.loop.tick();
  assert.equal(f.state.requests.filter(path => path === "/heartbeat").length, 2);
  assert.equal(f.state.requests.filter(path => path === "/adapter/v1/claims").length, 21);
  assert.equal(f.state.creates, 0);
  assert.equal(f.state.executions, 0);
});

test("page_completed retains input leases until authenticated commit and cleanup precedes the release ACK", async () => {
  const f = fixture(true);
  await f.loop.tick();
  assert.equal(f.loop.prepared.rendered, true);
  assert.equal(f.state.deletes, 0);
  let releaseAck = 0;
  const request = f.client.request.bind(f.client);
  f.client.request = async (path, envelope) => {
    if (path === "/heartbeat" && envelope.body.payload.last_event_seq === 4) {
      assert.equal(f.state.deletes, f.state.creates);
      assert.equal(f.loop.prepared, null);
      releaseAck++;
    }
    return request(path, envelope);
  };
  f.state.mutate = (response, path) => {
    if (path.startsWith("/executions/")) Object.assign(response.body, { execution_status: "completed", event_seq: 4,
      events: [{ execution_id: "execution_test", event_seq: 4, event_type: "execution_completed", status: "completed" }] });
  };
  await f.loop.tick();
  assert.equal(releaseAck, 1);
  assert.equal(f.state.executions, 1);
});

test("failed commit cleanup cannot acknowledge release or claim another page", async () => {
  const f = fixture(true);
  await f.loop.tick();
  f.state.files[0].delete = async () => { throw Error("cleanup failed"); };
  f.state.mutate = (response, path) => {
    if (path.startsWith("/executions/")) Object.assign(response.body, { execution_status: "completed", event_seq: 4,
      events: [{ execution_id: "execution_test", event_seq: 4, event_type: "execution_completed", status: "completed" }] });
  };
  const claims = f.state.requests.filter(path => path === "/adapter/v1/claims").length;
  await assert.rejects(f.loop.tick(), /BRIDGE_RESOURCE_BINDING_INVALID/);
  assert.equal(f.loop.lastEvent, 3);
  assert.equal(f.client.session, null);
  assert.equal(f.state.requests.filter(path => path === "/adapter/v1/claims").length, claims);
});

for (const outcome of ["completed", "running", "wrong_identity", "missing_event"]) {
  test(`commit between query and empty claim is reconciled only with valid completion: ${outcome}`, async () => {
    const f = fixture(true); await f.loop.tick();
    let queries = 0, committed = 0;
    f.loop.execution.onCommitted = async () => { committed++; };
    f.state.mutate = (response, path) => {
      if (path === "/adapter/v1/claims") response.body = { has_work: false };
      if (path.startsWith("/executions/") && ++queries > 1 && outcome !== "running") {
        Object.assign(response.body, { execution_status: "completed", event_seq: 4,
          events: [{ execution_id: "execution_test", event_seq: 4, event_type: "execution_completed", status: "completed" }] });
        if (outcome === "wrong_identity") response.body.execution_id = "execution_other";
        if (outcome === "missing_event") response.body.events = [];
      }
    };
    if (outcome === "completed") {
      await f.loop.tick();
      assert.equal(committed, 1); assert.ok(f.client.session);
      assert.equal(f.loop.lastEvent, 4); assert.equal(f.state.requests.at(-1), "/heartbeat");
    } else {
      await assert.rejects(f.loop.tick(), /BRIDGE_EXECUTION_UNKNOWN/);
      assert.equal(committed, 0); assert.equal(f.client.session, null);
    }
    assert.equal(queries, 2); assert.equal(f.state.executions, 1);
    assert.equal(f.loop.prepared, null); assert.equal(f.state.deletes, f.state.creates);
  });
}

for (const mutation of ["unknown", "identity", "sequence", "missing_event"]) test(`commit query rejects ${mutation}`, async () => {
  const f = fixture(true); await f.loop.tick();
  f.state.mutate = (response, path) => {
    if (!path.startsWith("/executions/")) return;
    Object.assign(response.body, { execution_status: "completed", event_seq: 4,
      events: [{ execution_id: "execution_test", event_seq: 4, event_type: "execution_completed", status: "completed" }] });
    if (mutation === "unknown") response.body.execution_status = "unknown";
    if (mutation === "identity") response.body.execution_id = "execution_other";
    if (mutation === "sequence") response.body.events[0].event_seq = 5;
    if (mutation === "missing_event") response.body.events = [];
  };
  await assert.rejects(f.loop.tick(), /BRIDGE_EXECUTION_UNKNOWN/);
  assert.equal(f.state.executions, 1); assert.equal(f.client.session, null);
});

test("execution emits ordered page events once, waits for desktop commit, and uses the next sequence for cancellation", async () => {
  const f = fixture(true);
  await f.loop.tick(); await f.loop.tick();
  assert.equal(f.state.executions, 1); assert.equal(f.state.rendered, 1);
  assert.deepEqual(f.state.events.map(e => [e.event_type, e.event_seq]), [["page_started", 2], ["page_completed", 3]]);
  assert.equal(f.loop.prepared.claim.state, "running");
  f.state.cancelled = true; await f.loop.tick();
  assert.equal(f.state.events[2].event_type, "execution_cancelled"); assert.equal(f.state.events[2].event_seq, 4);
  assert.equal(f.state.deletes, 2); assert.equal(f.loop.prepared, null);
});

test("cancel at an execution boundary never emits page completed or replays the execution", async () => {
  const f = fixture(true);
  f.state.mutate = (response, route) => { if (route === "/adapter/v1/claims" && f.state.running) response.body.claim.cancel_requested = true; };
  await f.loop.tick();
  assert.deepEqual(f.state.events.map(e => [e.event_type, e.event_seq]), [["page_started", 2], ["execution_cancelled", 3]]);
  assert.equal(f.state.rendered, 0); assert.equal(f.loop.prepared, null); assert.ok(f.client.session);
});

test("formal sample digest matches the existing .NET fixture", () => {
  assert.equal(computePlanDigest(JSON.stringify(example)), example.plan_digest);
});

test("a cleaned local rejection reports page_failed and preserves the authenticated session",async()=>{
  const f=fixture(true);
  f.loop.execution.executor.execute=async()=>{
    f.state.executions++;
    f.loop.execution.executor.failure={code:"BRIDGE_PLAN_INVALID",cleanupConfirmed:true};
    throw Error("BRIDGE_PLAN_INVALID");
  };
  let reported=0;f.loop.execution.onFailed=async evidence=>{reported++;assert.equal(evidence.temporary_resources_released,true);};
  await f.loop.tick();
  assert.deepEqual(f.state.events.map(e=>e.event_type),["page_started","page_failed"]);
  const failure=f.state.events[1];assert.equal(failure.status,"failed");assert.equal(failure.error.code,"BRIDGE_PLAN_INVALID");
  assert.equal(validators.bridge({protocol_version:"ps-bridge/v1",envelope_kind:"event",body:failure}),true);
  assert.equal(f.state.executions,1);assert.equal(f.state.rendered,0);assert.equal(f.state.deletes,f.state.creates);
  assert.equal(f.loop.prepared,null);assert.ok(f.client.session);assert.equal(reported,1);
  f.state.mutate=(response,path)=>{if(path==="/adapter/v1/claims")response.body={has_work:false};};
  await f.loop.tick();assert.equal(f.state.executions,1);
});

for(const fault of ["unconfirmed","disconnected","lost_failure_ack","resource_cleanup"]) {
  test(`page rejection remains unknown after ${fault}`,async()=>{
    const f=fixture(true);
    f.loop.execution.executor.execute=async()=>{
      f.loop.execution.executor.failure={code:"BRIDGE_PLAN_INVALID",cleanupConfirmed:fault!=="unconfirmed"};
      if(fault==="resource_cleanup")f.state.files[0].delete=async()=>{throw Error("delete failed");};
      throw Error(fault==="disconnected"?"BRIDGE_DISCONNECTED":"BRIDGE_PLAN_INVALID");
    };
    if(fault==="lost_failure_ack")f.state.mutate=(response,path)=>{
      if(path==="/adapter/v1/events"&&f.state.events.at(-1)?.event_type==="page_failed")throw Error("BRIDGE_DISCONNECTED");
    };
    await assert.rejects(f.loop.tick());assert.equal(f.client.session,null);assert.equal(f.loop.prepared,null);
    assert.equal(f.state.rendered,0);
    if(fault!=="lost_failure_ack")assert.deepEqual(f.state.events.map(e=>e.event_type),["page_started"]);
  });
}

for (const mutation of ["valid", "missing_capability", "product", "market_jpeg", "grid_digest", "crop", "mask", "second_target"]) {
  test(`calibrated size-table claim ${mutation} is checked before ACK or resource creation`, async () => {
    const f = fixture(true), payload = f.claim.plan.body.payload, plan = payload.render_plan;
    plan.document.content_type = "dynamic_size_table";
    plan.required_capabilities.push("size-table/calibrated-target-v1");
    plan.resources[1].role = payload.resource_bindings[1].purpose = "deterministic_size_table";
    plan.resources[1].media_type = "image/png";
    plan.targets[0].layout.intent = "size_table_complete";
    Object.assign(plan.targets[0].layout.placement, { width: 700, height: 1050 });
    plan.size_table = { size_table_id: "table_1", grid_digest: "sha256:" + plan.resources[1].sha256,
      style_version: "style_1", layout_version: "grid_1", embed_as: "smart_object",
      render_resource_id: plan.resources[1].resource_id, structured_data_digest: "sha256:" + "c".repeat(64),
      columns: [{ column_id: "col_1", label: "尺码", index: 0 }], rows: [{ row_id: "row_1", index: 0, cells: { col_1: "L" } }] };
    if (mutation === "missing_capability") plan.required_capabilities.pop();
    if (mutation === "product") plan.resources[1].role = payload.resource_bindings[1].purpose = "high_resolution_render_source";
    if (mutation === "market_jpeg") plan.resources[1].media_type = "image/jpeg";
    if (mutation === "grid_digest") plan.size_table.grid_digest = "sha256:" + "e".repeat(64);
    if (mutation === "crop") plan.targets[0].layout.placement.left = -1;
    if (mutation === "mask") plan.targets[0].expected_layer.user_mask = true;
    if (mutation === "second_target") plan.targets.push(structuredClone(plan.targets[0]));
    plan.plan_digest = payload.plan_digest = f.claim.plan_digest = computePlanDigest(JSON.stringify(plan));
    if (mutation === "valid") {
      assert.equal(validateClaim(f.claim, f.session).plan.size_table.render_resource_id, plan.resources[1].resource_id);
      await f.loop.tick();
      assert.equal(f.state.authorized, true); assert.equal(f.state.creates, 2); assert.equal(f.state.executions, 1);
      await f.loop.stop();
    } else {
      await assert.rejects(f.loop.tick(), /BRIDGE_PLAN_INVALID/);
      assert.equal(f.state.authorized, false); assert.equal(f.state.creates, 0); assert.equal(f.state.executions, 0);
    }
  });
}

test("canonical UTF-8 escaping and lexical decimals match the .NET interop vectors", () => {
  const unicode = JSON.stringify({ plan_digest: "unused", z: '中文<&+`"🚀', a: [1.23, 0.00001, 0, 1e-28] });
  assert.equal(computePlanDigest(unicode), "sha256:6eadd0dcaa44acf671a6099b0a404e4c2661aa515620bad0083940fdd962e724");
  const decimals = '{"plan_digest":"ignored","n":12345678901234567890123456789,"a":0.0000000000000000000000000001}';
  assert.equal(computePlanDigest(decimals), "sha256:f61ea7db7ee38b934f3b96e7639c07fdbf54a0ff83e4954e46614d9d58731726");
});

test("claim validation rejects changed authenticated semantics before filesystem access", async () => {
  for (const change of [c => c.plan.body.token = "U".repeat(43), c => c.plan.body.payload.render_plan.targets[0].layout.scale = 2,
    c => c.plan.body.payload.resource_bindings[0].sha256 = "0".repeat(64),
    c => c.plan.body.payload.resource_bindings[1].session_handle = c.plan.body.payload.resource_bindings[0].session_handle,
    c => c.plan.body.payload.output_bindings[0].artifact_set_id = "wrong_output"]) {
    const f = fixture(); change(f.claim);
    await assert.rejects(f.loop.tick(), /^Error: BRIDGE_(PLAN|RESOURCE_BINDING)_INVALID$/);
    assert.equal(f.state.creates, 0); assert.equal(f.state.authorized, false); assert.equal(f.client.session, null);
  }
});

test("a plan capability not negotiated by the authenticated session is rejected before ACK", async () => {
  const f = fixture(true);
  f.session.capabilities = f.session.capabilities.filter(item => item !== "layout/absolute-placement-v1");
  await assert.rejects(f.loop.tick(), /BRIDGE_PLAN_INVALID/);
  assert.equal(f.state.authorized, false); assert.equal(f.state.creates, 0);
});

test("one ACK stages bounded real bytes once; repeated delivery never starts or repeats execution", async () => {
  const f = fixture();
  await Promise.all([f.loop.tick(), f.loop.tick()]);
  assert.equal(f.state.prepared, 1); assert.equal(f.state.creates, 2);
  for (let i = 0; i < f.content.length; i++) assert.deepEqual(f.state.files[i].bytes, f.content[i]);
  const reads = f.state.requests.filter(path => path.endsWith("/read")).length;
  await f.loop.tick();
  assert.equal(f.state.prepared, 1); assert.equal(f.state.requests.filter(path => path.endsWith("/read")).length, reads);
  assert.equal(f.state.requests.filter(path => path.endsWith("/events")).length, 0);
  await f.loop.stop(); await f.loop.stop();
  assert.equal(f.state.deletes, 2); assert.equal(f.state.source.toString(), "unrelated original");
});

test("lost ACK clears the session without retry, staging or claiming a second execution", async () => {
  const f = fixture(); f.state.mutate = (_, path) => { if (path.endsWith("/ack")) throw new Error("BRIDGE_DISCONNECTED"); };
  await assert.rejects(f.loop.tick(), /BRIDGE_DISCONNECTED/);
  await f.loop.tick();
  assert.equal(f.state.creates, 0); assert.equal(f.state.requests.filter(path => path.endsWith("/ack")).length, 1);
  assert.equal(f.loop.reconnectSafe, false);
  await f.loop.stop(); assert.equal(f.loop.reconnectSafe, false);
});

for (const failure of ["claim_response_lost", "claim_unsent", "idle_heartbeat_lost", "idle_protocol_error"]) {
  test(`automatic reconnect preserves delivery uncertainty: ${failure}`, async () => {
    const f = fixture();
    f.state.mutate = (_, path) => {
      if (failure.startsWith("idle_") && path === "/heartbeat")
        throw Error(failure === "idle_protocol_error" ? "BRIDGE_PROTOCOL_MISMATCH" : "BRIDGE_DISCONNECTED");
      if (path === "/adapter/v1/claims") throw failure === "claim_unsent" ? new UnsentRequestError() : Error("BRIDGE_DISCONNECTED");
    };
    await assert.rejects(f.loop.tick());
    assert.equal(f.loop.prepared, null); assert.equal(f.state.creates, 0);
    assert.equal(f.loop.reconnectSafe, ["claim_unsent", "idle_heartbeat_lost"].includes(failure));
    const requests = f.state.requests.length;
    await f.loop.tick(); await f.loop.stop(); assert.equal(f.state.requests.length, requests);
  });
}

test("authenticated empty claim restores idle safety but never overrides sticky uncertainty", async () => {
  for (const safe of [true, false]) {
    const f = fixture(); f.loop.reconnectSafe = safe;
    f.state.mutate = (reply, path) => { if (path === "/adapter/v1/claims") reply.body = { has_work: false }; };
    await f.loop.tick(); assert.equal(f.loop.reconnectSafe, safe); assert.equal(f.state.creates, 0);
  }
});

test("host block before claim or after preparing protects unrelated files and stops the session", async () => {
  const f = fixture(); f.state.ready = false;
  await f.loop.tick(); assert.equal(f.state.creates, 0); assert.equal(f.state.requests.length, 1);
  f.state.ready = true; await f.loop.tick();
  f.state.ready = false; await assert.rejects(f.loop.tick(), /BRIDGE_HOST_BLOCKED/);
  assert.equal(f.state.deletes, 2); assert.equal(f.client.session, null);
});

test("authorized plan returned to a fresh loop is unknown and never re-executed", async () => {
  const f = fixture(); f.state.authorized = true;
  await assert.rejects(f.loop.tick(), /BRIDGE_EXECUTION_UNKNOWN/);
  assert.equal(f.state.creates, 0);
});

test("resource corruption cleans all new partial/completed copies and does not emit completion", async () => {
  const f = fixture();
  f.state.mutate = (response, path) => { if (path.endsWith("/read") && f.state.creates === 2) response.body.sha256 = "0".repeat(64); };
  await assert.rejects(f.loop.tick(), /BRIDGE_RESOURCE_BINDING_INVALID/);
  assert.equal(f.state.creates, 2); assert.equal(f.state.deletes, 2); assert.equal(f.state.prepared, 0);
  assert.equal(f.client.session, null);
});

for (const failure of ["none", "unverified", "cleanup", "lost_receipt"]) {
  test(`cancellation during second resource ${failure} acknowledges only after confirmed cleanup`, async () => {
    const f = fixture(true), request = f.client.request.bind(f.client);
    f.client.request = async (path, envelope) => {
      if (path.endsWith("/resources/read") && f.state.creates === 2 && envelope.body.offset > 0) {
        f.state.cancelled = true;
        if (failure === "cleanup") f.state.files[1].delete = async () => { throw new Error("private path"); };
        if (failure === "unverified") throw new Error("BRIDGE_EXECUTION_CANCELLED");
        throw new AuthenticatedCancellation();
      }
      if (path === "/adapter/v1/events") {
        assert.equal(f.state.deletes, 2);
        if (failure === "lost_receipt") throw new Error("BRIDGE_DISCONNECTED");
      }
      return request(path, envelope);
    };
    if (failure === "none") {
      await f.loop.tick();
      assert.deepEqual(f.state.events.map(e => [e.event_type, e.event_seq]), [["execution_cancelled", 2]]);
      assert.ok(f.client.session);
    } else {
      await assert.rejects(f.loop.tick());
      assert.equal(f.state.events.length, 0); assert.equal(f.client.session, null);
    }
    assert.equal(f.state.executions, 0); assert.equal(f.state.rendered, 0); assert.equal(f.loop.prepared, null);
    assert.equal(f.state.source.toString(), "unrelated original");
    assert.equal(f.state.deletes, failure === "cleanup" ? 1 : 2);
    assert.equal(f.loop.reconnectSafe, failure === "none");
  });
}

test("cancel before ACK or after staging releases only this page and acknowledges the boundary", async () => {
  for (const prepared of [false, true]) {
    const f = fixture();
    if (prepared) await f.loop.tick();
    f.state.cancelled = true; await f.loop.tick();
    assert.equal(f.state.creates, prepared ? 2 : 0); assert.equal(f.state.deletes, f.state.creates);
    assert.equal(f.loop.lastEvent, 2); assert.equal(f.loop.prepared, null);
  }
});

for (const failure of ["none", "unverified", "cleanup"]) {
  test(`cancel response during output ${failure} acknowledges only after all temporary leases release`, async () => {
    const f = fixture(true), request = f.client.request.bind(f.client);
    let outputDeleted = false, closes = 0;
    const entry = { getMetadata: async () => ({ size: 4 }) };
    f.loop.storage.localFileSystem.getFsUrl = () => "plugin-temp:/output.jpg";
    f.loop.execution.fs = { open: async () => 1,
      read: async (_fd, buffer) => { new Uint8Array(buffer).set([255, 216, 255, 0]); return { bytesRead: 4 }; },
      close: async () => { closes++; } };
    f.loop.execution.executor.execute = async () => ({ outputs: new Map([["jpg", {
      getEntry: () => entry, dispose: async () => {
        if (failure === "cleanup") throw new Error("private path");
        outputDeleted = true;
      }
    }]]), evidence: {} });
    f.client.request = async (path, envelope) => {
      if (path === "/adapter/v1/outputs/write") {
        if (failure === "unverified") throw new Error("BRIDGE_EXECUTION_CANCELLED");
        throw new AuthenticatedCancellation();
      }
      if (path === "/adapter/v1/events" && envelope.body.event.body.event_type === "execution_cancelled") {
        assert.equal(outputDeleted, true); assert.equal(f.state.deletes, 2); assert.equal(closes, 1);
      }
      return request(path, envelope);
    };
    if (failure === "none") {
      await f.loop.tick();
      assert.deepEqual(f.state.events.map(e => [e.event_type, e.event_seq]), [["page_started", 2], ["execution_cancelled", 3]]);
      assert.ok(f.client.session);
    } else {
      await assert.rejects(f.loop.tick()); assert.equal(f.client.session, null);
      assert.deepEqual(f.state.events.map(e => e.event_type), ["page_started"]);
    }
    assert.equal(f.loop.prepared, null); assert.equal(f.state.rendered, 0);
    assert.equal(f.state.source.toString(), "unrelated original");
  });
}

test("second claim identity cannot replace a prepared page", async () => {
  const f = fixture(); await f.loop.tick();
  f.state.mutate = (r, path) => { if (path.endsWith("/claims")) r.body.claim.execution_id = "execution_other"; };
  await assert.rejects(f.loop.tick(), /BRIDGE_EXECUTION_UNKNOWN/);
  assert.equal(f.state.creates, 2); assert.equal(f.state.deletes, 2);
});
