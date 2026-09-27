"use strict";
const { sha256 } = require("@noble/hashes/sha256");
const { hmac } = require("@noble/hashes/hmac");
const { bytesToHex, concatBytes } = require("@noble/hashes/utils");
const { utf8ToBytes } = require("./utf8");
const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

function encode(bytes) {
  let result = "";
  for (let i = 0; i < bytes.length; i += 3) {
    const n = (bytes[i] << 16) | ((bytes[i + 1] || 0) << 8) | (bytes[i + 2] || 0);
    result += alphabet[(n >>> 18) & 63] + alphabet[(n >>> 12) & 63];
    if (i + 1 < bytes.length) result += alphabet[(n >>> 6) & 63];
    if (i + 2 < bytes.length) result += alphabet[n & 63];
  }
  return result;
}

function decode32(value) {
  if (typeof value !== "string" || !/^[A-Za-z0-9_-]{43}$/.test(value)) throw new Error("BRIDGE_PAIRING_REJECTED");
  const bytes = new Uint8Array(32);
  let bits = 0, acc = 0, index = 0;
  for (const char of value) {
    acc = (acc << 6) | alphabet.indexOf(char); bits += 6;
    if (bits >= 8) { bits -= 8; if (index < 32) bytes[index++] = (acc >>> bits) & 255; }
  }
  if (encode(bytes) !== value) throw new Error("BRIDGE_PAIRING_REJECTED");
  return bytes;
}

function sign(key, ...fields) {
  const parts = [];
  for (const field of fields) {
    const bytes = utf8ToBytes(String(field));
    const prefix = new Uint8Array(4);
    new DataView(prefix.buffer).setUint32(0, bytes.length, false);
    parts.push(prefix, bytes);
  }
  const transcript = concatBytes(...parts);
  try { return encode(hmac(sha256, key, transcript)); }
  finally { transcript.fill(0); }
}

function fixedEquals(expected, actual) {
  try {
    const a = decode32(expected), b = decode32(actual);
    let diff = 0;
    for (let i = 0; i < 32; i++) diff |= a[i] ^ b[i];
    a.fill(0); b.fill(0);
    return diff === 0;
  } catch { return false; }
}

const hash = value => bytesToHex(sha256(utf8ToBytes(value)));
const challengeProof = (secret, c) => sign(secret, "ps-adapter/v1", "server-challenge", c.request_id,
  c.client_nonce, c.nonce, c.session_id, c.plugin_instance_id, c.task_binding, c.app_version, c.plugin_id,
  c.plugin_version, c.photoshop_build, c.uxp_runtime_version, c.issued_at_unix_seconds,
  c.expires_at_unix_seconds, [...c.capabilities].sort().join("\n"));
const sessionKey = (secret, c) => decode32(sign(secret, "ps-adapter/v1", "session-key", c.server_proof,
  c.client_nonce, c.nonce, c.session_id, c.plugin_instance_id, c.task_binding));
const pairingProof = (secret, r) => encode(hmac(sha256, secret,
  concatBytes(decode32(r.nonce), utf8ToBytes(r.session_id + r.host.plugin_manifest_version + r.host.photoshop_build))));
const handshakeContextProof = (secret, c, json) => sign(secret, "ps-adapter/v1", "client-handshake", c.server_proof, hash(json));
const handshakeResponseProof = (key, requestId, json) => sign(key, "ps-adapter/v1", "handshake-response", requestId, hash(json));
const requestMac = (key, a, method, path, body) => {
  const fields = ["ps-adapter/v1", "request", method, path,
    a.request_id, a.session_id, a.plugin_instance_id, a.sequence, a.token, hash(body)];
  if (method === "GET") {
    if (!a.query) throw new Error("BRIDGE_PROTOCOL_MISMATCH");
    fields.push("get-metadata", a.query.protocol_version, a.query.correlation_id, a.query.sent_at, a.query.idempotency_key);
  }
  return sign(key, ...fields);
};
const responseMac = (key, a, status, body) => sign(key, "ps-adapter/v1", "response", a.request_id,
  a.session_id, a.plugin_instance_id, a.sequence, status, hash(body));

module.exports = { encode, decode32, sign, hash, fixedEquals, challengeProof, sessionKey, pairingProof,
  handshakeContextProof, handshakeResponseProof, requestMac, responseMac };
