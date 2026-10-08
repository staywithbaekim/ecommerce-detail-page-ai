"use strict";
const auth = require("../src/auth");
const secret = Uint8Array.from({ length: 32 }, (_, i) => i);
const challenge = {
  request_id: "request_vector", client_nonce: auth.encode(new Uint8Array(32).fill(1)),
  nonce: auth.encode(new Uint8Array(32).fill(2)), session_id: "session_vector", plugin_instance_id: "instance_vector",
  task_binding: auth.encode(new Uint8Array(32).fill(3)), app_version: "0.1.0", plugin_id: "local.ecommerce.detail-renderer",
  plugin_version: "0.1.0", photoshop_build: "26.8.1 test-build", uxp_runtime_version: "8.1",
  issued_at_unix_seconds: 1800316800, expires_at_unix_seconds: 1800316830,
  capabilities: ["output/jpeg-srgb-icc", "adapter/pull-v1"]
};
challenge.server_proof = auth.challengeProof(secret, challenge);
const key = auth.sessionKey(secret, challenge);
const request = { request_id: "request_mac", session_id: challenge.session_id, plugin_instance_id: challenge.plugin_instance_id,
  sequence: 7, token: auth.encode(new Uint8Array(32).fill(4)), query: { protocol_version: "ps-bridge/v1",
    correlation_id: "corr_query", sent_at: "2026-09-19T00:00:00+00:00", idempotency_key: "query_execution_1" } };
// Public synthetic material only, exercising the approved full build and E3
// capability sets without reading any Windows/UXP protected storage.
const currentBuildCases = [false, true].map(instanceContract => {
  const current = { ...challenge, plugin_version: "0.1.1", photoshop_build: "26.8.1 (20250624.r.8 5e0d05d)",
    uxp_runtime_version: "uxp-8.3.0-uxp-b1d8c85", capabilities: require("../src/pairing-capabilities").createPairingCapabilities()
      .filter(value => value !== "pairing/protected-storage-v1" && (instanceContract || value !== "template/page-instance-identity-v1")) };
  current.server_proof = auth.challengeProof(secret, current);
  const currentKey = auth.sessionKey(secret, current);
  const result = { instance_contract: instanceContract, challenge: current, key: auth.encode(currentKey),
    context_proof: auth.handshakeContextProof(secret, current, '{"值":1}'),
    response_mac: auth.handshakeResponseProof(currentKey, current.request_id, '{"值":1}') };
  currentKey.fill(0);
  return result;
});
process.stdout.write(JSON.stringify({ challenge, key: auth.encode(key),
  request_mac: auth.requestMac(key, request, "POST", "/adapter/v1/claims", "{}"),
  query_mac: auth.requestMac(key, request, "GET", "/executions/execution_1?after_seq=2", ""),
  response_mac: auth.responseMac(key, request, 200, '{"值":1}'),
  transcript_mac: auth.sign(secret, "ps-adapter/v1", "中文", "", "a\nb"), current_build_cases: currentBuildCases }));
