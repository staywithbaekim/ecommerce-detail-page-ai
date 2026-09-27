"use strict";
const { sha256 } = require("@noble/hashes/sha256");
const { bytesToHex } = require("@noble/hashes/utils");
const { encode } = require("./auth");
const { AuthenticatedCancellation } = require("./client");
const LIMIT = 128 * 1024;
const invalid = () => new Error("BRIDGE_RESOURCE_BINDING_INVALID");

// Receives only an executor-owned new output lease. No path or filename crosses the wire.
// fs is UXP's documented chunked filesystem API, not Node's callback API.
async function uploadOutput(client, prepared, kind, lease, storage, fs, boundary = async () => {}, performance = null) {
  let fd = null;
  const digest = sha256.create();
  const diagnostic = { kind: ["jpg", "editable_psd"].includes(kind) ? kind : "invalid",
    stage: "open", offset: 0, code: null };
  prepared.transferDiagnostic = diagnostic;
  client.outputDiagnostic = diagnostic;
  try {
    diagnostic.stage = "authorize";
    const allowed = ["jpg", "editable_psd"].includes(kind) && prepared.plan.output[kind].enabled;
    if (!allowed) throw invalid();
    diagnostic.stage = "metadata";
    const entry = lease.getEntry();
    const length = (await entry.getMetadata()).size;
    diagnostic.stage = "length";
    if (!Number.isSafeInteger(length) || length < 1 || length > 2147483648) throw invalid();
    const binding = { claim_id: prepared.claim.claim_id, execution_id: prepared.claim.execution_id,
      plan_digest: prepared.claim.plan_digest, output_root_handle: prepared.output.output_root_handle,
      artifact_set_id: prepared.output.artifact_set_id, output_kind: kind, byte_length: length };
    diagnostic.stage = "temp_open";
    fd = await fs.open(storage.localFileSystem.getFsUrl(entry), "r");
    for (let offset = 0; offset < length;) {
      diagnostic.offset = offset; diagnostic.stage = "boundary";
      const count = Math.min(LIMIT, length - offset), fields = { output_kind: diagnostic.kind, bytes: count };
      if (performance) await performance.measureAsync("output_boundary", boundary, fields);
      else await boundary();
      diagnostic.stage = "read";
      const buffer = new ArrayBuffer(count);
      const readChunk = async () => {
        for (let read = 0; read < count;) {
          const result = await fs.read(fd, buffer, read, count - read, offset + read);
          if (!Number.isSafeInteger(result.bytesRead) || result.bytesRead < 1 || result.bytesRead > count - read) throw invalid();
          read += result.bytesRead;
        }
      };
      if (performance) await performance.measureAsync("output_temp_read", readChunk, fields);
      else await readChunk();
      const bytes = new Uint8Array(buffer);
      let encoded;
      const encodeChunk = () => {
        digest.update(bytes);
        encoded = { sha256: bytesToHex(sha256(bytes)), data: encode(bytes) };
      };
      if (performance) performance.measure("output_chunk_encode", encodeChunk, fields);
      else encodeChunk();
      diagnostic.stage = "write";
      const write = () => client.request("/adapter/v1/outputs/write", { transport_version: "ps-adapter/v1", kind: "output_write",
        body: { ...binding, offset, count, ...encoded } });
      const reply = performance ? await performance.measureAsync("output_write_rpc", write, fields) : await write();
      diagnostic.stage = "receipt";
      checkReceipt(reply, binding, offset + count, false);
      offset += count;
    }
    diagnostic.offset = length; diagnostic.stage = "length";
    if ((await entry.getMetadata()).size !== length) throw invalid();
    await fs.close(fd); fd = null;
    diagnostic.stage = "boundary";
    const sealFields = { output_kind: diagnostic.kind };
    if (performance) await performance.measureAsync("output_boundary", boundary, sealFields);
    else await boundary();
    const hash = bytesToHex(digest.digest());
    diagnostic.stage = "seal";
    const seal = () => client.request("/adapter/v1/outputs/seal", { transport_version: "ps-adapter/v1", kind: "output_seal",
      body: { ...binding, sha256: hash } });
    const reply = performance ? await performance.measureAsync("output_seal_rpc", seal, sealFields) : await seal();
    checkReceipt(reply, binding, length, true);
    diagnostic.stage = "sealed";
    return { output_kind: kind, byte_length: length, sha256: hash };
  } catch (error) {
    diagnostic.code = /^BRIDGE_[A-Z_]+$/.test(error?.message) ? error.message : "BRIDGE_RESOURCE_BINDING_INVALID";
    if (client.session && (error instanceof AuthenticatedCancellation
      || diagnostic.stage === "boundary" && error?.message === "BRIDGE_EXECUTION_CANCELLED")) throw error;
    client.clear();
    throw invalid();
  } finally {
    digest.destroy();
    if (fd !== null) { try { await fs.close(fd); } catch { client.clear(); } }
  }
}

function checkReceipt(reply, binding, offset, sealed) {
  const b = reply.body;
  if (reply.kind !== "output_receipt" || b.execution_id !== binding.execution_id || b.artifact_set_id !== binding.artifact_set_id
    || b.output_kind !== binding.output_kind || b.next_offset !== offset || b.sealed !== sealed) throw invalid();
}

module.exports = { uploadOutput };
