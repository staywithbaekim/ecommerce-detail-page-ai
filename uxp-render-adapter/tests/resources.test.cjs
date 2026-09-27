"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const { createHash } = require("node:crypto");
const { downloadResource, stageResource, CHUNK_LIMIT } = require("../src/resources");
const validators = require("../dist/validators.cjs");
const hash = bytes => createHash("sha256").update(bytes).digest("hex");
const claim = { claim_id: "claim_1", execution_id: "execution_1", plan_digest: "sha256:" + "a".repeat(64) };

function fixture(mutate = () => {}) {
  const bytes = Buffer.alloc(CHUNK_LIMIT * 2 + 7);
  for (let i = 0; i < bytes.length; i++) bytes[i] = i % 251;
  const binding = { session_handle: "A".repeat(43), byte_length: bytes.length, sha256: hash(bytes), read_only: true };
  const client = {
    calls: 0, cleared: false,
    clear() { this.cleared = true; },
    async request(path, value) {
      this.calls++;
      assert.equal(path, "/adapter/v1/resources/read");
      assert.equal(validators.adapter(value), true);
      const { offset, count } = value.body;
      const part = bytes.subarray(offset, offset + count);
      const reply = { transport_version: "ps-adapter/v1", kind: "resource_chunk", body: {
        execution_id: claim.execution_id, plan_digest: claim.plan_digest, session_handle: binding.session_handle,
        offset, count, sha256: hash(part), data: part.toString("base64url")
      } };
      assert.equal(validators.adapter(reply), true);
      mutate(reply);
      return reply;
    }
  };
  return { bytes, binding, client };
}

test("resource download verifies bounded chunks and the final whole-resource hash", async () => {
  const { bytes, binding, client } = fixture();
  const written = [];
  let length = 0;
  await downloadResource(client, claim, binding, async (part, offset) => {
    assert.equal(offset, length);
    assert.ok(part.length <= CHUNK_LIMIT);
    written.push(Buffer.from(part)); length += part.length;
  });
  assert.deepEqual(Buffer.concat(written), bytes);
  assert.equal(client.calls, 3);
  assert.equal(client.cleared, false);
});

for (const mutation of ["execution_id", "plan_digest", "session_handle", "offset", "count", "data", "sha256", "noncanonical"]) {
  test(`resource ${mutation} mismatch never writes or retries the rejected chunk`, async () => {
    const { binding, client } = fixture(reply => {
      if (mutation === "offset" || mutation === "count") reply.body[mutation]++;
      else if (mutation === "noncanonical") reply.body.data = reply.body.data.slice(0, -1) + "B";
      else reply.body[mutation] = "altered";
    });
    let writes = 0;
    await assert.rejects(downloadResource(client, claim, binding, async () => { writes++; }), /BRIDGE_RESOURCE_BINDING_INVALID/);
    assert.equal(writes, 0);
    assert.equal(client.calls, 1);
    assert.equal(client.cleared, true);
  });
}

test("a valid series with the wrong total hash remains an unpublished failure", async () => {
  const { binding, client } = fixture();
  binding.sha256 = "0".repeat(64);
  await assert.rejects(downloadResource(client, claim, binding, async () => {}), /BRIDGE_RESOURCE_BINDING_INVALID/);
  assert.equal(client.calls, 3);
  assert.equal(client.cleared, true);
});

test("failed temporary-copy write does not advance to the next chunk", async () => {
  const { binding, client } = fixture();
  await assert.rejects(downloadResource(client, claim, binding, async () => { throw new Error("private file path"); }), /BRIDGE_RESOURCE_BINDING_INVALID/);
  assert.equal(client.calls, 1);
  assert.equal(client.cleared, true);
});

function stagingFixture() {
  const context = fixture();
  Object.assign(context.binding, { resource_id: "resource_source", purpose: "high_resolution_render_source" });
  const resource = { resource_id: context.binding.resource_id, role: context.binding.purpose,
    media_type: "image/jpeg", byte_length: context.binding.byte_length, sha256: context.binding.sha256 };
  const state = { creates: 0, writes: [], deletes: 0, contents: Buffer.alloc(0), existing: Buffer.from("unrelated source") };
  const binary = Symbol("binary");
  const file = {
    async write(data, options) {
      assert.equal(options.format, binary);
      assert.ok(data instanceof ArrayBuffer);
      const bytes = Buffer.from(data);
      assert.ok(bytes.length <= CHUNK_LIMIT);
      state.writes.push(options.append);
      state.contents = options.append ? Buffer.concat([state.contents, bytes]) : bytes;
      return bytes.length;
    },
    async getMetadata() { return { size: state.contents.length }; },
    async delete() { state.deletes++; state.contents = Buffer.alloc(0); }
  };
  const storage = { formats: { binary }, localFileSystem: { async getTemporaryFolder() {
    return { async createFile(name, options) {
      state.creates++;
      assert.match(name, /^resource_[A-Za-z0-9_-]{43}\.jpg$/);
      assert.deepEqual(options, { overwrite: false });
      return file;
    } };
  } } };
  const stage = () => stageResource(context.client, claim, context.binding, resource, storage, () => new Uint8Array(32).fill(3));
  return { ...context, resource, state, file, storage, stage };
}

test("temporary resource lease appends bounded bytes and exposes only a completed copy", async () => {
  const f = stagingFixture();
  const lease = await f.stage();
  assert.deepEqual(f.state.contents, f.bytes);
  assert.deepEqual(f.state.writes, [false, true, true]);
  assert.equal(lease.getEntry(), f.file);
  assert.equal(f.state.deletes, 0);
  assert.deepEqual(JSON.parse(JSON.stringify(lease)), { resourceId: f.resource.resource_id, released: false });
  const first = lease.dispose();
  assert.throws(() => lease.getEntry(), /BRIDGE_RESOURCE_BINDING_INVALID/);
  await Promise.all([first, lease.dispose()]);
  assert.equal(f.state.deletes, 1);
  assert.equal(f.state.existing.toString(), "unrelated source");
});

test("opt-in staging timings separate bridge boundaries from chunk verification and local writes", async () => {
  const f = stagingFixture(), phases = [];
  const performance = { add(name, ms, fields) { phases.push({ name, ms, ...fields }); },
    measure(name, callback, fields) { const value = callback(); this.add(name, 1, fields); return value; },
    async measureAsync(name, callback, fields) { const value = await callback(); this.add(name, 1, fields); return value; } };
  const lease = await stageResource(f.client, claim, f.binding, f.resource, f.storage,
    () => new Uint8Array(32).fill(3), async () => {}, performance);
  assert.equal(lease.getEntry(), f.file);
  assert.equal(phases.filter(p => p.name === "resource_read_rpc").length, 3);
  assert.equal(phases.filter(p => p.name === "resource_boundary").length, 3);
  assert.equal(phases.filter(p => p.name === "resource_temp_write").length, 3);
  assert.equal(phases.filter(p => p.name === "resource_chunk_verify").length, 3);
  assert.ok(phases.every(p => p.resource_role === "high_resolution_render_source"));
});

for (const field of ["resource_id", "role", "media_type", "sha256", "byte_length"]) {
  test(`temporary staging rejects mismatched ${field} before filesystem access`, async () => {
    const f = stagingFixture();
    f.resource[field] = field === "byte_length" ? f.resource.byte_length + 1 : "invalid";
    await assert.rejects(f.stage(), { message: "BRIDGE_RESOURCE_BINDING_INVALID" });
    assert.equal(f.state.creates, 0);
    assert.equal(f.client.calls, 0);
    assert.equal(f.state.deletes, 0);
  });
}

for (const failure of ["short_write", "write_error", "metadata_length", "final_hash"]) {
  test(`temporary staging ${failure} releases only its newly created unpublished file`, async () => {
    const f = stagingFixture();
    if (failure === "short_write") f.file.write = async bytes => bytes.byteLength - 1;
    if (failure === "write_error") f.file.write = async () => { throw new Error("private native path"); };
    if (failure === "metadata_length") f.file.getMetadata = async () => ({ size: f.bytes.length + 1 });
    if (failure === "final_hash") f.resource.sha256 = f.binding.sha256 = "0".repeat(64);
    await assert.rejects(f.stage(), { message: "BRIDGE_RESOURCE_BINDING_INVALID" });
    assert.equal(f.state.creates, 1);
    assert.equal(f.state.deletes, 1);
    assert.equal(f.client.cleared, true);
    assert.equal(f.state.existing.toString(), "unrelated source");
    if (failure.startsWith("write") || failure === "short_write") assert.equal(f.client.calls, 1);
  });
}

test("a temporary name collision never deletes or replaces the existing file", async () => {
  const f = stagingFixture();
  f.storage.localFileSystem.getTemporaryFolder = async () => ({ createFile: async () => { throw new Error("file exists"); } });
  await assert.rejects(f.stage(), { message: "BRIDGE_RESOURCE_BINDING_INVALID" });
  assert.equal(f.state.deletes, 0);
  assert.equal(f.client.calls, 0);
  assert.equal(f.state.existing.toString(), "unrelated source");
});

test("temporary lease cleanup failure is redacted and cannot reopen or retry the copy", async () => {
  const f = stagingFixture();
  f.file.delete = async () => { f.state.deletes++; throw new Error("private native path"); };
  const lease = await f.stage();
  await assert.rejects(lease.dispose(), { message: "BRIDGE_RESOURCE_BINDING_INVALID" });
  assert.throws(() => lease.getEntry(), /BRIDGE_RESOURCE_BINDING_INVALID/);
  await assert.rejects(lease.dispose(), { message: "BRIDGE_RESOURCE_BINDING_INVALID" });
  assert.equal(f.state.deletes, 1);
});
