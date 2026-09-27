"use strict";
const FILE = "render-transform-preference-v1.json";
const NEXT = FILE + ".next";
const blocked = (reason = "preference_unavailable") => Object.assign(new Error("BRIDGE_HOST_BLOCKED"), { preferenceReason: reason });

async function readSnap(ps) {
  const reply = await ps.action.batchPlay([{ _obj: "get",
    _target: [{ _property: "toolsPreferences" }, { _ref: "application", _enum: "ordinal", _value: "targetEnum" }],
    _options: { dialogOptions: "dontDisplay" } }], { synchronousExecution: true });
  const value = reply[0]?.toolsPreferences?.transformsSnapToPixels;
  if (typeof value !== "boolean") throw blocked();
  return value;
}

async function writeSnap(ps, value) {
  const reply = await ps.action.batchPlay([{ _obj: "set",
    _target: [{ _ref: "property", _property: "toolsPreferences" }, { _ref: "application", _enum: "ordinal", _value: "targetEnum" }],
    to: { _obj: "toolsPreferences", transformsSnapToPixels: value },
    _options: { dialogOptions: "dontDisplay" } }], { synchronousExecution: true });
  if (reply.length !== 1 || reply[0]?._obj === "error" || await readSnap(ps) !== value) throw blocked();
}

async function backupEntries(storage) {
  const folder = await storage.localFileSystem.getDataFolder(), entries = await folder.getEntries();
  return { folder, entry: entries.find(entry => entry.name === FILE) || null,
    next: entries.find(entry => entry.name === NEXT) || null };
}

async function readBackup(entry) {
  try {
    if ((await entry.getMetadata()).size > 1024) throw blocked();
    const value = JSON.parse(await entry.read());
    if (value?.schema !== "render-transform-preference/v1" || value.previous !== true
      || !["pending", "restored"].includes(value.state)
      || Object.keys(value).sort().join() !== "previous,schema,state") throw blocked();
    return value;
  } catch { throw blocked("record_invalid"); }
}

async function saveBackup(folder, state) {
  // Never truncate the sole recovery authority. Do not reuse an interrupted
  // candidate: startup recovery must first preserve it with the original bytes.
  try {
    const next = await folder.createFile(NEXT, { overwrite: false });
    await next.write(JSON.stringify({ schema: "render-transform-preference/v1", previous: true, state }));
    if ((await readBackup(next)).state !== state) throw blocked();
    await next.moveTo(folder, { newName: FILE, overwrite: true });
    const committed = await folder.getEntry(FILE);
    if ((await readBackup(committed)).state !== state) throw blocked();
  } catch { throw blocked("record_write_failed"); }
}

async function retainInterruptedCandidate(folder, next) {
  // No overwrites and no deletion, including when two recoveries share a clock tick.
  await next.moveTo(folder, { newName: `${NEXT}.interrupted-${Date.now()}`, overwrite: false });
}

// Call within an owned Photoshop modal scope. Back up before changing even one
// preference. Retain the recovery record, and restore on every completion path.
async function withSubpixelTransform(ps, storage, callback) {
  // A failed previous restoration must not be mistaken for the user's original
  // disabled setting when reconnecting without reloading the plugin.
  const { folder, entry, next } = await backupEntries(storage);
  if (entry && (await readBackup(entry)).state !== "restored") throw blocked();
  if (next) throw blocked("record_incomplete");
  const previous = await readSnap(ps);
  if (!previous) return callback();
  await saveBackup(folder, "pending");
  try {
    await writeSnap(ps, false);
    return await callback();
  } finally {
    try { await writeSnap(ps, true); } catch { throw blocked("restore_failed"); }
    await saveBackup(folder, "restored");
  }
}

// An interrupted modal may leave a pending backup. Restore only the one recorded
// preference before accepting another pairing; malformed backups fail closed.
async function recoverTransformPreference(ps, storage) {
  const { folder, entry, next } = await backupEntries(storage);
  // A candidate alone is never authority to change a global preference.
  if (!entry) { if (next) throw blocked("record_incomplete"); return; }
  const backup = await readBackup(entry);
  if (backup.state === "restored") {
    if (next) {
      if (!await readSnap(ps)) throw blocked("record_incomplete");
      await retainInterruptedCandidate(folder, next);
    }
    return;
  }
  await ps.core.executeAsModal(async () => {
    try { if (!await readSnap(ps)) await writeSnap(ps, true); } catch { throw blocked("restore_failed"); }
    if (next) await retainInterruptedCandidate(folder, next);
    await saveBackup(folder, "restored");
  }, { commandName: "恢复渲染前的像素对齐设置", timeOut: 1000 });
}

module.exports = { readSnap, withSubpixelTransform, recoverTransformPreference };
