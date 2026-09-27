"use strict";
const { parseTree, getNodeValue, findNodeAtLocation } = require("jsonc-parser");
const rawPlans = new WeakMap();

function parseStrict(text) {
  const errors = [];
  const root = parseTree(text, errors, { disallowComments: true, allowTrailingComma: false, allowEmptyContent: false });
  if (errors.length || !root || root.type !== "object") throw new Error("BRIDGE_PROTOCOL_MISMATCH");
  function inspect(node, depth) {
    if (depth > 64) throw new Error("BRIDGE_PROTOCOL_MISMATCH");
    if (node.type === "object") {
      const keys = new Set();
      for (const property of node.children) {
        const key = property.children[0].value;
        if (keys.has(key)) throw new Error("BRIDGE_PROTOCOL_MISMATCH");
        keys.add(key);
        inspect(property.children[1], depth + 1);
      }
    } else if (node.type === "array") {
      for (const child of node.children) inspect(child, depth + 1);
    }
  }
  inspect(root, 1);
  const value = getNodeValue(root);
  // Retain only the plan's lexical numbers for cross-language digest validation, never the
  // containing authenticated envelope. JSON.parse would round valid decimal plan coordinates.
  if (value.kind === "claim_response" && value.body?.claim?.plan?.body?.payload?.render_plan) {
    const node = findNodeAtLocation(root, ["body", "claim", "plan", "body", "payload", "render_plan"]);
    rawPlans.set(value.body.claim.plan.body.payload.render_plan, text.slice(node.offset, node.offset + node.length));
  }
  return value;
}

module.exports = { parseStrict, rawPlanJson: value => rawPlans.get(value) };
