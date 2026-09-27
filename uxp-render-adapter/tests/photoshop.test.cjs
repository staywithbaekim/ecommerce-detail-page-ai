"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { randomBytes } = require("node:crypto");
const { AdapterWorkLoop } = require("../src/work");
const { PhotoshopPageExecutor, resolveLayer, axisAlignedBox, ordinaryRotation, requireExecutable, identityWarp } = require("../src/photoshop");
const example = JSON.parse(fs.readFileSync(path.join(__dirname, "../../docs/技术方案/schemas/render-plan-v1.valid.example.json"), "utf8"));

function fixture(sourceName = "resource_owned.jpg") {
  const plan = structuredClone(example), entries = [], state = { modal: false, opens: 0, replacements: 0, saves: [], closes: 0, boundaries: 0 };
  plan.required_capabilities.push("layout/absolute-placement-v1");
  plan.output.editable_psd = { enabled: true, file_name: "01-detail.psd" };
  plan.targets[0].layout.placement = { coordinate_space: "document_pixels", source_width: 4000, source_height: 6000,
    left: -5, top: 10, width: 800, height: 1200, clipping: "preserve_template_masks_and_canvas" };
  const descriptor = { smartObject: { linked: false }, smartObjectMore: { ID: "embedded_1", transform: [0, 0, 400, 0, 400, 600, 0, 600],
    size: { width: 4000, height: 6000 }, warp: { warpStyle: { _value: "warpNone" } } } };
  const target = { id: 257, name: "product_frame", kind: "smartObject", visible: true, opacity: 100, blendMode: "normal",
    bounds: { left: 0, top: 0, right: 400, bottom: 600 },
    async scale(sx, sy) { descriptor.smartObjectMore.transform = descriptor.smartObjectMore.transform.map((n, i) => n * (i % 2 ? sy : sx) / 100); },
    async translate(x, y) { descriptor.smartObjectMore.transform = descriptor.smartObjectMore.transform.map((n, i) => n + (i % 2 ? y : x)); } };
  const layers = new Map([[target.id, target]]), descriptors = new Map([[target.id, descriptor]]);
  const fixed = { id: 258, name: "fixed", kind: "pixel", visible: true, opacity: 100, blendMode: "normal", bounds: { left: 0, top: 0, right: 790, bottom: 1252 } };
  const doc = { id: 42, name: "resource_owned.psd", width: 790, height: 1252, mode: "RGBColor", bitsPerChannel: 8,
    layers: [target, fixed], colorProfileName: "Adobe RGB (1998)",
    async convertProfile(profile) { this.colorProfileName = profile; },
    saveAs: { async psd(entry, options, copy) { assert.equal(copy, true); assert.equal(options.embedColorProfile, true); state.saves.push("psd"); },
      async jpg(entry, options, copy) { assert.equal(copy, true); assert.equal(options.embedColorProfile, true); state.saves.push("jpg"); } },
    async closeWithoutSaving() { assert.equal(ps.app.activeDocument.id, this.id); state.closes++; ps.app.documents = []; ps.app.activeDocument = null; } };
  const ps = { app: { documents: [], activeDocument: null, async open(entry) {
    assert.equal(entry.name, "resource_owned.psd"); state.opens++; this.documents = [doc]; this.activeDocument = doc; return doc;
  } }, core: { isModal: () => state.modal, async executeAsModal(callback) {
    state.modal = true; try { return await callback({ isCancelled: false }); } finally { state.modal = false; }
  } }, constants: { AnchorPosition: { MIDDLECENTER: "center" }, Intent: { RELATIVECOLORIMETRIC: "relative" } },
    action: { async batchPlay(commands) { return commands.map(command => {
      if (command._obj === "get") {
        if (command._target[0]._property === "toolsPreferences") return { toolsPreferences: { transformsSnapToPixels: false } };
        if (command._target[0]._property === "mode") return { mode: { _enum: "colorSpace", _value: "RGBColor" } };
        if (command._target[0]._property === "depth") return { depth: 8 };
        return descriptors.has(command._target[0]._id) ? structuredClone(descriptors.get(command._target[0]._id)) : {};
      }
      if (command._obj === "placedLayerReplaceContents") {
        const selected = commands.find(item => item._obj === "select")?._target?.[0]?._id;
        state.replacements++;
        const selectedLayer = layers.get(selected);
        if (selectedLayer) selectedLayer.name = "replacement";
      }
      if (command._obj === "transform") {
        const id = command._target[0]._id, currentTarget = layers.get(id), currentDescriptor = descriptors.get(id);
        assert.ok(currentTarget); assert.ok(currentDescriptor);
        assert.equal(command.freeTransformCenterState._value, "QCSAverage");
        if (command.angle) {
          const q = currentDescriptor.smartObjectMore.transform;
          const cx = (q[0] + q[4]) / 2, cy = (q[1] + q[5]) / 2, angle = command.angle._value * Math.PI / 180;
          currentDescriptor.smartObjectMore.transform = q.flatMap((_, i) => i % 2 ? [] : [
            cx + (q[i] - cx) * Math.cos(angle) - (q[i + 1] - cy) * Math.sin(angle),
            cy + (q[i] - cx) * Math.sin(angle) + (q[i + 1] - cy) * Math.cos(angle)]);
          state.unrotations = (state.unrotations || 0) + 1;
          if (state.failUnrotate) currentDescriptor.smartObjectMore.transform = q;
          return {};
        }
        const box = axisAlignedBox(currentDescriptor), cx = box.left + box.width / 2, cy = box.top + box.height / 2;
        const sx = command.width ? command.width._value / 100 : 1;
        const sy = command.height ? command.height._value / 100 : 1;
        const dx = command.offset?.horizontal?._value || 0;
        const dy = command.offset?.vertical?._value || 0;
        currentDescriptor.smartObjectMore.transform = currentDescriptor.smartObjectMore.transform.map((n, i) => i % 2
          ? cy + (n - cy) * sy + dy
          : cx + (n - cx) * sx + dx);
        state.afterTransform?.();
      }
      return {};
    }); } } };
  const storage = { localFileSystem: { createSessionToken(entry) { assert.equal(entry.name, sourceName); return "ephemeral_fixture"; },
    async getDataFolder() { return { async getEntries() { return []; } }; },
    async getTemporaryFolder() { return { async createFile(name, options) {
      assert.equal(options.overwrite, false);
      const entry = { name, deleted: false, async delete() { this.deleted = true; } }; entries.push(entry); return entry;
    } }; } } };
  const prepared = { plan, leases: new Map(plan.resources.map((resource, i) => [resource.resource_id,
    { getEntry: () => ({ name: i ? sourceName : "resource_owned.psd" }) }])) };
  const executor = new PhotoshopPageExecutor(ps, storage, randomBytes);
  const boundary = async () => { state.boundaries++; assert.equal(state.modal, false); };
  return { plan, prepared, executor, ps, doc, target, fixed, descriptor, layers, descriptors, state, entries, boundary };
}

test("one-based nested bottom-to-top paths never use historical zero-based paths", () => {
  const leaf = { id: 9 }, doc = { layers: [{ layers: [leaf, { id: 8 }] }, { id: 7 }] };
  assert.equal(resolveLayer(doc, [2, 2]).id, 9);
  assert.throws(() => resolveLayer(doc, [0]), /BRIDGE_PLAN_INVALID/);
  assert.throws(() => resolveLayer(doc, [3]), /BRIDGE_PLAN_INVALID/);
});

test("PSD tabs use the exact observed Photoshop space representation without trimming names", async () => {
  for (const name of ["\tfabric_detail", "\tfront", "\tsleeve", " \tframe\t "]) {
    const f = fixture();
    f.plan.targets[0].expected_layer.name_before = name;
    f.target.name = name.replace(/\t/g, " ");
    const records = await f.executor.snapshot(f.doc);
    f.executor.preflight(f.plan, records);
    for (const changed of [f.target.name.trim(), f.target.name + " ", f.target.name.replace(/ /g, "\n")]) {
      const altered = structuredClone(records);
      altered.find(r => r.id === f.target.id).name = changed;
      assert.throws(() => f.executor.preflight(f.plan, altered), /BRIDGE_PLAN_INVALID/);
    }
    assert.equal(f.plan.targets[0].expected_layer.name_before, name);
  }
});

test("JPG-only known-target preflight accepts a placeholder source size before replacement", async () => {
  const f = fixture();
  f.descriptor.smartObjectMore.size = { width: 1024, height: 1024 };
  await f.executor.preflightKnownTargets(f.plan, f.doc);
});

test("JPG-only known-target preflight accepts a clipped superseded smart object", async () => {
  const f = fixture(), old = { ...f.fixed, id: 259, kind: "smartObject", name: "old clipped product", visible: true };
  f.doc.layers.unshift(old);
  f.plan.targets[0].superseded_layers = [{ layer_path: [3], layer_path_order: "psd_bottom_to_top",
    expected_kind: "smartObject", expected_visible: true, expected_clipping: true, expected_layer_id: 259 }];
  const base = f.ps.action.batchPlay.bind(f.ps.action);
  f.ps.action.batchPlay = async commands => {
    const result = await base(commands);
    return result.map((value, index) => commands[index]._obj === "get" && commands[index]._target[0]._id === 259
      ? { group: true, smartObject: { linked: false } } : value);
  };
  await f.executor.preflightKnownTargets(f.plan, f.doc);
});

function sizeInsertionFixture() {
  const f = fixture("table.png"), p = f.plan;
  const originalTarget = { ...f.target };
  // Use the old target object as a fixed group, and create a genuinely new layer.
  const group = { ...originalTarget, id: 2, name: "old table", kind: "group", layers: [] };
  const child = { ...f.fixed, id: 3, name: "old cell", kind: "text", visible: true };
  group.layers.push(child);
  f.doc.layers = [group, f.fixed];
  const ref = { layer_path:[2], layer_id:2, kind:"group", name:"old table" };
  p.document.content_type="dynamic_size_table"; p.required_capabilities.push("size-table/insert-layer-v1");
  p.size_table={render_resource_id:p.resources[1].resource_id,rows:[["L","-\\/"]],insertion:{
    layer_name:"confirmed table",above_layer:ref,hidden_layers:[ref],placement:p.targets[0].layout.placement}};
  p.targets=[];
  f.ps.constants.ElementPlacement={PLACEBEFORE:"before"}; f.ps.constants.BlendMode={NORMAL:"normal"};
  const base=f.ps.action.batchPlay.bind(f.ps.action);
  f.ps.action.batchPlay=async commands=>{
    if(commands[0]._obj==="get" && commands[0]._target[0]._id===2) return [{itemIndex:10}];
    if(commands[0]._obj==="get" && commands[0]._target[0]._id===257) {
      const result=await base(commands); result[0].itemIndex=f.state.newLayerIndex??20; return result;
    }
    if(commands[0]._obj==="placeEvent"){
      assert.equal(commands[0].linked,false); f.state.insertions=(f.state.insertions||0)+1;
      f.doc.layers.unshift(f.target); f.doc.activeLayers=[f.target];
      f.target.move=()=>{throw Error("Group-anchor DOM moveAbove must not be used");};
      return [{}];
    }
    if(commands[0]._obj==="move"){
      const command=commands[0];
      assert.equal(command._target[0]._id,257); assert.equal(command.version,5); assert.equal(command.adjustment,true);
      f.doc.layers.splice(f.doc.layers.indexOf(f.target),1);
      // Native index 10 is the boundary above the group; 9 is inside it.
      // Removing a source from below lowers both indices by one.
      const groupBoundary=(f.state.newLayerIndex??20)<10?9:10;
      if(command.to._index===groupBoundary && !f.state.forceInside) f.doc.layers.splice(f.doc.layers.indexOf(group),0,f.target);
      else group.layers.unshift(f.target);
      return [{}];
    }
    return base(commands);
  };
  return {...f,group,child};
}

test("explicit size insertion hides the calibrated group without deleting old cells or altering their local visibility",async()=>{
  const f=sizeInsertionFixture(), original=structuredClone(f.plan.size_table.rows);
  const result=await f.executor.execute(f.prepared,f.boundary,async()=>{});
  assert.equal(f.state.insertions,1); assert.equal(f.state.replacements,0);
  assert.equal(f.group.visible,false); assert.equal(f.child.visible,true); assert.equal(f.fixed.visible,true);
  assert.equal(f.target.name,"confirmed table"); assert.deepEqual(f.plan.size_table.rows,original);
  assert.equal(f.doc.layers.length,3); assert.equal(f.group.layers.length,1);
  assert.deepEqual(f.state.saves,["psd","jpg"]);
  await Promise.all([...result.outputs.values()].map(output=>output.dispose()));
});

test("native size insertion accounts for a source below the group anchor",async()=>{
  const f=sizeInsertionFixture(); f.state.newLayerIndex=5;
  const result=await f.executor.execute(f.prepared,f.boundary,async()=>{});
  assert.deepEqual(f.doc.layers.map(layer=>layer.id),[257,2,258]);
  assert.equal(f.group.layers.length,1);
  await Promise.all([...result.outputs.values()].map(output=>output.dispose()));
});

test("a host placing the new table inside the group fails before hiding or exporting",async()=>{
  const f=sizeInsertionFixture(); f.state.forceInside=true;
  await assert.rejects(f.executor.execute(f.prepared,f.boundary,async()=>{}),/BRIDGE_PLAN_INVALID/);
  assert.equal(f.group.visible,true); assert.equal(f.state.saves.length,0); assert.equal(f.state.closes,1);
  assert.deepEqual(f.executor.failure,{code:"BRIDGE_PLAN_INVALID",cleanupConfirmed:true});
});

test("unconfirmed document cleanup cannot be reported as a safe page failure",async()=>{
  const f=sizeInsertionFixture(); f.state.forceInside=true;
  f.doc.closeWithoutSaving=async()=>{throw Error("host close failed");};
  await assert.rejects(f.executor.execute(f.prepared,f.boundary,async()=>{}),/BRIDGE_PLAN_INVALID/);
  assert.equal(f.executor.failure,null); assert.equal(f.executor.documentId,42);
});

for(const mutation of ["anchor","hidden","collision","extra","fixed_change"]){
  test(`size insertion rejects ${mutation} without publishing`,async()=>{
    const f=sizeInsertionFixture();
    if(mutation==="anchor") f.plan.size_table.insertion.above_layer={...f.plan.size_table.insertion.above_layer,layer_id:99};
    if(mutation==="hidden") f.plan.size_table.insertion.hidden_layers=[{...f.plan.size_table.insertion.above_layer,layer_path:[1]}];
    if(mutation==="collision") f.plan.size_table.insertion.layer_name=f.fixed.name;
    if(mutation==="extra") f.state.afterTransform=()=>f.doc.layers.unshift({...f.fixed,id:99});
    if(mutation==="fixed_change") f.state.afterTransform=()=>f.fixed.visible=false;
    await assert.rejects(f.executor.execute(f.prepared,f.boundary,async()=>{}),/BRIDGE_PLAN_INVALID/);
    assert.equal(f.state.saves.length,0); assert.equal(f.state.closes,1);
  });
}

test("explicit size-table target uses the same controlled replacement and export path without changing cells", async () => {
  const f = fixture("deterministic_table.png");
  f.plan.document.content_type = "dynamic_size_table";
  f.plan.resources[1].role = "deterministic_size_table";
  f.plan.resources[1].media_type = "image/png";
  f.plan.size_table = { render_resource_id: f.plan.resources[1].resource_id, columns: ["尺码", "特殊原文"], rows: [["L", "-\\/"]] };
  const original = structuredClone(f.plan.size_table);
  await assert.rejects(f.executor.execute(f.prepared, f.boundary, async () => {}), /BRIDGE_PLAN_INVALID/);
  assert.equal(f.state.opens, 0);
  f.plan.required_capabilities.push("size-table/calibrated-target-v1");
  const result = await f.executor.execute(f.prepared, f.boundary, async () => {});
  assert.equal(f.state.replacements, 1); assert.deepEqual(f.state.saves, ["psd", "jpg"]);
  assert.deepEqual(f.plan.size_table, original);
  assert.equal(f.state.closes, 1);
  await Promise.all([...result.outputs.values()].map(output => output.dispose()));
});

test("explicit source-proportional placement corrects existing unequal template scales", async () => {
  const f = fixture();
  f.descriptor.smartObjectMore.transform = [0, 0, 400, 0, 400, 600.1, 0, 600.1];
  await f.executor.execute(f.prepared, f.boundary, async () => {});
  const actual = axisAlignedBox(f.descriptor);
  assert.deepEqual(actual, { left: -5, top: 10, width: 800, height: 1200 });
  assert.equal(actual.width / actual.height, 4000 / 6000);
});

test("ordinary execution uses only owned copies and exact geometry, preserves fixed layers, exports copies and closes", async () => {
  const f = fixture(), fixedBefore = structuredClone(f.fixed);
  Object.defineProperty(f.doc, "mode", { get() { throw "Unsupported DOM property"; } });
  const result = await f.executor.execute(f.prepared, f.boundary, async () => {});
  assert.equal(f.state.opens, 1); assert.equal(f.state.replacements, 1); assert.equal(f.state.closes, 1);
  assert.deepEqual(f.fixed, fixedBefore); assert.deepEqual(f.state.saves, ["psd", "jpg"]);
  assert.deepEqual(axisAlignedBox(f.descriptor), { left: -5, top: 10, width: 800, height: 1200 });
  assert.equal(result.evidence.fixed_structure_unchanged, true); assert.equal(result.outputs.size, 2);
  assert.equal(f.executor.inspect().ready, true);
  await Promise.all([...result.outputs.values()].map(output => output.dispose()));
  assert.ok(f.entries.every(entry => entry.deleted));
});

test("multiple target replacements share one page modal and pulse between targets", async () => {
  const f = fixture();
  const target2 = { ...f.target, id: 259, name: "product_frame_2" };
  const descriptor2 = structuredClone(f.descriptor);
  descriptor2.smartObjectMore.ID = "embedded_2";
  f.layers.set(target2.id, target2); f.descriptors.set(target2.id, descriptor2);
  f.doc.layers = [target2, f.target, f.fixed];
  const second = structuredClone(f.plan.targets[0]);
  second.layer_path = [3]; second.expected_layer.layer_id = target2.id; second.expected_layer.name_before = target2.name;
  f.plan.targets.push(second);
  let modalCalls = 0, pulses = 0;
  const executeAsModal = f.ps.core.executeAsModal.bind(f.ps.core);
  f.ps.core.executeAsModal = async (...args) => { modalCalls++; return executeAsModal(...args); };
  const result = await f.executor.execute(f.prepared, f.boundary, async () => { pulses++; });
  assert.equal(f.state.replacements, 2);
  assert.equal(f.state.boundaries, 5);
  assert.equal(modalCalls, 7);
  assert.equal(pulses, modalCalls + 1);
  assert.deepEqual(f.state.saves, ["psd", "jpg"]);
  await Promise.all([...result.outputs.values()].map(output => output.dispose()));
});

test("JPG-only skips PSD save and full structure audit while retaining target preflight and geometry checks", async () => {
  const f = fixture();
  f.plan.output.editable_psd = { enabled: false, file_name: null };
  let snapshots = 0, audits = 0;
  const snapshot = f.executor.snapshot.bind(f.executor), audit = f.executor.audit.bind(f.executor);
  f.executor.snapshot = async (...args) => { snapshots++; return snapshot(...args); };
  f.executor.audit = (...args) => { audits++; return audit(...args); };
  const result = await f.executor.execute(f.prepared, f.boundary, async () => {});
  assert.deepEqual([...result.outputs.keys()], ["jpg"]);
  assert.deepEqual(f.state.saves, ["jpg"]);
  assert.equal(snapshots, 0); assert.equal(audits, 0);
  assert.equal(result.evidence.fixed_structure_unchanged, false);
  assert.equal(f.state.closes, 1);
  await Promise.all([...result.outputs.values()].map(lease => lease.dispose()));
  assert.ok(f.entries.every(entry => entry.deleted));
});

test("JPG-only hides the preflight-identified superseded layer without losing its identity map", async () => {
  const f = fixture(), old = { ...f.fixed, id: 259, name: "old product", kind: "smartObject", visible: true };
  f.doc.layers.unshift(old);
  f.plan.output.editable_psd = { enabled: false, file_name: null };
  f.plan.targets[0].superseded_layers = [{ layer_path: [3], layer_path_order: "psd_bottom_to_top",
    expected_kind: "smartObject", expected_visible: true, expected_clipping: false, expected_layer_id: 259 }];
  const result = await f.executor.execute(f.prepared, f.boundary, async () => {});
  assert.equal(old.visible, false);
  assert.deepEqual(f.state.saves, ["jpg"]);
  assert.deepEqual([...result.outputs.keys()], ["jpg"]);
  await Promise.all([...result.outputs.values()].map(lease => lease.dispose()));
});

for (const mutation of ["legacy", "external_document", "path", "layer_id", "mask", "skew", "linked", "source_size", "shared"]) {
  test(`Photoshop ${mutation} rejection never exports or closes unrelated documents`, async () => {
    const f = fixture();
    if (mutation === "legacy") f.plan.targets[0].layout.placement = undefined;
    if (mutation === "external_document") f.ps.app.documents = [{ id: 999, name: "user.psd" }];
    if (mutation === "path") f.plan.targets[0].layer_path = [1];
    if (mutation === "layer_id") f.plan.targets[0].expected_layer.layer_id = 1;
    if (mutation === "mask") f.descriptor.hasUserMask = true;
    if (mutation === "skew") f.descriptor.smartObjectMore.transform[3] = 5;
    if (mutation === "linked") f.descriptor.smartObject.linked = true;
    if (mutation === "source_size") f.descriptor.smartObjectMore.size.width = 3999;
    if (mutation === "shared") f.descriptor.smartObjectMore.ID = undefined;
    await assert.rejects(f.executor.execute(f.prepared, f.boundary, async () => {}), /BRIDGE_(PLAN_INVALID|HOST_BLOCKED)/);
    assert.equal(f.state.saves.length, 0);
    if (mutation === "external_document") { assert.equal(f.state.closes, 0); assert.equal(f.ps.app.documents[0].id, 999); }
    else assert.equal(f.ps.app.documents.length, 0);
    if (!["source_size"].includes(mutation)) assert.equal(f.state.replacements, 0);
  });
}

test("cancellation at a modal boundary closes only the owned document and deletes only its new outputs", async () => {
  const f = fixture();
  await assert.rejects(f.executor.execute(f.prepared, async () => {
    await f.boundary(); if (f.state.saves.length) throw new Error("BRIDGE_EXECUTION_CANCELLED");
  }, async () => {}), /BRIDGE_EXECUTION_CANCELLED/);
  assert.deepEqual(f.state.saves, ["psd"]); assert.equal(f.state.closes, 1); assert.ok(f.entries.every(entry => entry.deleted));
});

test("authenticated bridge loss while an owned document is open still performs local guarded cleanup", async () => {
  const f = fixture(), lost = new Error("BRIDGE_SESSION_LOST");
  let pulses = 0;
  await assert.rejects(f.executor.execute(f.prepared, f.boundary, async () => {
    pulses++;
    if (pulses >= 4) { assert.equal(f.state.modal, true); throw lost; }
  }), error => error === lost);
  assert.equal(pulses, 4);
  assert.equal(f.state.replacements, 1);
  assert.equal(f.state.closes, 1);
  assert.equal(f.ps.app.documents.length, 0);
  assert.equal(f.executor.documentId, null);
  assert.equal(f.executor.documentName, null);
  assert.equal(f.executor.failure, null);
});

test("local bridge-loss cleanup preserves every owned-document identity guard", async () => {
  const mutations = {
    id(f) { const foreign = { id: 99, name: f.doc.name }; f.ps.app.documents = [foreign]; f.ps.app.activeDocument = foreign; },
    name(f) { f.doc.name = "renamed.psd"; },
    active(f) { f.ps.app.activeDocument = { id: 99, name: "user.psd" }; },
    only_document(f) { f.ps.app.documents.push({ id: 99, name: "user.psd" }); }
  };
  for (const mutate of Object.values(mutations)) {
    const f = fixture(), lost = new Error("BRIDGE_SESSION_LOST");
    let pulses = 0;
    await assert.rejects(f.executor.execute(f.prepared, f.boundary, async () => {
      pulses++;
      if (pulses >= 4) { mutate(f); throw lost; }
    }), error => error === lost);
    assert.equal(f.state.closes, 0);
    assert.equal(f.executor.documentId, 42);
    assert.equal(f.executor.failure, null);
  }
});

const realCollarQuad = [-230.60875600987174, 440.83081886424316, 559, 465.69067470740856,
  525.8640149330972, 1518.1691811357568, -263.7447410767744, 1493.3093252925914];

test("real collar rigid rotation resolves to the explicit upright placement and keeps the fixed layer", async () => {
  const f = fixture();
  f.descriptor.smartObjectMore.transform = [...realCollarQuad];
  const fixed = structuredClone(f.fixed);
  assert.ok(Math.abs(ordinaryRotation(f.descriptor) - 1.803275246) < 0.01);
  const result = await f.executor.execute(f.prepared, f.boundary, async () => {});
  assert.equal(f.state.unrotations, 1);
  assert.equal(f.state.replacements, 1);
  assert.deepEqual(f.fixed, fixed);
  assert.deepEqual(f.state.saves, ["psd", "jpg"]);
  const actual = axisAlignedBox(f.descriptor), p = f.plan.targets[0].layout.placement;
  for (const key of ["left", "top", "width", "height"]) assert.ok(Math.abs(actual[key] - p[key]) < 0.001);
  await Promise.all([...result.outputs.values()].map(lease => lease.dispose()));
});

test("a rotation command that does not produce an upright result cannot export", async () => {
  const f = fixture();
  f.descriptor.smartObjectMore.transform = [...realCollarQuad];
  f.state.failUnrotate = true;
  await assert.rejects(f.executor.execute(f.prepared, f.boundary, async () => {}), /BRIDGE_PLAN_INVALID/);
  assert.equal(f.state.saves.length, 0);
  assert.equal(f.ps.app.documents.length, 0);
});

for (const kind of ["reflection", "perspective", "non_affine", "warp", "zero", "overflow"]) {
  test(`ordinary rotation rejects ${kind} before replacing or exporting`, async () => {
    const f = fixture(), more = f.descriptor.smartObjectMore;
    more.transform = [...realCollarQuad];
    if (kind === "reflection") more.transform = [400, 0, 0, 0, 0, 600, 400, 600];
    if (kind === "perspective") more.transform[4] += 10;
    if (kind === "non_affine") more.nonAffineTransform = more.transform.map((v, i) => v + (i === 0 ? 1 : 0));
    if (kind === "warp") more.warp.warpStyle._value = "warpArc";
    if (kind === "zero") more.transform = Array(8).fill(0);
    if (kind === "overflow") more.transform = more.transform.map(v => v * 1e300);
    await assert.rejects(f.executor.execute(f.prepared, f.boundary, async () => {}), /BRIDGE_PLAN_INVALID/);
    assert.equal(f.state.replacements, 0);
    assert.equal(f.state.saves.length, 0);
  });
}

test("fixed-layer side effects invalidate the page before any output", async () => {
  const f = fixture();
  f.state.afterTransform = () => { f.fixed.visible = false; };
  await assert.rejects(f.executor.execute(f.prepared, f.boundary, async () => {}), /BRIDGE_PLAN_INVALID/);
  assert.equal(f.state.saves.length, 0); assert.equal(f.state.closes, 1);
});

for (const initialVisible of [true, false]) {
  test(`only the calibrated superseded smart object is hidden (initially visible: ${initialVisible})`, async () => {
    const f = fixture(), old = { ...f.fixed, id: 259, kind: "smartObject", name: "old product", visible: initialVisible };
    f.doc.layers.unshift(old);
    f.plan.targets[0].superseded_layers = [{ layer_path: [3], layer_path_order: "psd_bottom_to_top",
      expected_kind: "smartObject", expected_visible: initialVisible, expected_clipping: false, expected_layer_id: 259 }];
    const baseline = { ...old }, fixedBefore = { ...f.fixed };
    if (!initialVisible) {
      await assert.rejects(f.executor.execute(f.prepared, f.boundary, async () => {}), /BRIDGE_PLAN_INVALID/);
      assert.equal(f.state.saves.length, 0);
      return;
    }
    await f.executor.execute(f.prepared, f.boundary, async () => {});
    assert.deepEqual(old, { ...baseline, visible: false });
    assert.deepEqual(f.fixed, fixedBefore);
    assert.equal(f.state.replacements, 1);
    assert.deepEqual(f.state.saves, ["psd", "jpg"]);
  });
}

for (const mutation of ["alias", "wrong_id", "wrong_kind", "stale_visibility", "hide_failed", "changed_content"]) {
  test(`superseded ${mutation} refuses publication`, async () => {
    const f = fixture(), old = { ...f.fixed, id: 259, kind: "smartObject", name: "old product" };
    f.doc.layers.unshift(old);
    const binding = { layer_path: [3], layer_path_order: "psd_bottom_to_top", expected_kind: "smartObject",
      expected_visible: true, expected_clipping: false, expected_layer_id: 259 };
    f.plan.targets[0].superseded_layers = [binding];
    if (mutation === "alias") binding.layer_path = [2];
    if (mutation === "wrong_id") binding.expected_layer_id = 123;
    if (mutation === "wrong_kind") old.kind = "pixel";
    if (mutation === "stale_visibility") old.visible = false;
    if (mutation === "hide_failed") Object.defineProperty(old, "visible", { get: () => true, set: () => {} });
    if (mutation === "changed_content") f.state.afterTransform = () => { old.name = "unexpected"; };
    await assert.rejects(f.executor.execute(f.prepared, f.boundary, async () => {}), /BRIDGE_PLAN_INVALID/);
    assert.equal(f.state.saves.length, 0);
    assert.equal(f.state.closes, 1);
  });
}

test("fractional placement uses a native transform and rejects host pixel rounding", async () => {
  for (const rounded of [false, true]) {
    const f = fixture(), p = f.plan.targets[0].layout.placement;
    Object.assign(p, { source_width: 4480, source_height: 6720, left: -22.333333, top: 0, width: 834.666667, height: 1252 });
    f.descriptor.smartObjectMore.size = { width: 4480, height: 6720 };
    f.target.scale = f.target.translate = async () => { throw new Error("DOM geometry must not be used"); };
    if (rounded) {
      f.state.afterTransform = () => { f.descriptor.smartObjectMore.transform = f.descriptor.smartObjectMore.transform.map(Math.round); };
      await assert.rejects(f.executor.execute(f.prepared, f.boundary, async () => {}), /BRIDGE_PLAN_INVALID/);
      assert.equal(f.state.saves.length, 0);
    } else {
      await f.executor.execute(f.prepared, f.boundary, async () => {});
      const actual = axisAlignedBox(f.descriptor);
      for (const key of ["left", "top", "width", "height"]) assert.ok(Math.abs(actual[key] - p[key]) < 0.000001);
    }
  }
});

test("a positional residue after combined scale and offset is corrected once from host geometry", async () => {
  const f = fixture(), p = f.plan.targets[0].layout.placement;
  Object.assign(p, { source_width: 4480, source_height: 6720, left: -22.333333, top: 0, width: 834.666667, height: 1252 });
  f.descriptor.smartObjectMore.size = { width: 4480, height: 6720 };
  let transforms = 0;
  f.state.afterTransform = () => {
    transforms++;
    if (transforms === 1) f.descriptor.smartObjectMore.transform = f.descriptor.smartObjectMore.transform.map((n, i) => n + (i % 2 ? 0.00457 : -0.145));
  };
  await f.executor.execute(f.prepared, f.boundary, async () => {});
  assert.equal(transforms, 2);
  const actual = axisAlignedBox(f.descriptor);
  for (const key of ["left", "top", "width", "height"]) assert.ok(Math.abs(actual[key] - p[key]) < 0.000001);
});

test("a scale residue is never hidden by the positional correction", async () => {
  const f = fixture(), p = f.plan.targets[0].layout.placement;
  let transforms = 0;
  f.state.afterTransform = () => {
    transforms++;
    if (transforms === 1) {
      const q = f.descriptor.smartObjectMore.transform;
      f.descriptor.smartObjectMore.transform = q.map((n, i) => i % 2 ? n : n * 1.001);
    }
  };
  await assert.rejects(f.executor.execute(f.prepared, f.boundary, async () => {}), /BRIDGE_PLAN_INVALID/);
  assert.equal(transforms, 1);
  assert.equal(f.state.saves.length, 0);
});

test("a host that stringifies modal errors cannot erase the original rejection or cancellation code", async () => {
  const f = fixture();
  f.ps.core.executeAsModal = async callback => {
    try { return await callback({ isCancelled: false }); }
    catch (error) { throw String(error); }
  };
  for (const code of ["BRIDGE_PLAN_INVALID", "BRIDGE_EXECUTION_CANCELLED"]) {
    const expected = new Error(code);
    await assert.rejects(f.executor.modalCall(async () => { throw expected; }, async () => {}), error => error === expected);
  }
  assert.equal(f.executor.modal, false);
});

test("modal entry pulses the authenticated execution boundary before Photoshop work", async () => {
  const f = fixture();
  const calls = [];
  await f.executor.modalCall(async () => { calls.push("callback"); }, async () => { calls.push("pulse"); });
  assert.deepEqual(calls, ["pulse", "callback"]);
  assert.equal(f.executor.modal, false);
});

test("a document visible before owned open resolves can keep the same execution alive without claiming ownership", async t => {
  const f = fixture();
  let interval, duringOpen;
  t.mock.method(globalThis, "setInterval", callback => { interval = callback; return 1; });
  t.mock.method(globalThis, "clearInterval", () => {});
  const claim = { claim_id: "claim_open", execution_id: "execution_open", plan_digest: "sha256:test", state: "running" };
  const loop = new AdapterWorkLoop({ session: {}, async request(path) {
    if (path === "/heartbeat") return { body: { ok: true } };
    assert.equal(path, "/adapter/v1/claims"); return { body: { has_work: true, claim } };
  } }, f.storage, randomBytes, () => f.executor.inspect());
  loop.prepared = { claim };
  const original = f.ps.app.open;
  f.ps.app.open = async function(entry) {
    const doc = await original.call(this, entry);
    assert.equal(f.executor.documentId, null);
    duringOpen = f.executor.inspect();
    interval();
    await new Promise(setImmediate);
    return doc;
  };
  const result = await f.executor.execute(f.prepared, () => loop.executionBoundary(), () => loop.executionBoundary(true));
  assert.equal(duringOpen.ownedDocument, false);
  assert.equal(duringOpen.ready, false);
  assert.equal(duringOpen.openingCopy, true);
  assert.equal(f.state.closes, 1);
  assert.equal(f.executor.inspect().openingCopy, false);
  await Promise.all([...result.outputs.values()].map(lease => lease.dispose()));
});

test("owned modal remains identified until the host releases executeAsModal", async t => {
  const f = fixture(); let interval;
  t.mock.method(globalThis, "setInterval", callback => { interval = callback; return 1; });
  t.mock.method(globalThis, "clearInterval", () => {});
  f.ps.core.executeAsModal = async callback => {
    f.state.modal = true;
    try {
      const value = await callback({ isCancelled: false });
      interval(); await new Promise(setImmediate);
      return value;
    } finally { f.state.modal = false; }
  };
  await f.executor.modalCall(async () => {}, async () => {
    assert.equal(f.executor.inspect().ownedModal, true);
  });
  assert.equal(f.executor.modal, false);
});

test("opening liveness never treats foreign, multiple or inactive documents as the controlled copy", () => {
  const changes = [f => { f.doc.name = "foreign.psd"; },
    f => { f.ps.app.documents.push({ id: 99, name: "user.psd" }); },
    f => { f.ps.app.activeDocument = { id: 99 }; },
    f => { f.executor.modal = false; }, f => { f.state.modal = false; },
    f => { f.executor.opening = false; }];
  for (const change of changes) {
    const f = fixture();
    f.executor.opening = true; f.executor.modal = true; f.executor.documentName = f.doc.name;
    f.state.modal = true; f.ps.app.documents = [f.doc]; f.ps.app.activeDocument = f.doc;
    assert.equal(f.executor.inspect().openingCopy, true);
    change(f);
    assert.equal(f.executor.inspect().openingCopy, false);
    assert.equal(f.executor.inspect().ownedDocument, false);
    assert.equal(f.state.closes, 0);
  }
});

test("native warpCustom requires every control point of a full-source identity grid", () => {
  const more = { size: { width: 6120, height: 8160 }, warp: { warpStyle: { _value: "warpCustom" },
    warpValue: 0, warpPerspective: 0, warpPerspectiveOther: 0, uOrder: 4, vOrder: 4,
    bounds: { left: 0, top: 0, right: 6120, bottom: 8160 }, customEnvelopeWarp: { meshPoints: {
      horizontal: { list: Array.from({ length: 16 }, (_, i) => (i % 4) * 2040) },
      vertical: { list: Array.from({ length: 16 }, (_, i) => Math.floor(i / 4) * 2720) }
    } } } };
  assert.equal(identityWarp(more), true);
  for (const change of [m => m.warp.customEnvelopeWarp.meshPoints.horizontal.list[5] += 1,
    m => m.warp.warpPerspective = 0.1, m => m.warp.uOrder = 5, m => m.warp.bounds.right -= 1,
    m => m.warp.customEnvelopeWarp.meshPoints.vertical.list.pop(),
    m => m.warp.customEnvelopeWarp.meshPoints.vertical.list[4] = NaN, m => delete m.warp]) {
    const invalid = structuredClone(more); change(invalid); assert.equal(identityWarp(invalid), false);
  }
});

test("dynamic pages and unknown capabilities stay closed until their explicit contracts are implemented", () => {
  const f = fixture(); f.plan.document.content_type = "dynamic_size_table";
  assert.throws(() => requireExecutable(f.plan), /BRIDGE_PLAN_INVALID/);
  f.plan.document.content_type = "ordinary_set"; f.plan.required_capabilities.push("unknown/capability");
  assert.throws(() => requireExecutable(f.plan), /BRIDGE_PLAN_INVALID/);
});
