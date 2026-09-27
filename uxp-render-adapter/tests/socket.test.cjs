"use strict";
const { test } = require("node:test");
const assert = require("node:assert/strict");
const { createSocketTransport, UnsentRequestError } = require("../src/socket");
const { AdapterClient } = require("../src/client");
const { randomBytes } = require("node:crypto");
const address = "http://127.0.0.1:17842/handshake";

test("only a disconnected socket before first send provides typed zero-byte evidence", async () => {
  class RefusedSocket {
    constructor() { queueMicrotask(() => this.onerror?.()); }
    close() {}
  }
  await assert.rejects(createSocketTransport(RefusedSocket)(address, options()), error => error instanceof UnsentRequestError);
  const { Socket } = fakeSocket(() => { throw Error("send may have delivered bytes"); });
  await assert.rejects(createSocketTransport(Socket)(address, options()), error =>
    error.message === "BRIDGE_DISCONNECTED" && !(error instanceof UnsentRequestError));
});

for (const opened of [false, true]) {
  test(`client deadline preserves local unsent evidence only before socket send: opened=${opened}`, async context => {
    context.mock.timers.enable({ apis: ["setTimeout"] });
    let socket;
    class HeldSocket {
      constructor(_url, protocol) { this.protocol = protocol; socket = this; }
      send() {}
      close() { this.closed = true; }
    }
    const client = new AdapterClient(createSocketTransport(HeldSocket), randomBytes, {}, []);
    const pending = assert.rejects(client.raw(address, "{}"), error =>
      error.message === "BRIDGE_DISCONNECTED" && (error instanceof UnsentRequestError) === !opened);
    if (opened) socket.onopen();
    context.mock.timers.tick(5000);
    await pending; assert.equal(socket.closed, true);
  });
}
const options = () => ({ method: "POST", redirect: "error", credentials: "omit",
  headers: { "Content-Type": "application/json" }, body: "{}" });
const metadata = (length, extra = {}) => JSON.stringify({ version: "ps-adapter-rpc/v1", status: 200, byte_length: length, mac: "", ...extra });

function fakeSocket(onSend, closeAutomatically = true) {
  const sockets = [];
  class Socket {
    constructor(url, protocol) {
      this.url = url; this.protocol = protocol; this.sent = []; this.closed = false;
      sockets.push(this); queueMicrotask(() => this.onopen?.());
    }
    send(value) { this.sent.push(typeof value === "string" ? value : new Uint8Array(value).slice()); onSend?.(this); }
    close() {
      this.closed = true;
      if (closeAutomatically) queueMicrotask(() => this.onclose?.({ code: 1000, wasClean: true }));
    }
    emit(value) { this.onmessage?.({ data: typeof value === "string" ? value : Uint8Array.from(value).buffer }); }
  }
  return { Socket, sockets };
}

test("carrier exchanges bounded binary chunks and keeps secrets out of URL and subprotocol", async () => {
  const text = JSON.stringify({ text: "商品尺码".repeat(24000) });
  const bytes = Buffer.from(text);
  const { Socket, sockets } = fakeSocket(socket => {
    if (socket.sent.length !== 2) return;
    socket.emit(metadata(bytes.length));
    for (let offset = 0; offset < bytes.length; offset += 128 * 1024) socket.emit(bytes.subarray(offset, offset + 128 * 1024));
  });
  const request = options(); request.headers["X-Adapter-Handshake-Proof"] = "synthetic-proof";
  const reply = await createSocketTransport(Socket)(address, request);
  assert.equal(await reply.text(), text);
  assert.equal(reply.headers.get("Content-Length"), String(bytes.length));
  assert.equal(sockets[0].url, "ws://localhost:17842/handshake");
  assert.equal(sockets[0].protocol, "ps-adapter-rpc.v1");
  assert.equal(sockets[0].closed, true);
  assert.equal(sockets.length, 1);
  assert.equal(JSON.parse(sockets[0].sent[0]).headers["X-Adapter-Handshake-Proof"], "synthetic-proof");
});

test("carrier rejects unbound URLs and ambient credentials before connecting", async () => {
  const { Socket, sockets } = fakeSocket();
  const transport = createSocketTransport(Socket);
  for (const url of ["http://localhost:17842/handshake", "http://127.0.0.1:17843/handshake",
    "http://127.0.0.1:17842/handshake#fragment", "http://127.0.0.1:17842/handshake?token=x",
    "http://untrusted.invalid:17842/handshake", "http://user@127.0.0.1:17842/handshake",
    "http://127.0.0.1:51000/../heartbeat", "http://127.0.0.1:65536/heartbeat"]) {
    await assert.rejects(transport(url, options()), { message: "BRIDGE_PROTOCOL_MISMATCH" });
  }
  for (const change of [{ credentials: "include" }, { redirect: "follow" }, { method: "PUT" },
    { headers: { Cookie: "synthetic=1" } }, { headers: { Origin: "file://" } },
    { headers: { Authorization: "synthetic\r\ninvalid" } }]) {
    await assert.rejects(transport(address, { ...options(), ...change }), { message: "BRIDGE_PROTOCOL_MISMATCH" });
  }
  assert.equal(sockets.length, 0);
});

test("complete response waits for normal close before allowing the next request", async () => {
  const { Socket, sockets } = fakeSocket(socket => {
    if (socket.sent.length === 2) { socket.emit(metadata(2)); socket.emit([123, 125]); }
  }, false);
  let resolved = false;
  const pending = createSocketTransport(Socket)(address, options()).then(reply => { resolved = true; return reply; });
  await new Promise(resolve => setImmediate(resolve));
  const completedBeforeClose = resolved;
  sockets[0].onclose?.({ code: 1000, wasClean: true });
  assert.equal(await (await pending).text(), "{}");
  assert.equal(completedBeforeClose, false);
});

for (const closing of [{ code: 1006, wasClean: false }, { code: 1000, wasClean: false }, {}]) {
  test(`complete body with unconfirmed close ${JSON.stringify(closing)} is not success`, async () => {
    const { Socket, sockets } = fakeSocket(socket => {
      if (socket.sent.length === 2) { socket.emit(metadata(2)); socket.emit([123, 125]); }
    }, false);
    const pending = assert.rejects(createSocketTransport(Socket)(address, options()), { message: "BRIDGE_DISCONNECTED" });
    await Promise.resolve();
    sockets[0].onclose?.(closing);
    await pending;
    assert.equal(sockets.length, 1);
  });
}

test("deadline includes close handshake after all response bytes arrive", async context => {
  context.mock.timers.enable({ apis: ["setTimeout"] });
  const { Socket, sockets } = fakeSocket(socket => {
    if (socket.sent.length === 2) { socket.emit(metadata(2)); socket.emit([123, 125]); }
  }, false);
  const pending = assert.rejects(createSocketTransport(Socket)(address, options()), { message: "BRIDGE_DISCONNECTED" });
  await Promise.resolve();
  context.mock.timers.tick(5000);
  await pending;
  assert.equal(sockets.length, 1);
});

test("additional frame after complete body is rejected before close", async () => {
  const { Socket, sockets } = fakeSocket(socket => {
    if (socket.sent.length === 2) { socket.emit(metadata(2)); socket.emit([123, 125]); socket.emit([32]); }
  }, false);
  await assert.rejects(createSocketTransport(Socket)(address, options()), /^Error: BRIDGE_/);
  assert.equal(sockets.length, 1);
});

for (const mutation of ["duplicate", "unknown", "version", "mac", "size", "header_size", "binary_header",
  "text_body", "oversized_chunk", "extra_bytes", "invalid_utf8", "closed", "wrong_protocol"]) {
  test(`carrier ${mutation} failure closes once and never retries`, async () => {
    const { Socket, sockets } = fakeSocket(socket => {
      if (socket.sent.length !== 2) return;
      switch (mutation) {
        case "duplicate": socket.emit(metadata(2).replace('"status":200', '"status":200,"status":200')); break;
        case "unknown": socket.emit(metadata(2, { unknown: true })); break;
        case "version": socket.emit(metadata(2, { version: "other" })); break;
        case "mac": socket.emit(metadata(2, { mac: "invalid" })); break;
        case "size": socket.emit(metadata(1048577)); break;
        case "header_size": socket.emit(" ".repeat(8193)); break;
        case "binary_header": socket.emit([123, 125]); break;
        case "text_body": socket.emit(metadata(2)); socket.emit("{}"); break;
        case "oversized_chunk": socket.emit(metadata(131073)); socket.emit(new Uint8Array(131073)); break;
        case "extra_bytes": socket.emit(metadata(2)); socket.emit([123, 125, 32]); break;
        case "invalid_utf8": socket.emit(metadata(2)); socket.emit([0xc0, 0xaf]); break;
        case "closed": socket.onclose?.(); break;
      }
    });
    // Change the protocol before the asynchronously delivered open event.
    const UsedSocket = mutation === "wrong_protocol" ? class extends Socket {
      constructor(...args) { super(...args); this.protocol = "other"; }
    } : Socket;
    await assert.rejects(createSocketTransport(UsedSocket)(address, options()), /^Error: BRIDGE_/);
    assert.equal(sockets.length, 1);
    assert.equal(sockets[0].closed, true);
  });
}

test("socket deadline covers absent or incomplete response and aborts without reconnecting", async context => {
  context.mock.timers.enable({ apis: ["setTimeout"] });
  for (const partial of [false, true]) {
    const { Socket, sockets } = fakeSocket(socket => {
      if (partial && socket.sent.length === 2) { socket.emit(metadata(3)); socket.emit([123]); }
    });
    const rejected = assert.rejects(createSocketTransport(Socket)(address, options()), { message: "BRIDGE_DISCONNECTED" });
    await Promise.resolve();
    context.mock.timers.tick(5000);
    await rejected;
    assert.equal(sockets.length, 1); assert.equal(sockets[0].closed, true);
  }
});

test("explicit abort closes the current connection and ignores late data", async () => {
  const controller = new AbortController();
  const { Socket, sockets } = fakeSocket();
  const pending = assert.rejects(createSocketTransport(Socket)(address, { ...options(), signal: controller.signal }),
    { message: "BRIDGE_DISCONNECTED" });
  await Promise.resolve();
  controller.abort();
  await pending;
  sockets[0].emit(metadata(2)); sockets[0].emit([123, 125]);
  assert.equal(sockets[0].closed, true); assert.equal(sockets.length, 1);
});

test("AdapterClient explicitly omits cookies on every carrier call", async () => {
  let called = false;
  const client = new AdapterClient(async (_, request) => {
    called = true;
    assert.equal(request.credentials, "omit"); assert.equal(request.redirect, "error");
    return { status: 200, headers: { get: name => name === "Content-Length" ? "2" : "" }, text: async () => "{}" };
  }, randomBytes, {}, []);
  await client.raw(address, "{}"); assert.equal(called, true);
});
