"use strict";
const capability = "template/page-instance-identity-v1";

// Only validates frozen single-plan references. No alias, recipe evaluation or iteration policy.
function validInstanceIdentity(plan) {
  const document = plan.document;
  const present = Object.prototype.hasOwnProperty.call(document, "instance_identity");
  if (present !== plan.required_capabilities.includes(capability)) return false;
  if (!present) return true;
  const identity = document.instance_identity;
  if (!identity || Object.prototype.hasOwnProperty.call(document, "output_variant_id")
    || !/^instance_[0-9a-fA-F]{64}$/.test(identity.page_instance_id)
    || identity.source_page_id !== document.page_id || !Array.isArray(identity.slots) || !Array.isArray(identity.frames)
    || identity.slots.length > 10000 || identity.frames.length > 10000) return false;
  const slots = new Map(), frames = new Map();
  for (const slot of identity.slots) {
    if (slots.has(slot.slot_id)) return false;
    slots.set(slot.slot_id, slot.variant_id);
  }
  for (const frame of identity.frames) {
    if (!slots.has(frame.slot_id) || slots.get(frame.slot_id) !== frame.variant_id
      || typeof frame.asset_id !== "string" || frames.has(frame.frame_id)) return false;
    frames.set(frame.frame_id, frame.asset_id);
  }
  return document.content_type === "dynamic_size_table" ? frames.size === 0
    : frames.size === plan.targets.length && plan.targets.every(target =>
      frames.get(target.frame_id) === target.asset_resource_id);
}

module.exports = { capability, validInstanceIdentity };
