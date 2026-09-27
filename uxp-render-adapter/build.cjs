"use strict";
const fs = require("node:fs");
const path = require("node:path");
const Ajv = require("ajv/dist/2020");
const addFormats = require("ajv-formats");
const standalone = require("ajv/dist/standalone").default;
const esbuild = require("esbuild");
const root = __dirname;
const output = path.join(root, "dist");
fs.mkdirSync(output, { recursive: true });
const ajv = new Ajv({ code: { source: true }, strict: false, allErrors: false });
addFormats(ajv);
const validators = {};
for (const [name, file] of Object.entries({ bridge: "bridge-v1", adapter: "adapter-transport-v1", plan: "render-plan-v1" })) {
  const schema = JSON.parse(fs.readFileSync(path.join(root, "../docs/技术方案/schemas", file + ".schema.json"), "utf8"));
  ajv.addSchema(schema, name);
  validators[name] = name;
}
fs.writeFileSync(path.join(output, "validators.cjs"), standalone(ajv, validators));
esbuild.buildSync({ entryPoints: [path.join(root, "src/main.js")], outfile: path.join(output, "main.js"), bundle: true,
  platform: "browser", format: "cjs", target: "es2020",
  alias: { "jsonc-parser": path.join(root, "node_modules/jsonc-parser/lib/esm/main.js") },
  external: ["photoshop", "uxp", "os", "fs"] });
const manifest = JSON.parse(fs.readFileSync(path.join(root, "manifest.json"), "utf8"));
fs.writeFileSync(path.join(output, "manifest.json"), JSON.stringify(manifest, null, 2));
fs.copyFileSync(path.join(root, "index.html"), path.join(output, "index.html"));
fs.mkdirSync(path.join(output, "icons"), { recursive: true });
for (const name of ["renderer.png", "renderer@2x.png"])
  fs.copyFileSync(path.join(root, "icons", name), path.join(output, "icons", name));
const notices = ["# Runtime Third-Party Notices\n"];
for (const name of ["@noble/hashes", "jsonc-parser", "ajv", "ajv-formats"]) {
  const directory = path.join(root, "node_modules", name);
  const license = fs.readdirSync(directory).find(file => /^license(?:\.|$)/i.test(file));
  if (!license) throw new Error("Missing third-party license: " + name);
  notices.push(`## ${name}\n\n${fs.readFileSync(path.join(directory, license), "utf8")}`);
}
fs.writeFileSync(path.join(output, "THIRD-PARTY-NOTICES.txt"), notices.join("\n\n"));
console.log("Formal adapter development files built; no ccx created.");
