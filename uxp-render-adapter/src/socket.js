"use strict";
const { concatBytes } = require("@noble/hashes/utils");
const { utf8ToBytes, bytesToUtf8 } = require("./utf8");
const { parseStrict } = require("./json");
const VERSION = "ps-adapter-rpc/v1";
const PROTOCOL = "ps-adapter-rpc.v1";
const CHUNK = 128 * 1024;
const HEADER = 8192;
// Local carrier evidence, never inferred from a remote error code or message.
class UnsentRequestError extends Error {
  constructor() { super("BRIDGE_DISCONNECTED"); }
}
const headerNames = new Set(["Content-Type", "X-Adapter-Handshake-Proof", "Authorization", "X-Adapter-Version",
  "X-Bridge-Request-Id", "X-Bridge-Session-Id", "X-Adapter-Instance-Id", "X-Adapter-Sequence", "X-Adapter-Mac",
  "X-Bridge-Protocol-Version", "X-Bridge-Correlation-Id", "X-Bridge-Sent-At", "X-Bridge-Idempotency-Key"]);

function responseByteLimit(url, method) {
  return url.endsWith("/adapter/v1/claims") ? 8 * 1024 * 1024 + 65536
    : method === "GET" || url.endsWith("/heartbeat") || url.endsWith("/adapter/v1/resources/read")
      || url.endsWith("/adapter/v1/events") || /\/adapter\/v1\/outputs\/(write|seal)$/.test(url) ? 256 * 1024 : 1024 * 1024;
}

function socketAddress(url, method) {
  // The client retains its existing logical endpoints; only this carrier maps them to UXP's supported host.
  const match = /^http:\/\/127\.0\.0\.1:([1-9][0-9]{0,4})(\/[^#]*)$/.exec(url);
  if (!match || !["GET", "POST"].includes(method)) throw new Error("BRIDGE_PROTOCOL_MISMATCH");
  const port = Number(match[1]), path = match[2];
  const discovery = port === 17842 && method === "POST" && ["/adapter/v1/challenges", "/handshake"].includes(path);
  const execution = port >= 49152 && port <= 65535 && (method === "POST"
    ? /^(?:\/heartbeat|\/release|\/adapter\/v1\/(?:claims|events|resources\/read|outputs\/(?:write|seal))|\/adapter\/v1\/claims\/[A-Za-z0-9][A-Za-z0-9._:-]{0,127}\/ack)$/.test(path)
    : /^\/executions\/[A-Za-z0-9][A-Za-z0-9._:-]{0,127}\?after_seq=[0-9]{1,10}$/.test(path));
  if (!discovery && !execution) throw new Error("BRIDGE_PROTOCOL_MISMATCH");
  return `ws://localhost:${port}${path}`;
}

function createSocketTransport(Socket) {
  return (url, options) => new Promise((resolve, reject) => {
    let socket, timer, settled = false, metadata = null, received = 0, response = null;
    let requestBytes, requestStarted = false;
    // Shared only with the local client's outer deadline. No wire data can set it.
    if (options.deliveryEvidence) options.deliveryEvidence.requestStarted = false;
    const chunks = [];
    const fail = code => finish(code === "BRIDGE_DISCONNECTED" && !requestStarted
      ? new UnsentRequestError() : new Error(code));
    const abort = () => fail("BRIDGE_DISCONNECTED");
    function finish(error, response) {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      options?.signal?.removeEventListener("abort", abort);
      requestBytes?.fill(0);
      for (const chunk of chunks) chunk.fill(0);
      if (socket) {
        socket.onopen = socket.onmessage = socket.onerror = socket.onclose = null;
        try { socket.close(); } catch {}
      }
      if (error) reject(error); else resolve(response);
    }
    function complete() {
      const bytes = concatBytes(...chunks);
      try {
        const text = bytesToUtf8(bytes);
        response = { status: metadata.status, headers: { get: name => name === "Content-Length"
          ? String(metadata.byte_length) : name === "X-Adapter-Mac" ? metadata.mac : null }, text: async () => text };
        // The server treats a failed close as uncertain delivery. Keep the request
        // pending (and its deadline active) until that handshake has completed;
        // otherwise an earlier close fault can invalidate the next page/chunk.
        socket.close(1000, "");
      } finally { bytes.fill(0); }
    }
    try {
      if (options?.redirect !== "error" || options.credentials !== "omit") throw new Error("BRIDGE_PROTOCOL_MISMATCH");
      const address = socketAddress(url, options.method);
      if (!options.headers || Object.entries(options.headers).some(([name, value]) => !headerNames.has(name)
        || typeof value !== "string" || !value || /[\x00-\x1f\x7f]/.test(value))) throw new Error("BRIDGE_PROTOCOL_MISMATCH");
      requestBytes = utf8ToBytes(options.body || "");
      if ((options.method === "GET" && requestBytes.length !== 0)
        || requestBytes.length > (address.startsWith("ws://localhost:17842/") ? 1024 * 1024 : 256 * 1024))
        throw new Error("BRIDGE_REQUEST_TOO_LARGE");
      const header = JSON.stringify({ version: VERSION, method: options.method, headers: options.headers, byte_length: requestBytes.length });
      if (utf8ToBytes(header).length > HEADER) throw new Error("BRIDGE_REQUEST_TOO_LARGE");
      const limit = responseByteLimit(url, options.method);
      if (options.signal?.aborted) throw new Error("BRIDGE_DISCONNECTED");
      options.signal?.addEventListener("abort", abort, { once: true });
      timer = setTimeout(abort, 5000);
      socket = new Socket(address, PROTOCOL);
      socket.binaryType = "arraybuffer";
      socket.onopen = () => {
        if (settled) return;
        try {
          if (socket.protocol !== PROTOCOL) return fail("BRIDGE_PROTOCOL_MISMATCH");
          // Set before send: a throwing send does not prove that no bytes escaped.
          requestStarted = true;
          if (options.deliveryEvidence) options.deliveryEvidence.requestStarted = true;
          socket.send(header);
          for (let offset = 0; offset < requestBytes.length; offset += CHUNK)
            socket.send(requestBytes.slice(offset, offset + CHUNK).buffer);
        } catch { fail("BRIDGE_DISCONNECTED"); }
      };
      socket.onmessage = event => {
        if (settled) return;
        try {
          if (response) return fail("BRIDGE_PROTOCOL_MISMATCH");
          if (!metadata) {
            if (typeof event.data !== "string" || event.data.length > HEADER || utf8ToBytes(event.data).length > HEADER)
              return fail("BRIDGE_REQUEST_TOO_LARGE");
            const value = parseStrict(event.data);
            if (Object.keys(value).sort().join(",") !== "byte_length,mac,status,version" || value.version !== VERSION
              || !Number.isInteger(value.status) || value.status < 100 || value.status > 599
              || !Number.isInteger(value.byte_length) || value.byte_length < 0
              || typeof value.mac !== "string" || !/^(?:[A-Za-z0-9_-]{43})?$/.test(value.mac))
              return fail("BRIDGE_PROTOCOL_MISMATCH");
            if (value.byte_length > limit) return fail("BRIDGE_REQUEST_TOO_LARGE");
            metadata = value;
          } else {
            if (!(event.data instanceof ArrayBuffer)) return fail("BRIDGE_PROTOCOL_MISMATCH");
            const bytes = new Uint8Array(event.data);
            if (bytes.length === 0 || bytes.length > CHUNK || received + bytes.length > metadata.byte_length)
              return fail("BRIDGE_REQUEST_TOO_LARGE");
            chunks.push(bytes); received += bytes.length;
          }
          if (metadata && received === metadata.byte_length) complete();
        } catch { fail("BRIDGE_PROTOCOL_MISMATCH"); }
      };
      socket.onerror = () => fail("BRIDGE_DISCONNECTED");
      socket.onclose = event => {
        if (response && event?.code === 1000 && event.wasClean === true) finish(null, response);
        else fail("BRIDGE_DISCONNECTED");
      };
    } catch (error) {
      finish(new Error(/^BRIDGE_[A-Z_]+$/.test(error.message) ? error.message : "BRIDGE_DISCONNECTED"));
    }
  });
}

module.exports = { createSocketTransport, responseByteLimit, UnsentRequestError };
