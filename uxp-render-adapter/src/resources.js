"use strict";
const { sha256 } = require("@noble/hashes/sha256");
const { bytesToHex } = require("@noble/hashes/utils");
const { encode } = require("./auth");
const { AuthenticatedCancellation } = require("./client");
const CHUNK_LIMIT = 128 * 1024;
const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

function decodeChunk(value, count) {
  if (typeof value !== "string" || value.length !== Math.ceil(count * 4 / 3) || !/^[A-Za-z0-9_-]+$/.test(value))
    throw new Error("BRIDGE_RESOURCE_BINDING_INVALID");
  const bytes = new Uint8Array(count);
  let bits = 0, acc = 0, index = 0;
  for (const char of value) {
    acc = (acc << 6) | alphabet.indexOf(char); bits += 6;
    if (bits >= 8) { bits -= 8; if (index < count) bytes[index++] = (acc >>> bits) & 255; }
  }
  if (encode(bytes) !== value) throw new Error("BRIDGE_RESOURCE_BINDING_INVALID");
  return bytes;
}

// writeChunk owns an unpublished temporary copy. A caller may use it only after this method succeeds.
async function downloadResource(client, claim, binding, writeChunk, performance = null, fields = {}) {
  const hash = sha256.create();
  try {
    if (!Number.isSafeInteger(binding.byte_length) || binding.byte_length < 1 || !binding.read_only
      || !/^[0-9a-fA-F]{64}$/.test(binding.sha256) || typeof writeChunk !== "function")
      throw new Error("BRIDGE_RESOURCE_BINDING_INVALID");
    for (let offset = 0; offset < binding.byte_length;) {
      const count = Math.min(CHUNK_LIMIT, binding.byte_length - offset);
      const request = () => client.request("/adapter/v1/resources/read", {
        transport_version: "ps-adapter/v1", kind: "resource_read", body: {
          claim_id: claim.claim_id, execution_id: claim.execution_id, plan_digest: claim.plan_digest,
          session_handle: binding.session_handle, offset, count
        }
      });
      const measured = { ...fields, bytes: count };
      const reply = performance
        ? await performance.measureAsync("resource_read_rpc", request, measured)
        : await request();
      const chunk = reply.body;
      if (reply.kind !== "resource_chunk" || chunk.execution_id !== claim.execution_id
        || chunk.plan_digest.toLowerCase() !== claim.plan_digest.toLowerCase()
        || chunk.session_handle !== binding.session_handle || chunk.offset !== offset || chunk.count !== count)
        throw new Error("BRIDGE_RESOURCE_BINDING_INVALID");
      const verify = () => {
        const bytes = decodeChunk(chunk.data, count);
        if (bytesToHex(sha256(bytes)) !== chunk.sha256) throw new Error("BRIDGE_RESOURCE_BINDING_INVALID");
        hash.update(bytes);
        return bytes;
      };
      const bytes = performance ? performance.measure("resource_chunk_verify", verify, measured) : verify();
      await writeChunk(bytes, offset);
      offset += count;
    }
    if (bytesToHex(hash.digest()) !== binding.sha256.toLowerCase()) throw new Error("BRIDGE_RESOURCE_BINDING_INVALID");
  } catch (error) {
    if (error instanceof AuthenticatedCancellation && client.session) throw error;
    client.clear();
    throw new Error("BRIDGE_RESOURCE_BINDING_INVALID");
  } finally { hash.destroy(); }
}

// Only the new file returned by createFile belongs to this lease. Never enumerate or clear the temp root.
async function stageResource(client, claim, binding, resource, storage, randomBytes, boundary = async () => {}, performance = null) {
  let file = null;
  try {
    const extensions = { "image/vnd.adobe.photoshop": "psd", "image/png": "png", "image/jpeg": "jpg" };
    const extension = extensions[resource?.media_type];
    if (!extension || binding.resource_id !== resource.resource_id || binding.purpose !== resource.role
      || binding.sha256?.toLowerCase() !== resource.sha256?.toLowerCase() || binding.byte_length !== resource.byte_length
      || !binding.read_only || !Number.isSafeInteger(binding.byte_length) || binding.byte_length < 1
      || !/^[0-9a-fA-F]{64}$/.test(binding.sha256)
      || !(binding.purpose === "template_copy" && extension === "psd"
        || binding.purpose === "deterministic_size_table" && extension === "png"
        || binding.purpose === "high_resolution_render_source" && ["png", "jpg"].includes(extension)))
      throw new Error("BRIDGE_RESOURCE_BINDING_INVALID");
    const nonce = randomBytes(32);
    if (!(nonce instanceof Uint8Array) || nonce.length !== 32) throw new Error("BRIDGE_RESOURCE_BINDING_INVALID");
    const name = "resource_" + encode(nonce) + "." + extension;
    nonce.fill(0);
    const folder = await storage.localFileSystem.getTemporaryFolder();
    file = await folder.createFile(name, { overwrite: false });
    const fields = { resource_role: resource.role };
    await downloadResource(client, claim, binding, async (bytes, offset) => {
      if (performance) await performance.measureAsync("resource_boundary", boundary, { ...fields, bytes: bytes.byteLength });
      else await boundary();
      const data = bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength);
      const write = () => file.write(data, { format: storage.formats.binary, append: offset !== 0 });
      const written = performance
        ? await performance.measureAsync("resource_temp_write", write, { ...fields, bytes: bytes.byteLength })
        : await write();
      if (written !== bytes.byteLength) throw new Error("BRIDGE_RESOURCE_BINDING_INVALID");
    }, performance, fields);
    const metadata = await file.getMetadata();
    if (metadata.size !== binding.byte_length) throw new Error("BRIDGE_RESOURCE_BINDING_INVALID");
    const copy = file;
    let released = false;
    let releasing = null;
    return Object.freeze({
      resourceId: resource.resource_id,
      getEntry() {
        if (released) throw new Error("BRIDGE_RESOURCE_BINDING_INVALID");
        return copy;
      },
      dispose() {
        if (!releasing) {
          released = true;
          releasing = Promise.resolve().then(() => copy.delete()).catch(() => {
            throw new Error("BRIDGE_RESOURCE_BINDING_INVALID");
          });
        }
        return releasing;
      },
      toJSON() { return { resourceId: resource.resource_id, released }; }
    });
  } catch (error) {
    const cancelled = error instanceof AuthenticatedCancellation && client.session;
    if (!cancelled) client.clear();
    if (file) {
      try { await file.delete(); } catch {
        client.clear();
        throw new Error("BRIDGE_RESOURCE_BINDING_INVALID");
      }
    }
    if (cancelled) throw error;
    throw new Error("BRIDGE_RESOURCE_BINDING_INVALID");
  }
}

module.exports = { downloadResource, stageResource, CHUNK_LIMIT };
