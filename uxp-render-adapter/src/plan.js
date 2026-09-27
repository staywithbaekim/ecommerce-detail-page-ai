"use strict";
const { parseTree } = require("jsonc-parser");
const validators = require("../dist/validators.cjs");
const { hash } = require("./auth");
const { rawPlanJson, parseStrict } = require("./json");

const fail = (code = "BRIDGE_PLAN_INVALID") => { throw new Error(code); };
const sameDigest = (a, b) => typeof a === "string" && typeof b === "string" && a.toLowerCase() === b.toLowerCase();

// Utf8JsonWriter's default BasicLatin encoder, including HTML-sensitive ASCII and UTF-16 escapes.
function quote(value) {
  let result = '"';
  const short = { 8: "\\b", 9: "\\t", 10: "\\n", 12: "\\f", 13: "\\r", 92: "\\\\" };
  for (let i = 0; i < value.length; i++) {
    const c = value.charCodeAt(i);
    if (short[c]) result += short[c];
    else if (c < 32 || c > 126 || [34, 38, 39, 43, 60, 62, 96].includes(c))
      result += "\\u" + c.toString(16).toUpperCase().padStart(4, "0");
    else result += value[i];
  }
  return result + '"';
}

// Match the decimal/G29 domain used by RenderPlanDigest. Values needing decimal rounding are
// rejected instead of allowing JavaScript binary rounding to change an authenticated plan.
function number(raw) {
  const match = /^(-?)(\d+)(?:\.(\d+))?(?:[eE]([+-]?\d+))?$/.exec(raw);
  if (!match) return fail();
  let digits = (match[2] + (match[3] || "")).replace(/^0+/, "");
  if (!digits) return "0";
  let power = Number(match[4] || 0) - (match[3] || "").length;
  if (!Number.isSafeInteger(power) || Math.abs(power) > 1000) return fail();
  while (digits.endsWith("0")) { digits = digits.slice(0, -1); power++; }
  if (digits.length > 29 || power < -28 || digits.length + power > 29) return fail();
  const integer = digits + "0".repeat(Math.max(0, power));
  if (power >= 0 && integer.length === 29 && integer > "79228162514264337593543950335") return fail();
  const exponent = digits.length + power - 1;
  if (exponent < -4) return match[1] + digits[0] + (digits.length > 1 ? "." + digits.slice(1) : "")
    + "E-" + String(-exponent).padStart(2, "0");
  const point = digits.length + power;
  return match[1] + (power >= 0 ? integer : point > 0 ? digits.slice(0, point) + "." + digits.slice(point)
    : "0." + "0".repeat(-point) + digits);
}

function computePlanDigest(json) {
  parseStrict(json);
  const tree = parseTree(json);
  function canonical(node, root = false) {
    if (node.type === "object") return "{" + [...node.children]
      .sort((a, b) => a.children[0].value < b.children[0].value ? -1 : a.children[0].value > b.children[0].value ? 1 : 0)
      .map(property => quote(property.children[0].value) + ":" + (root && property.children[0].value === "plan_digest"
        ? quote("sha256:" + "0".repeat(64)) : canonical(property.children[1]))).join(",") + "}";
    if (node.type === "array") return "[" + node.children.map(child => canonical(child)).join(",") + "]";
    if (node.type === "string") return quote(node.value);
    if (node.type === "number") return number(json.slice(node.offset, node.offset + node.length));
    return node.type === "null" ? "null" : String(node.value);
  }
  return "sha256:" + hash(canonical(tree, true));
}

function validateClaim(claim, session) {
  if (!validators.adapter({ transport_version: "ps-adapter/v1", kind: "claim_response", body: { has_work: true, claim } })
    || claim.state !== "claimed" || !claim.plan || !validators.bridge(claim.plan)
    || claim.plan.envelope_kind !== "request" || claim.plan.body.operation !== "submit_plan") return fail();
  const body = claim.plan.body, payload = body.payload, plan = payload.render_plan;
  if (!validators.plan(plan) || body.session_id !== session.session_id || body.token !== session.token
    || !Array.isArray(session.capabilities) || plan.required_capabilities.some(capability => !session.capabilities.includes(capability))
    || body.correlation_id !== plan.correlation_id || body.idempotency_key !== plan.idempotency_key
    || !sameDigest(payload.plan_digest, plan.plan_digest) || !sameDigest(claim.plan_digest, plan.plan_digest)
    || !sameDigest(computePlanDigest(rawPlanJson(plan) || JSON.stringify(plan)), plan.plan_digest)) return fail();
  const resources = new Map(), bindings = new Map(), handles = new Set();
  for (const resource of plan.resources) {
    if (resources.has(resource.resource_id)) return fail();
    resources.set(resource.resource_id, resource);
  }
  for (const binding of payload.resource_bindings) {
    const resource = resources.get(binding.resource_id);
    if (!resource || bindings.has(binding.resource_id) || handles.has(binding.session_handle)
      || resource.role !== binding.purpose || !sameDigest(resource.sha256, binding.sha256)
      || binding.byte_length !== resource.byte_length || !binding.read_only) return fail("BRIDGE_RESOURCE_BINDING_INVALID");
    bindings.set(binding.resource_id, binding); handles.add(binding.session_handle);
  }
  if (bindings.size !== resources.size || resources.get(plan.document.template_resource_id)?.role !== "template_copy") return fail();
  const used = new Set([plan.document.template_resource_id]), frames = new Set(), layers = new Set();
  if (Object.prototype.hasOwnProperty.call(plan.document, "output_variant_id") !== plan.required_capabilities.includes("output/variant-identity-v1")) return fail();
  const explicitPlacement = plan.required_capabilities.includes("layout/absolute-placement-v1");
  const calibratedSizeTable = plan.required_capabilities.includes("size-table/calibrated-target-v1");
  const inserting = plan.required_capabilities.includes("size-table/insert-layer-v1");
  if (explicitPlacement && plan.targets.length === 0 && !inserting) return fail();
  if (inserting) {
    const insertion = plan.size_table.insertion, p = insertion.placement, b = insertion.safe_bounds;
    if (Math.abs(p.width * p.source_height - p.height * p.source_width)
        > Math.max(p.width * p.source_height, p.height * p.source_width) * 0.000001
      || p.left < b.left || p.top < b.top || p.left + p.width > b.right || p.top + p.height > b.bottom
      || b.right > plan.document.canvas.width || b.bottom > plan.document.canvas.height
      || new Set(insertion.hidden_layers.map(layer => layer.layer_id)).size !== insertion.hidden_layers.length
      || new Set(insertion.hidden_layers.map(layer => layer.layer_path[0])).size !== insertion.hidden_layers.length) return fail();
  }
  for (const target of plan.targets) {
    const path = target.layer_path.join("/");
    const bounds = target.layout.safe_bounds;
    if (frames.has(target.frame_id) || layers.has(path) || resources.get(target.asset_resource_id)?.role !==
        (calibratedSizeTable ? "deterministic_size_table" : "high_resolution_render_source")
      || bounds.right <= bounds.left || bounds.bottom <= bounds.top
      || bounds.right > plan.document.canvas.width || bounds.bottom > plan.document.canvas.height) return fail();
    frames.add(target.frame_id); layers.add(path); used.add(target.asset_resource_id);
    const p = target.layout.placement;
    if (explicitPlacement !== Boolean(p)) return fail();
    if (p && (Math.abs(p.width * p.source_height - p.height * p.source_width)
        > Math.max(p.width * p.source_height, p.height * p.source_width) * 0.000001
      || p.left >= plan.document.canvas.width || p.top >= plan.document.canvas.height
      || p.left + p.width <= 0 || p.top + p.height <= 0)) return fail();
    if (calibratedSizeTable && (p.left < bounds.left || p.top < bounds.top
      || p.left + p.width > bounds.right || p.top + p.height > bounds.bottom)) return fail();
  }
  if (plan.size_table) {
    if (resources.get(plan.size_table.render_resource_id)?.role !== "deterministic_size_table") return fail();
    if (calibratedSizeTable && (plan.targets[0].asset_resource_id !== plan.size_table.render_resource_id
      || resources.get(plan.size_table.render_resource_id).media_type !== "image/png"
      || !sameDigest(plan.size_table.grid_digest, "sha256:" + resources.get(plan.size_table.render_resource_id).sha256))) return fail();
    if (inserting && (resources.get(plan.size_table.render_resource_id).media_type !== "image/png"
      || !sameDigest(plan.size_table.grid_digest, "sha256:" + resources.get(plan.size_table.render_resource_id).sha256))) return fail();
    used.add(plan.size_table.render_resource_id);
  }
  if ([...resources.keys()].some(id => !used.has(id))) return fail();
  const outputs = payload.output_bindings, output = outputs[0];
  if (outputs.length !== 1 || output.artifact_set_id !== plan.output.artifact_set_id
    || output.overwrite_policy !== plan.output.overwrite_policy || handles.has(output.output_root_handle)
    || (output.overwrite_policy === "never" ? output.confirmed_target_ids.length !== 0 : output.confirmed_target_ids.length === 0))
    return fail("BRIDGE_RESOURCE_BINDING_INVALID");
  return { plan, bindings, output };
}

module.exports = { computePlanDigest, validateClaim };
