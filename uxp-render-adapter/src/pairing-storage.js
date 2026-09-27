"use strict";
const auth = require("./auth");
const KEY = "render-pairing-v1";

async function hasPairingSecret(storage) {
  const length = storage.length;
  if (!Number.isInteger(length) || length < 0 || length > 1024 || typeof storage.key !== "function")
    throw new Error("BRIDGE_PAIRING_STORAGE_UNAVAILABLE");
  for (let index = 0; index < length; index++) if (await storage.key(index) === KEY) return true;
  return false;
}

async function loadPairingSecret(storage, supplied) {
  if (supplied) return auth.decode32(supplied);
  let bytes;
  try {
    if (!await hasPairingSecret(storage)) throw new Error("BRIDGE_PAIRING_REQUIRED");
    bytes = new Uint8Array(await storage.getItem(KEY));
    if (bytes.length !== 32) throw new Error("BRIDGE_PAIRING_STORAGE_UNAVAILABLE");
    return bytes;
  } catch (error) {
    bytes?.fill(0);
    throw new Error(error?.message === "BRIDGE_PAIRING_REQUIRED"
      ? "BRIDGE_PAIRING_REQUIRED" : "BRIDGE_PAIRING_STORAGE_UNAVAILABLE");
  }
}

// Called only after the peer has proved possession of the supplied secret.
// Backups and readback stay inside UXP's protected storage, never PluginData.
async function rememberPairingSecret(storage, secret) {
  if (!(secret instanceof Uint8Array) || secret.length !== 32) throw new Error("BRIDGE_PAIRING_REJECTED");
  let previous = null;
  let readback = null;
  let writeAttempted = false;
  try {
    if (await hasPairingSecret(storage)) {
      previous = new Uint8Array(await storage.getItem(KEY));
      if (previous.length !== 32) throw new Error("BRIDGE_PAIRING_STORAGE_UNAVAILABLE");
      await storage.setItem(KEY + ".previous", previous);
      readback = new Uint8Array(await storage.getItem(KEY + ".previous"));
      if (readback.length !== 32 || readback.some((value, index) => value !== previous[index]))
        throw new Error("BRIDGE_PAIRING_STORAGE_UNAVAILABLE");
      readback.fill(0);
      readback = null;
    }
    writeAttempted = true;
    await storage.setItem(KEY, secret);
    readback = new Uint8Array(await storage.getItem(KEY));
    if (readback.length !== 32 || readback.some((value, index) => value !== secret[index]))
      throw new Error("BRIDGE_PAIRING_STORAGE_UNAVAILABLE");
  } catch {
    if (writeAttempted) {
      try {
        if (previous) await storage.setItem(KEY, previous);
        else await storage.removeItem(KEY);
      } catch { /* Connection remains blocked even when protected-store rollback fails. */ }
    }
    throw new Error("BRIDGE_PAIRING_STORAGE_UNAVAILABLE");
  } finally { previous?.fill(0); readback?.fill(0); }
}

module.exports = { loadPairingSecret, rememberPairingSecret };
