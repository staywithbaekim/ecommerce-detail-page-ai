"use strict";
const { test } = require("node:test");
const assert = require("node:assert/strict");
const { parseStrict } = require("../src/json");

test("strict JSON retains literal cell text and refuses ambiguous wire documents", () => {
  const parsed = parseStrict('{"cells":["/","\\\\","-"],"nested":{"x":1}}');
  assert.deepEqual(parsed.cells, ["/", "\\", "-"]);
  assert.equal(parsed.nested.x, 1);
  assert.equal(Object.getPrototypeOf(parsed), null);
  for (const value of ['{"a":1,"a":2}', '{"a":1,"\\u0061":2}', '{"a":{"x":1,"x":2}}',
    '{/*comment*/"a":1}', '{"a":1,}', '{} {}', 'null', '[{}]', '{']) {
    assert.throws(() => parseStrict(value), { message: "BRIDGE_PROTOCOL_MISMATCH" });
  }
  const deep = '{"x":'.repeat(65) + '{}' + '}'.repeat(65);
  assert.throws(() => parseStrict(deep), { message: "BRIDGE_PROTOCOL_MISMATCH" });
});
