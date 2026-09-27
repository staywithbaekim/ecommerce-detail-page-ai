"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const { createHash } = require("node:crypto");
const { uploadOutput } = require("../src/outputs");
const { AuthenticatedCancellation } = require("../src/client");
const validators = require("../dist/validators.cjs");
const hash = bytes => createHash("sha256").update(bytes).digest("hex");

function fixture() {
  const bytes = Buffer.alloc(256 * 1024 + 17);
  for (let i = 0; i < bytes.length; i++) bytes[i] = i % 251;
  const state = { reads: 0, closes: 0, opens: 0, requests: [], received: [], mutate: () => {}, boundary: 0 };
  const entry = { getMetadata: async () => ({ size: bytes.length }) };
  const lease = { getEntry: () => entry };
  const storage = { localFileSystem: { getFsUrl(value) { assert.equal(value, entry); return "plugin-temp:/owned.jpg"; } } };
  const fs = { async open(url, mode) { assert.equal(url, "plugin-temp:/owned.jpg"); assert.equal(mode, "r"); state.opens++; return 7; },
    async read(fd, buffer, offset, length, position) {
      assert.equal(fd, 7); assert.ok(buffer.byteLength <= 128 * 1024);
      state.reads++;
      // Exercise documented short reads without restarting a chunk.
      const count = Math.min(4093, length);
      new Uint8Array(buffer).set(bytes.subarray(position, position + count), offset);
      return { bytesRead: count, buffer };
    }, async close(fd) { assert.equal(fd, 7); state.closes++; } };
  const prepared = { claim: { claim_id: "claim_test", execution_id: "execution_test", plan_digest: "sha256:" + "a".repeat(64) },
    output: { output_root_handle: "Z".repeat(43), artifact_set_id: "artifact_test" },
    plan: { output: { jpg: { enabled: true }, editable_psd: { enabled: false } } } };
  const client = { session: {}, clear() { this.session = null; }, async request(path, envelope) {
    assert.equal(validators.adapter(envelope), true, JSON.stringify(validators.adapter.errors));
    assert.ok(!JSON.stringify(envelope).includes("plugin-temp"));
    state.requests.push(envelope.kind);
    const b = envelope.body;
    if (envelope.kind === "output_write") {
      const chunk = Buffer.from(b.data, "base64url");
      assert.equal(b.sha256, hash(chunk)); assert.equal(chunk.length, b.count);
      assert.equal(b.offset, Buffer.concat(state.received).length);
      state.received.push(chunk);
    } else assert.equal(b.sha256, hash(Buffer.concat(state.received)));
    const reply = { transport_version: "ps-adapter/v1", kind: "output_receipt", body: {
      execution_id: b.execution_id, artifact_set_id: b.artifact_set_id, output_kind: b.output_kind,
      next_offset: envelope.kind === "output_write" ? b.offset + b.count : b.byte_length, sealed: envelope.kind === "output_seal" } };
    state.mutate(reply, envelope);
    return reply;
  } };
  return { state, bytes, fs, entry, storage, client, prepared, run: (kind = "jpg") => uploadOutput(client, prepared, kind, lease, storage, fs,
    async () => { state.boundary++; }) };
}

test("UXP output short reads stream bounded chunks and verify desktop byte receipts", async () => {
  const f = fixture(); const result = await f.run();
  assert.deepEqual(Buffer.concat(f.state.received), f.bytes);
  assert.deepEqual(result, { output_kind: "jpg", byte_length: f.bytes.length, sha256: hash(f.bytes) });
  assert.equal(f.state.requests.filter(x => x === "output_write").length, 3);
  assert.equal(f.state.requests.at(-1), "output_seal");
  assert.equal(f.state.closes, 1); assert.equal(f.state.boundary, 4);
  assert.ok(f.client.session);
});

test("opt-in output timings separate boundary RPCs, local reads, encoding, and writes", async () => {
  const f = fixture(), phases = [];
  const performance = { add(name, ms, fields) { phases.push({ name, ms, ...fields }); },
    measure(name, callback, fields) { const value = callback(); this.add(name, 1, fields); return value; },
    async measureAsync(name, callback, fields) { const value = await callback(); this.add(name, 1, fields); return value; } };
  await uploadOutput(f.client, f.prepared, "jpg", { getEntry: () => f.entry }, f.storage, f.fs,
    async () => { f.state.boundary++; }, performance);
  assert.equal(phases.filter(p => p.name === "output_boundary").length, 4);
  assert.equal(phases.filter(p => p.name === "output_temp_read").length, 3);
  assert.equal(phases.filter(p => p.name === "output_chunk_encode").length, 3);
  assert.equal(phases.filter(p => p.name === "output_write_rpc").length, 3);
  assert.equal(phases.filter(p => p.name === "output_seal_rpc").length, 1);
  assert.ok(phases.every(p => p.output_kind === "jpg"));
});

test("verification PSD receipts are rejected and strict editable PSD receipts remain supported", async () => {
  const f = fixture();
  await assert.rejects(f.run("verification_psd"), /BRIDGE_RESOURCE_BINDING_INVALID/);
  assert.equal(f.state.opens, 0);
  const finalPsd = fixture(); finalPsd.prepared.plan.required_capabilities = ["output/verification-psd-v1"];
  finalPsd.prepared.plan.output.editable_psd.enabled = true;
  await assert.rejects(finalPsd.run("verification_psd"), /BRIDGE_RESOURCE_BINDING_INVALID/);
  assert.equal(finalPsd.state.opens, 0);
  const strict = fixture(); strict.prepared.plan.output.editable_psd.enabled = true;
  assert.equal((await strict.run("editable_psd")).output_kind, "editable_psd");
});

for (const field of ["execution_id", "artifact_set_id", "output_kind", "next_offset", "sealed"])
  test(`output receipt ${field} mismatch closes local handle, clears authentication and never retries`, async () => {
    const f = fixture(); f.state.mutate = reply => { reply.body[field] = "mismatched"; };
    await assert.rejects(f.run(), /BRIDGE_RESOURCE_BINDING_INVALID/);
    assert.equal(f.state.requests.length, 1); assert.equal(f.state.closes, 1); assert.equal(f.client.session, null);
  });

test("disabled PSD and oversized output are rejected before file opening", async () => {
  const f = fixture(); await assert.rejects(f.run("editable_psd"), /BRIDGE_RESOURCE_BINDING_INVALID/);
  assert.equal(f.state.opens, 0);
  const large = fixture(); large.entry.getMetadata = async () => ({ size: 2147483649 });
  await assert.rejects(large.run(), /BRIDGE_RESOURCE_BINDING_INVALID/); assert.equal(large.state.opens, 0);
});

test("read failure or truncated file never sends a partial chunk or seals output", async () => {
  for (const read of [async () => ({ bytesRead: 0 }), async () => { throw Error("private path"); }]) {
    const f = fixture(); f.fs.read = read;
    await assert.rejects(f.run(), /^Error: BRIDGE_RESOURCE_BINDING_INVALID$/);
    assert.equal(f.state.requests.length, 0); assert.equal(f.state.closes, 1); assert.equal(f.client.session, null);
    assert.deepEqual(f.client.outputDiagnostic, { kind: "jpg", stage: "read", offset: 0, code: "BRIDGE_RESOURCE_BINDING_INVALID" });
    assert.equal(JSON.stringify(f.client.outputDiagnostic).includes("private path"), false);
  }
});

test("lost final receipt leaves output unknown and is not retried", async () => {
  const f = fixture(); f.state.mutate = (_, request) => { if (request.kind === "output_seal") throw Error("lost receipt"); };
  await assert.rejects(f.run(), /BRIDGE_RESOURCE_BINDING_INVALID/);
  assert.equal(f.state.requests.filter(x => x === "output_seal").length, 1);
  assert.equal(f.state.closes, 1); assert.equal(f.client.session, null);
});

for (const operation of ["output_write", "output_seal"])
for (const failure of ["verified", "unverified", "close"]) {
  test(`${operation} cancellation ${failure} retains authentication only after local handle release`, async () => {
    const f = fixture();
    f.state.mutate = (_, request) => {
      if (request.kind !== operation) return;
      if (failure === "unverified") throw new Error("BRIDGE_EXECUTION_CANCELLED");
      throw new AuthenticatedCancellation();
    };
    if (failure === "close") f.fs.close = async () => { f.state.closes++; throw new Error("private file"); };
    await assert.rejects(f.run());
    assert.equal(f.client.session !== null, failure === "verified");
    assert.ok(f.state.closes >= 1);
    assert.ok(f.state.requests.filter(kind => kind === operation).length <= 1);
  });
}
