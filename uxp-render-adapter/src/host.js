"use strict";

async function inspectHost(action, host, versions) {
  const descriptors = await action.batchPlay(["hostVersion", "buildNumber"].map(property => ({
    _obj: "get", _target: [{ _property: property }, { _ref: "application", _enum: "ordinal", _value: "targetEnum" }]
  })), { synchronousExecution: true });
  const version = descriptors[0]?.hostVersion;
  const build = descriptors[1]?.buildNumber;
  if (![version?.versionMajor, version?.versionMinor, version?.versionFix].every(Number.isSafeInteger))
    throw new Error("BRIDGE_HOST_BLOCKED");
  const semantic = `${version.versionMajor}.${version.versionMinor}.${version.versionFix}`;
  if (semantic !== "26.8.1" || host?.version !== semantic || typeof build !== "string"
    || !/^26\.8(?:\.1)? \([A-Za-z0-9. -]{1,90}\)$/.test(build)
    || typeof versions.uxp !== "string" || versions.uxp.length > 64)
    throw new Error("BRIDGE_HOST_BLOCKED");
  return { build: `${semantic} ${build.slice(build.indexOf("("))}`, runtime: versions.uxp };
}

function isHostModal(core) {
  const value = typeof core.isModal === "function" ? core.isModal() : core.isModal;
  return typeof value === "boolean" ? value : true;
}

module.exports = { inspectHost, isHostModal };
