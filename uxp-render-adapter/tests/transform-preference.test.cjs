"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const { withSubpixelTransform, recoverTransformPreference } = require("../src/transform-preference");

function fixture() {
  const files = new Map(), main = "render-transform-preference-v1.json";
  const state = { snap: true, writes: [], failBackup: false, failRestore: false, failMove: false, tornState: null,
    promotions: 0, failPromotedGet: false, failPromotedRead: false, corruptPromotion: false, failSnapRead: false };
  Object.defineProperty(state, "record", { get: () => files.get(main) ?? null,
    set: value => { if (value === null) files.delete(main); else files.set(main, value); } });
  function entry(name) { return { name,
    async getMetadata() { return { size: files.get(this.name)?.length || 0 }; },
    async read() {
      if (this.name === main && state.promotions && state.failPromotedRead) throw Error("read");
      return files.get(this.name);
    },
    async write(value) {
      assert.notEqual(this.name, main, "never truncate the authoritative recovery record");
      if (state.failBackup) throw Error("disk");
      files.set(this.name, JSON.parse(value).state === state.tornState ? "\0".repeat(78) : value);
    },
    async moveTo(destination, options) {
      assert.equal(destination, folder);
      if (state.failMove) throw Error("move");
      if (!options.overwrite && files.has(options.newName)) throw Error("exists");
      files.set(options.newName, files.get(this.name)); files.delete(this.name); this.name = options.newName;
      if (this.name === main) {
        state.promotions++;
        if (state.corruptPromotion) files.set(main, "\0".repeat(78));
      }
    }
  }; }
  const folder = { async getEntries() { return [...files.keys()].map(entry); },
    async getEntry(name) {
      if (name === main && state.promotions && state.failPromotedGet) throw Error("getEntry");
      assert.ok(files.has(name)); return entry(name);
    },
    async createFile(name, options) {
      assert.equal(options.overwrite, false);
      if (files.has(name)) throw Error("exists");
      files.set(name, ""); return entry(name);
    } };
  const storage = { localFileSystem: { async getDataFolder() { return folder; } } };
  const ps = { core: { async executeAsModal(fn) { return fn(); } }, action: { async batchPlay(commands) {
    return commands.map(command => {
      if (command._obj === "get") {
        if (state.failSnapRead) throw Error("host_read");
        return { toolsPreferences: { transformsSnapToPixels: Object.hasOwn(state, "snapReply") ? state.snapReply : state.snap } };
      }
      const next = command.to.transformsSnapToPixels;
      assert.ok(state.record, "must create verified backup before changing setting");
      if (next && state.failRestore) throw Error("host");
      state.writes.push(next); state.snap = next; return {};
    });
  } } };
  return { state, ps, storage, files, main, next: main + ".next" };
}

test("subpixel transform backs up and restores on success and rejected execution", async () => {
  for (const fail of [false, true]) {
    const f = fixture(), error = Error("operation");
    const pending = withSubpixelTransform(f.ps, f.storage, async () => {
      assert.equal(f.state.snap, false); assert.equal(JSON.parse(f.state.record).state, "pending");
      if (fail) throw error; return 7;
    });
    if (fail) await assert.rejects(pending, e => e === error); else assert.equal(await pending, 7);
    assert.equal(f.state.snap, true); assert.deepEqual(f.state.writes, [false, true]);
    assert.equal(JSON.parse(f.state.record).state, "restored");
  }
});
test("disabled preference is unchanged, backup failure prevents modification", async () => {
  const f = fixture(); f.state.snap = false;
  await withSubpixelTransform(f.ps, f.storage, async () => {});
  assert.deepEqual(f.state.writes, []); assert.equal(f.state.record, null);
  f.state.snap = true; f.state.failBackup = true;
  await assert.rejects(withSubpixelTransform(f.ps, f.storage, async () => assert.fail()), e => e.preferenceReason === "record_write_failed");
  assert.equal(f.state.snap, true); assert.deepEqual(f.state.writes, []);
});

test("unknown preference reads cannot create a backup, change settings or execute a page", async () => {
  for (const failure of [undefined, null, 0, 1, "false", {}, "throw"]) {
    for (const snap of [true, false]) {
      for (const record of [null, JSON.stringify({ schema: "render-transform-preference/v1", previous: true, state: "restored" })]) {
        const f = fixture(); f.state.snap = snap; f.state.record = record;
        f.state.snapReply = failure; f.state.failSnapRead = failure === "throw";
        const before = [...f.files]; let executions = 0;
        await assert.rejects(withSubpixelTransform(f.ps, f.storage, async () => { executions++; }));
        assert.equal(executions, 0); assert.equal(f.state.snap, snap);
        assert.deepEqual(f.state.writes, []); assert.deepEqual([...f.files], before);
      }
    }
  }
});

test("unknown preference reads leave pending authority and interrupted candidates unchanged", async () => {
  for (const failure of [undefined, null, 0, "true", "throw"]) {
    for (const state of ["pending", "restored"]) {
      const f = fixture(); f.state.snap = false;
      f.state.record = JSON.stringify({ schema: "render-transform-preference/v1", previous: true, state });
      f.files.set(f.next, "interrupted candidate");
      f.state.snapReply = failure; f.state.failSnapRead = failure === "throw";
      const before = [...f.files];
      await assert.rejects(recoverTransformPreference(f.ps, f.storage));
      assert.equal(f.state.snap, false); assert.deepEqual(f.state.writes, []);
      assert.deepEqual([...f.files], before); assert.equal(f.state.promotions, 0);
    }
  }
});

test("successful promotion followed by an unavailable authoritative reread never starts execution", async () => {
  for (const failure of ["failPromotedGet", "failPromotedRead"]) {
    const f = fixture(); f.state[failure] = true;
    f.state.record = JSON.stringify({ schema: "render-transform-preference/v1", previous: true, state: "restored" });
    let executions = 0;
    await assert.rejects(withSubpixelTransform(f.ps, f.storage, async () => { executions++; }),
      error => error.preferenceReason === "record_write_failed");
    assert.equal(f.state.promotions, 1); assert.equal(f.files.has(f.next), false);
    assert.equal(JSON.parse(f.state.record).state, "pending");
    assert.equal(executions, 0); assert.equal(f.state.snap, true); assert.deepEqual(f.state.writes, []);

    // Once readable, the intact pending authority can be closed without changing the already-original preference.
    f.state[failure] = false;
    await recoverTransformPreference(f.ps, f.storage);
    assert.equal(JSON.parse(f.state.record).state, "restored");
    assert.equal(executions, 0); assert.equal(f.state.snap, true); assert.deepEqual(f.state.writes, []);
  }
});

test("corrupt promoted authority remains blocking even when a valid candidate is available", async () => {
  const f = fixture(); f.state.corruptPromotion = true;
  let executions = 0;
  await assert.rejects(withSubpixelTransform(f.ps, f.storage, async () => { executions++; }),
    error => error.preferenceReason === "record_write_failed");
  assert.equal(f.state.promotions, 1); assert.equal(f.files.has(f.next), false);
  assert.equal(f.state.record, "\0".repeat(78)); assert.equal(f.state.snap, true);
  assert.deepEqual(f.state.writes, []); assert.equal(executions, 0);

  f.state.corruptPromotion = false;
  f.files.set(f.next, JSON.stringify({ schema: "render-transform-preference/v1", previous: true, state: "pending" }));
  const before = [...f.files];
  await assert.rejects(recoverTransformPreference(f.ps, f.storage), error => error.preferenceReason === "record_invalid");
  await assert.rejects(withSubpixelTransform(f.ps, f.storage, async () => { executions++; }),
    error => error.preferenceReason === "record_invalid");
  assert.deepEqual([...f.files], before); assert.equal(f.state.promotions, 1);
  assert.deepEqual(f.state.writes, []); assert.equal(f.state.snap, true); assert.equal(executions, 0);
});

test("failed restoration retains pending backup and next initialization recovers it", async () => {
  const f = fixture(); f.state.failRestore = true;
  await assert.rejects(withSubpixelTransform(f.ps, f.storage, async () => 1), e => e.preferenceReason === "restore_failed");
  assert.equal(f.state.snap, false); assert.equal(JSON.parse(f.state.record).state, "pending");
  await assert.rejects(withSubpixelTransform(f.ps, f.storage, async () => assert.fail()), /BRIDGE_HOST_BLOCKED/);
  f.state.failRestore = false;
  await recoverTransformPreference(f.ps, f.storage);
  assert.equal(f.state.snap, true); assert.equal(JSON.parse(f.state.record).state, "restored");
});

test("torn pending write and failed rename retain the prior record and never change the preference", async () => {
  for (const failure of ["torn", "move"]) {
    const f = fixture();
    f.state.record = JSON.stringify({ schema: "render-transform-preference/v1", previous: true, state: "restored" });
    const original = f.state.record;
    f.state.tornState = failure === "torn" ? "pending" : null; f.state.failMove = failure === "move";
    await assert.rejects(withSubpixelTransform(f.ps, f.storage, async () => assert.fail()), e => e.preferenceReason === "record_write_failed");
    assert.equal(f.state.record, original); assert.deepEqual(f.state.writes, []);
    const interrupted = f.files.get(f.next); assert.ok(interrupted);
    f.state.failMove = false;
    await recoverTransformPreference(f.ps, f.storage);
    assert.equal(f.state.record, original); assert.equal(f.files.has(f.next), false);
    assert.equal([...f.files].find(([name]) => name.startsWith(f.next + ".interrupted-"))[1], interrupted);
  }
});

test("torn completion cannot destroy pending authority and recovery preserves the interrupted bytes", async () => {
  const f = fixture(); f.state.tornState = "restored";
  await assert.rejects(withSubpixelTransform(f.ps, f.storage, async () => 7), e => e.preferenceReason === "record_write_failed");
  assert.equal(f.state.snap, true); assert.equal(JSON.parse(f.state.record).state, "pending");
  assert.equal(f.files.get(f.next), "\0".repeat(78));
  f.state.tornState = null;
  await recoverTransformPreference(f.ps, f.storage);
  assert.equal(JSON.parse(f.state.record).state, "restored");
  assert.deepEqual(f.state.writes, [false, true]);
  assert.equal([...f.files].find(([name]) => name.startsWith(f.next + ".interrupted-"))[1], "\0".repeat(78));
});

test("interrupted candidates without reliable original-state evidence block unchanged", async () => {
  for (const record of [null, "\0".repeat(78), JSON.stringify({ schema: "render-transform-preference/v1", previous: true, state: "restored" })]) {
    const f = fixture(); f.state.record = record; f.state.snap = false;
    f.files.set(f.next, JSON.stringify({ schema: "render-transform-preference/v1", previous: true, state: "pending" }));
    const before = [...f.files];
    await assert.rejects(recoverTransformPreference(f.ps, f.storage), /BRIDGE_HOST_BLOCKED/);
    await assert.rejects(withSubpixelTransform(f.ps, f.storage, async () => assert.fail()), /BRIDGE_HOST_BLOCKED/);
    assert.deepEqual([...f.files], before); assert.deepEqual(f.state.writes, []); assert.equal(f.state.snap, false);
  }
});

test("pending recovery restores only the saved original value and does not run a render", async () => {
  const f = fixture(); f.state.snap = false;
  f.state.record = JSON.stringify({ schema: "render-transform-preference/v1", previous: true, state: "pending" });
  f.files.set(f.next, "truncated");
  await recoverTransformPreference(f.ps, f.storage);
  assert.deepEqual(f.state.writes, [true]); assert.equal(f.state.snap, true);
  assert.equal(JSON.parse(f.state.record).state, "restored");
});

test("invalid recovery shapes have a stable diagnostic without raw file contents", async () => {
  for (const raw of ["null", "[]", "{}", "x".repeat(1025)]) {
    const f = fixture(); f.state.record = raw;
    await assert.rejects(recoverTransformPreference(f.ps, f.storage), e => e.message === "BRIDGE_HOST_BLOCKED" && e.preferenceReason === "record_invalid");
    assert.equal(f.state.record, raw); assert.deepEqual(f.state.writes, []);
  }
});
test("malformed recovery backup is never treated as authority to change preferences", async () => {
  const f = fixture(); f.state.record = '{"schema":"unknown"}';
  await assert.rejects(recoverTransformPreference(f.ps, f.storage), /BRIDGE_HOST_BLOCKED/);
  f.state.snap = false;
  await assert.rejects(withSubpixelTransform(f.ps, f.storage, async () => assert.fail()), /BRIDGE_HOST_BLOCKED/);
  assert.deepEqual(f.state.writes, []);
});
test("unreadable interrupted-write backups fail closed regardless of the current preference", async () => {
  for (const record of ["\0".repeat(78), '{"schema":"render-transform-preference/v1","previous":']) {
    for (const snap of [true, false]) {
      const f = fixture(); f.state.record = record; f.state.snap = snap;
      let executed = false;
      await assert.rejects(recoverTransformPreference(f.ps, f.storage));
      await assert.rejects(withSubpixelTransform(f.ps, f.storage, async () => { executed = true; }));
      assert.equal(executed, false);
      assert.equal(f.state.snap, snap);
      assert.equal(f.state.record, record);
      assert.deepEqual(f.state.writes, []);
    }
  }
});
