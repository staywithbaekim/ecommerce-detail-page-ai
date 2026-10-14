"use strict";
const { encode } = require("./auth");
const { isHostModal } = require("./host");
const { withSubpixelTransform } = require("./transform-preference");
const { capability: instanceCapability, validInstanceIdentity } = require("./instance-identity");
const fail = (code = "BRIDGE_PLAN_INVALID") => { throw new Error(code); };
const numeric = value => typeof value === "number" ? value : value?._value ?? value?.value;
const normalized = value => String(value).replace(/[^a-z]/gi, "").toLowerCase();
function identityWarp(more) {
  const warp = more?.warp, style = warp?.warpStyle?._value;
  if (style === "warpNone") return true;
  // PS 26.8.1 stores an unchanged 4x4 envelope as warpCustom. Accept only a
  // complete identity grid covering the full source, never merely the style name.
  if (style !== "warpCustom" || warp.warpValue !== 0 || warp.warpPerspective !== 0
    || warp.warpPerspectiveOther !== 0 || warp.uOrder !== 4 || warp.vOrder !== 4) return false;
  const width = numeric(more.size?.width), height = numeric(more.size?.height), bounds = warp.bounds;
  if (![width, height].every(n => Number.isFinite(n) && n > 0) || numeric(bounds?.left) !== 0 || numeric(bounds?.top) !== 0
    || numeric(bounds?.right) !== width || numeric(bounds?.bottom) !== height) return false;
  const mesh = warp.customEnvelopeWarp?.meshPoints, x = mesh?.horizontal?.list, y = mesh?.vertical?.list;
  return Array.isArray(x) && Array.isArray(y) && x.length === 16 && y.length === 16
    && x.every((n, i) => Number.isFinite(n) && Math.abs(n - (i % 4) * width / 3) <= 0.001)
    && y.every((n, i) => Number.isFinite(n) && Math.abs(n - Math.floor(i / 4) * height / 3) <= 0.001);
}

function resolveLayer(document, path) {
  let layers = document.layers, layer;
  for (const segment of path) {
    if (!Number.isSafeInteger(segment) || segment < 1 || segment > layers.length) return fail();
    layer = layers[layers.length - segment];
    layers = layer.layers || [];
  }
  return layer || fail();
}

function smartObjectQuad(descriptor, placement = null) {
  const more = descriptor.smartObjectMore, q = more?.transform;
  if (!Array.isArray(q) || q.length !== 8 || !q.every(Number.isFinite)
    || descriptor.smartObject?.linked === true) return fail();
  if (more.nonAffineTransform && (!Array.isArray(more.nonAffineTransform)
    || more.nonAffineTransform.length !== 8 || more.nonAffineTransform.some((n, i) => !Number.isFinite(n) || Math.abs(n - q[i]) > 0.01))) return fail();
  if (!identityWarp(more)) return fail();
  if (placement && (numeric(more.size?.width) !== placement.source_width
    || numeric(more.size?.height) !== placement.source_height)) return fail();
  return q;
}

function axisAlignedBox(descriptor, placement = null) {
  const q = smartObjectQuad(descriptor, placement);
  if (Math.abs(q[1] - q[3]) > 0.01 || Math.abs(q[2] - q[4]) > 0.01
    || Math.abs(q[5] - q[7]) > 0.01 || Math.abs(q[0] - q[6]) > 0.01
    || q[2] <= q[0] || q[5] <= q[1]) return fail();
  return { left: q[0], top: q[1], width: q[2] - q[0], height: q[5] - q[1] };
}

// Ordinary plans explicitly specify the final upright whole-image rectangle.
// Accept an existing rigid rotation only; skew, reflection and perspective stay unsupported.
function ordinaryRotation(descriptor, placement = null) {
  const q = smartObjectQuad(descriptor, placement);
  const ux = q[2] - q[0], uy = q[3] - q[1], vx = q[6] - q[0], vy = q[7] - q[1];
  const width = Math.hypot(ux, uy), height = Math.hypot(vx, vy);
  if (!(width > 0 && height > 0) || !Number.isFinite(width * height)
    || ux * vy - uy * vx <= 0
    || Math.abs(ux * vx + uy * vy) / (width * height) > 1e-7
    || Math.abs(q[4] - q[0] - ux - vx) > 0.01
    || Math.abs(q[5] - q[1] - uy - vy) > 0.01) return fail();
  return Math.atan2(uy, ux) * 180 / Math.PI;
}

function requireExecutable(plan) {
  if (!validInstanceIdentity(plan)) return fail();
  const supported = ["psd-layer-path/psd-bottom-to-top", "smart-object/replace", "output/jpeg-srgb-icc", "layout/absolute-placement-v1", "output/verification-psd-v1", "size-table/calibrated-target-v1", "size-table/insert-layer-v1", "output/variant-identity-v1"];
  supported.push(instanceCapability);
  supported.push("image-layer/replace-v1");
  supported.push("image-layer/covered-stack-v1");
  const coveredRaster = plan.targets.some(target => target.expected_layer.kind === "pixel" && target.superseded_layers.length > 0);
  if (coveredRaster !== plan.required_capabilities.includes("image-layer/covered-stack-v1")) return fail();
  const raster = plan.targets.some(target => target.expected_layer.kind === "pixel");
  if (raster !== plan.required_capabilities.includes("image-layer/replace-v1")
    || raster && plan.document.content_type !== "ordinary_set") return fail();
  const size = plan.document.content_type === "dynamic_size_table";
  const insertion = plan.required_capabilities.includes("size-table/insert-layer-v1");
  if (insertion) {
    if (!size || !plan.size_table?.insertion || plan.targets.length !== 0
      || plan.required_capabilities.includes("size-table/calibrated-target-v1")) return fail();
  } else if (size ? !plan.size_table || !plan.required_capabilities.includes("size-table/calibrated-target-v1")
      || plan.targets.length !== 1 || plan.targets[0].asset_resource_id !== plan.size_table.render_resource_id
    : plan.document.content_type !== "ordinary_set" || plan.size_table !== null
      || plan.required_capabilities.includes("size-table/calibrated-target-v1")) return fail();
  if (!insertion && plan.targets.length === 0
    || !plan.required_capabilities.includes("layout/absolute-placement-v1")
    || plan.required_capabilities.some(capability => !supported.includes(capability))
    || plan.targets.some(target => !target.layout.placement)) return fail();
}

class PhotoshopPageExecutor {
  constructor(photoshop, storage, randomBytes) {
    this.ps = photoshop; this.storage = storage; this.randomBytes = randomBytes;
    this.documentId = null; this.documentName = null; this.modal = false;
    this.opening = false;
    this.stage = "idle";
    this.diagnostic = null;
    this.failure = null;
    this.supersededLayerIds = new Map();
    this.replacementLayerIds = new Map();
  }

  inspect() {
    const docs = Array.from(this.ps.app.documents);
    const owned = this.documentId !== null && docs.length === 1 && docs[0].id === this.documentId;
    const modal = isHostModal(this.ps.core);
    // An awaited open can expose its document before returning its identity.
    // This permits only authenticated liveness inside that owned modal, not
    // document ownership, resource acquisition, editing, or another execution.
    const openingCopy = this.opening && this.modal && modal && this.documentId === null
      && docs.length <= 1 && (docs.length === 0 || (docs[0].name === this.documentName
        && this.ps.app.activeDocument?.id === docs[0].id));
    return { ready: (docs.length === 0 || owned) && (!modal || this.modal), modal,
      ownedModal: modal && this.modal, ownedDocument: owned, openingCopy };
  }

  current() {
    const docs = Array.from(this.ps.app.documents);
    if (docs.length !== 1 || docs[0].id !== this.documentId || docs[0].name !== this.documentName
      || this.ps.app.activeDocument?.id !== this.documentId) return fail("BRIDGE_HOST_BLOCKED");
    return docs[0];
  }

  async modalCall(callback, pulse) {
    let pending = null, pulseError = null, callbackError = null;
    const timer = setInterval(() => {
      if (!pending) pending = Promise.resolve().then(pulse).catch(error => { pulseError = error; })
        .finally(() => { pending = null; });
    }, 4000);
    try {
      const result = await this.ps.core.executeAsModal(async context => {
        this.modal = true;
        // Publish and verify the owned modal state before touching the document. This
        // closes the race where a short Photoshop modal finishes before the periodic
        // pulse can report busy/running or observe a desktop cancellation request.
        try {
          await pulse();
          return await callback(context);
        }
        catch (error) { callbackError = error; throw error; }
      }, { commandName: "电商详情页受控渲染", timeOut: 1000 });
      this.modal = false;
      if (pending) await pending;
      if (pulseError) throw pulseError;
      return result;
    } catch (error) { throw callbackError || error; }
    finally { clearInterval(timer); if (pending) await pending; this.modal = false; }
  }

  async descriptor(layer) {
    const result = await this.ps.action.batchPlay([{ _obj: "get", _target: [{ _ref: "layer", _id: layer.id }],
      _options: { dialogOptions: "dontDisplay" } }], { synchronousExecution: true });
    if (!result[0] || result[0]._obj === "error") return fail();
    return result[0];
  }

  async snapshot(document) {
    const records = [];
    const visit = async (layers, prefix = [], visible = true) => {
      for (let i = layers.length - 1; i >= 0; i--) {
        const layer = layers[i], path = [...prefix, layers.length - i];
        this.stage = "snapshot_descriptor";
        const d = await this.descriptor(layer);
        this.stage = "snapshot_bounds";
        const bounds = layer.bounds;
        this.stage = "snapshot_properties";
        const coordinates = [bounds.left, bounds.top, bounds.right, bounds.bottom].map(numeric);
        this.diagnostic = { layer_id: layer.id, finite_bounds: coordinates.every(Number.isFinite),
          finite_opacity: Number.isFinite(numeric(layer.opacity)) };
        if (!coordinates.every(Number.isFinite) || !Number.isFinite(numeric(layer.opacity))) return fail();
        records.push({ path: path.join("/"), id: layer.id, name: layer.name, kind: normalized(layer.kind),
          visible: layer.visible, effective: visible && layer.visible, clipping: d.group === true,
          user_mask: d.hasUserMask === true, vector_mask: d.hasVectorMask === true,
          opacity: Math.round(numeric(layer.opacity) * 255 / 100), blend_mode: normalized(layer.blendMode),
          bounds: coordinates, descriptor: d });
        if (layer.layers?.length) await visit(layer.layers, path, visible && layer.visible);
      }
    };
    await visit(document.layers);
    return records;
  }

  async preflightKnownTargets(plan, document) {
    this.diagnostic = {};
    const effectiveVisible = path => {
      let layers = document.layers, visible = true, layer;
      for (const segment of path) {
        if (!Number.isSafeInteger(segment) || segment < 1 || segment > layers.length) return fail();
        layer = layers[layers.length - segment];
        visible = visible && layer.visible;
        layers = layer.layers || [];
      }
      return { layer, visible };
    };
    const inspectReference = async (reference, expectedKind, expectedName, expectedVisible, expectedClipping, expectedId) => {
      const resolved = effectiveVisible(reference.layer_path), layer = resolved.layer;
      const descriptor = await this.descriptor(layer);
      if (layer.id !== expectedId || normalized(layer.kind) !== normalized(expectedKind)
        || layer.name !== expectedName.replace(/\t/g, " ") || layer.visible !== expectedVisible
        || resolved.visible !== expectedVisible || (descriptor.group === true) !== expectedClipping) return fail();
      return { layer, descriptor };
    };
    const targetLayerIds = new Map();
    const supersededLayerIds = new Map();
    const insertion = plan.size_table?.insertion;
    if (insertion) {
      const names = [];
      const visitNames = layers => { for (const layer of layers) { names.push(layer.name); if (layer.layers?.length) visitNames(layer.layers); } };
      visitNames(document.layers);
      if (names.includes(insertion.layer_name)) return fail();
      for (const reference of [insertion.above_layer, ...insertion.hidden_layers])
        await inspectReference(reference, reference.kind, reference.name, true, false, reference.layer_id);
      const nextPath = [insertion.above_layer.layer_path[0] + 1];
      if (nextPath[0] <= document.layers.length) {
        const next = effectiveVisible(nextPath).layer;
        if ((await this.descriptor(next)).group === true) return fail();
      }
    }
    for (const target of plan.targets) {
      const expected = target.expected_layer;
      const { layer, descriptor } = await inspectReference(target, expected.kind, expected.name_before,
        expected.visible, expected.clipping, expected.layer_id);
      targetLayerIds.set(target.layer_path.join("/"), layer.id);
      if ((descriptor.hasUserMask === true) !== expected.user_mask
        || (descriptor.hasVectorMask === true) !== expected.vector_mask
        || Math.round(numeric(layer.opacity) * 255 / 100) !== expected.opacity
        || normalized(layer.blendMode) !== normalized(expected.blend_mode === "norm" ? "normal" : expected.blend_mode)
        || descriptor.smartObject?.linked === true) return fail();
      const more = descriptor.smartObjectMore;
      const raster = expected.kind === "pixel";
      if (raster ? expected.user_mask || expected.vector_mask || more || descriptor.smartObject
        : !more || !identityWarp(more) || typeof more.ID !== "string") return fail();
      // The preflight descriptor still describes the template placeholder. Its
      // embedded source dimensions are allowed to differ from the selected
      // replacement resource; the replacement is the point at which the plan's
      // source dimensions become authoritative. Keep the preflight structural
      // geometry check, then enforce source dimensions after replacement.
      if (raster) { /* Pixel geometry is prepared inside the owned replacement modal. */ }
      else if (plan.document.content_type === "ordinary_set") ordinaryRotation(descriptor);
      else axisAlignedBox(descriptor);
      for (const old of target.superseded_layers) {
        const oldId = old.expected_layer_id;
        const resolved = effectiveVisible(old.layer_path), oldLayer = resolved.layer;
        const oldDescriptor = await this.descriptor(oldLayer);
        const targetAtSamePath = targetLayerIds.get(old.layer_path.join("/"));
        const key = old.layer_path.join("/");
        if (supersededLayerIds.has(key) && supersededLayerIds.get(key) !== oldLayer.id) return fail();
        supersededLayerIds.set(key, oldLayer.id);
        if (normalized(oldLayer.kind) !== "smartobject"
          || oldDescriptor.smartObject?.linked === true || oldLayer.visible !== old.expected_visible
          || (oldDescriptor.group === true) !== old.expected_clipping
          || oldLayer.id === targetAtSamePath
          || oldId !== undefined && oldLayer.id !== oldId || !resolved.visible) return fail();
      }
    }
    this.supersededLayerIds = supersededLayerIds;
  }

  preflight(plan, records) {
    const seen = new Set();
    const insertion = plan.size_table?.insertion;
    if (insertion) {
      this.stage = "size_insertion_preflight";
      if (records.some(record => record.name === insertion.layer_name)) return fail();
      for (const reference of [insertion.above_layer, ...insertion.hidden_layers]) {
        const record = records.find(item => item.path === reference.layer_path.join("/"));
        this.diagnostic = { layer_id: reference.layer_id, target_present: !!record,
          id_matches: record?.id === reference.layer_id, kind_matches: record?.kind === reference.kind,
          observed_kind: record?.kind, name_matches: record?.name === reference.name,
          visible: record?.visible, effective: record?.effective, clipping: record?.clipping };
        if (!record || reference.layer_path.length !== 1 || record.id !== reference.layer_id
          || record.kind !== reference.kind || record.name !== reference.name || !record.visible || !record.effective
          || record.clipping) return fail();
      }
      const next = records.find(item => item.path === String(insertion.above_layer.layer_path[0] + 1));
      if (next?.clipping) return fail();
    }
    for (const target of plan.targets) {
      const record = records.find(item => item.path === target.layer_path.join("/")), expected = target.expected_layer;
      // PS 26.8.1 reads each PSD layer-name TAB as one SPACE in both DOM and
      // Action Manager. Keep the frozen PSD name exact; map only this observed
      // host representation here, with path/id and all structural checks intact.
      const hostName = expected.name_before.replace(/\t/g, " ");
      this.diagnostic = { target_present: !!record, id_matches: record?.id === expected.layer_id,
        name_matches: record?.name === hostName, kind_matches: record?.kind === "smartobject",
        name_evidence: { expected_length: expected.name_before.length, observed_length: record?.name?.length ?? null,
          descriptor_length: typeof record?.descriptor?.name === "string" ? record.descriptor.name.length : null,
          descriptor_matches: record?.descriptor?.name === expected.name_before,
          observed_matches_trimmed_expected: record?.name === expected.name_before.trim(),
          descriptor_matches_trimmed_expected: record?.descriptor?.name === expected.name_before.trim() },
        effectively_visible: record?.effective === true,
        mismatched_flags: ["visible", "clipping", "user_mask", "vector_mask", "opacity"].filter(key => record?.[key] !== expected[key]),
        blend_matches: record?.blend_mode === normalized(expected.blend_mode === "norm" ? "normal" : expected.blend_mode) };
      this.stage = "target_identity";
      if (!record || seen.has(record.id) || record.kind !== normalized(expected.kind) || !record.effective
        || record.id !== expected.layer_id || record.name !== hostName) return fail();
      this.stage = "target_flags";
      if (["visible", "clipping", "user_mask", "vector_mask", "opacity"].some(key => record[key] !== expected[key])) return fail();
      this.stage = "target_blend";
      if (record.blend_mode !== normalized(expected.blend_mode === "norm" ? "normal" : expected.blend_mode)) return fail();
      this.stage = "target_geometry";
      const more = record.descriptor.smartObjectMore;
      if (expected.kind === "pixel") {
        if (expected.user_mask || expected.vector_mask || more || record.descriptor.smartObject) return fail();
      } else {
        const numericQuad = value => Array.isArray(value) && value.length === 8 && value.every(Number.isFinite) ? value : null;
        const warpBounds = more?.warp?.bounds, mesh = more?.warp?.customEnvelopeWarp?.meshPoints;
        this.diagnostic.geometry = { more_present: !!more, transform: numericQuad(more?.transform),
          non_affine: numericQuad(more?.nonAffineTransform), linked: record.descriptor.smartObject?.linked === true,
          warp_absent: !more?.warp?.warpStyle?._value, warp_none: more?.warp?.warpStyle?._value === "warpNone",
          warp_style: /^warp[a-z]{0,30}$/i.test(more?.warp?.warpStyle?._value || "") ? more.warp.warpStyle._value : "unknown",
          warp_value: Number.isFinite(more?.warp?.warpValue) ? more.warp.warpValue : null,
          warp_perspective: Number.isFinite(more?.warp?.warpPerspective) ? more.warp.warpPerspective : null,
          warp_perspective_other: Number.isFinite(more?.warp?.warpPerspectiveOther) ? more.warp.warpPerspectiveOther : null,
          warp_order: [more?.warp?.uOrder, more?.warp?.vOrder].map(n => Number.isFinite(n) ? n : null),
          warp_bounds: [warpBounds?.left, warpBounds?.top, warpBounds?.right, warpBounds?.bottom].map(n => numeric(n) ?? null),
          source_size: [numeric(more?.size?.width) ?? null, numeric(more?.size?.height) ?? null],
          identity_warp: identityWarp(more),
          mesh: Array.isArray(mesh?.horizontal?.list) && Array.isArray(mesh?.vertical?.list)
            && mesh.horizontal.list.length === 16 && mesh.vertical.list.length === 16
            ? mesh.horizontal.list.map((n, i) => [Number.isFinite(n) ? n : null,
              Number.isFinite(mesh.vertical.list[i]) ? mesh.vertical.list[i] : null]) : null };
        if (plan.document.content_type === "ordinary_set") ordinaryRotation(record.descriptor);
        else axisAlignedBox(record.descriptor);
        this.stage = "target_embedded_identity";
        const placedId = record.descriptor.smartObjectMore?.ID;
        if (typeof placedId !== "string" || records.filter(item => item.descriptor.smartObjectMore?.ID === placedId).length !== 1) return fail();
      }
      seen.add(record.id);
      this.stage = "superseded_preflight";
      for (const old of target.superseded_layers) {
        const item = records.find(record => record.path === old.layer_path.join("/"));
        if (!item || seen.has(item.id) || item.kind !== "smartobject" || !item.effective || item.descriptor.smartObject?.linked === true
          || item.visible !== old.expected_visible || item.clipping !== old.expected_clipping
          || old.expected_layer_id !== undefined && item.id !== old.expected_layer_id) return fail();
        seen.add(item.id);
      }
    }
  }

  audit(plan, before, after) {
    this.stage = "structure_audit";
    this.diagnostic = { before_count: before.length, after_count: after.length };
    const insertion = plan.size_table?.insertion;
    if (before.length + (insertion ? 1 : 0) !== after.length) return fail();
    const addedSummary = insertion ? after.filter(item => !before.some(old => old.id === item.id))
      .map(item => ({ layer_id: item.id, path: item.path, kind: item.kind })) : [];
    const targets = new Set(plan.targets.map(item => item.layer_path.join("/")));
    const rasterTargets = new Set(plan.targets.filter(item => item.expected_layer.kind === "pixel")
      .map(item => item.layer_path.join("/")));
    const superseded = new Set(plan.targets.flatMap(item => item.superseded_layers.map(old => old.layer_path.join("/"))));
    for (let i = 0; i < before.length; i++) {
      const a = before[i], b = insertion ? after.find(item => item.id === a.id) : after[i], target = targets.has(a.path);
      this.diagnostic = { layer_id: a.id, target_present: !!b, added_layers: addedSummary };
      if (!b) return fail();
      const keys = ["path", "id", "kind", "clipping", "user_mask", "vector_mask", "opacity", "blend_mode"];
      if (rasterTargets.has(a.path)) {
        if (a.kind !== "pixel" || b.kind !== "smartobject"
          || b.id !== this.replacementLayerIds.get(a.path)) return fail();
        keys.splice(keys.indexOf("id"), 1);
        keys.splice(keys.indexOf("kind"), 1);
      }
      const hidden = insertion?.hidden_layers.some(item => a.path === item.layer_path.join("/"));
      const hiddenChild = insertion?.hidden_layers.some(item => a.path.startsWith(item.layer_path.join("/") + "/"));
      if (insertion) {
        const path = a.path.split("/").map(Number);
        if (path[0] > insertion.above_layer.layer_path[0]) path[0]++;
        this.diagnostic.path_matches = b.path === path.join("/");
        if (b.path !== path.join("/")) return fail();
        keys.splice(keys.indexOf("path"), 1);
      }
      if (superseded.has(a.path) || hidden) {
        this.diagnostic.hidden = { visible: b.visible, effective: b.effective };
        if (b.visible !== false || b.effective !== false) return fail();
      } else if (hiddenChild) { keys.push("visible"); if (b.effective !== false) return fail(); }
      else keys.push("visible", "effective");
      if (!target) keys.push("name");
      this.diagnostic.mismatched_flags = keys.filter(key => a[key] !== b[key]);
      this.diagnostic.bounds_before = a.bounds;
      this.diagnostic.bounds_after = b.bounds;
      this.diagnostic.child_ids_before = before.filter(item => item.path.startsWith(a.path + "/")).map(item => item.id);
      this.diagnostic.child_ids_after = after.filter(item => item.path.startsWith(b.path + "/")).map(item => item.id);
      this.diagnostic.smart_descriptor_matches = JSON.stringify(a.descriptor.smartObjectMore) === JSON.stringify(b.descriptor.smartObjectMore);
      // Group bounds are derived from their children. Replacing a declared
      // descendant can change that union without moving the group. Every
      // descendant still passes its own exact path/identity/flags/leaf-bounds
      // checks below; unrelated groups and leaves retain the bounds guard.
      const targetAncestor = a.kind === "group" && b.kind === "group"
        && [...targets].some(path => path.startsWith(a.path + "/"));
      if (keys.some(key => a[key] !== b[key]) || !target && (!targetAncestor && a.bounds.some((n, j) => Math.abs(n - b.bounds[j]) > 0.01)
        || JSON.stringify(a.descriptor.smartObjectMore) !== JSON.stringify(b.descriptor.smartObjectMore))) return fail();
    }
    if (insertion) {
      const added = after.filter(layer => !before.some(old => old.id === layer.id));
      if (added.length !== 1) return fail();
      const layer = added[0];
      this.diagnostic = { added_layer_id: layer.id, path_matches: layer.path === String(insertion.above_layer.layer_path[0] + 1),
        name_matches: layer.name === insertion.layer_name, kind: layer.kind, visible: layer.visible, effective: layer.effective,
        clipping: layer.clipping, user_mask: layer.user_mask, vector_mask: layer.vector_mask, opacity: layer.opacity,
        blend_mode: layer.blend_mode };
      if (layer.path !== String(insertion.above_layer.layer_path[0] + 1) || layer.name !== insertion.layer_name
        || layer.kind !== "smartobject" || !layer.visible || !layer.effective || layer.clipping || layer.user_mask
        || layer.vector_mask || layer.opacity !== 255 || layer.blend_mode !== "normal") return fail();
      const box = axisAlignedBox(layer.descriptor, insertion.placement);
      if (["left", "top", "width", "height"].some(key => Math.abs(box[key] - insertion.placement[key]) > 0.1)) return fail();
    }
  }

  async placeLayer(layer, p) {
    const box = axisAlignedBox(await this.descriptor(layer), p);
    const transformed = await withSubpixelTransform(this.ps, this.storage, () => this.ps.action.batchPlay([{ _obj: "transform",
      _target: [{ _ref: "layer", _id: layer.id }], freeTransformCenterState: { _enum: "quadCenterState", _value: "QCSAverage" },
      offset: { _obj: "offset", horizontal: { _unit: "pixelsUnit", _value: p.left + p.width / 2 - box.left - box.width / 2 },
        vertical: { _unit: "pixelsUnit", _value: p.top + p.height / 2 - box.top - box.height / 2 } },
      width: { _unit: "percentUnit", _value: p.width / box.width * 100 },
      height: { _unit: "percentUnit", _value: p.height / box.height * 100 },
      _options: { dialogOptions: "dontDisplay" } }], { synchronousExecution: true }));
    if (transformed.length !== 1 || transformed[0]?._obj === "error") return fail();
    const final = axisAlignedBox(await this.descriptor(layer), p);
    if (["left", "top", "width", "height"].some(key => Math.abs(final[key] - p[key]) > 0.1)) return fail();
  }

  async insertSizeTable(prepared, before) {
    const insertion = prepared.plan.size_table.insertion, doc = this.current();
    const anchor = resolveLayer(doc, insertion.above_layer.layer_path);
    const source = prepared.leases.get(prepared.plan.size_table.render_resource_id)?.getEntry();
    if (!source || anchor.id !== insertion.above_layer.layer_id) return fail();
    const token = this.storage.localFileSystem.createSessionToken(source);
    const result = await this.ps.action.batchPlay([{ _obj: "placeEvent", null: { _path: token, _kind: "local" },
      linked: false, _options: { dialogOptions: "dontDisplay" } }], { synchronousExecution: true });
    if (result.length !== 1 || result[0]?._obj === "error") return fail();
    const active = Array.from(doc.activeLayers);
    if (active.length !== 1 || before.some(item => item.id === active[0].id) || normalized(active[0].kind) !== "smartobject") return fail();
    const layer = active[0];
    this.stage = "size_table_move";
    const sourceIndex = (await this.descriptor(layer)).itemIndex;
    const anchorIndex = (await this.descriptor(anchor)).itemIndex;
    if (![sourceIndex, anchorIndex].every(Number.isSafeInteger) || sourceIndex < 1 || anchorIndex < 0
      || sourceIndex === anchorIndex) return fail();
    // PS 26.8.1 DOM moveAbove subtracts one unconditionally for a group anchor,
    // placing a layer that came from above inside that group. Native itemIndex
    // includes group end markers; only removal from below shifts the anchor.
    const destination = anchorIndex - (sourceIndex < anchorIndex ? 1 : 0);
    const moved = await this.ps.action.batchPlay([{ _obj: "move", _target: [{ _ref: "layer", _id: layer.id }],
      to: { _ref: "layer", _index: destination }, version: 5, adjustment: true,
      _options: { dialogOptions: "dontDisplay" } }], { synchronousExecution: true });
    if (moved.length !== 1 || moved[0]?._obj === "error") return fail();
    const expectedRoots = before.filter(item => !item.path.includes("/")).map(item => item.id);
    expectedRoots.splice(insertion.above_layer.layer_path[0], 0, layer.id);
    const actualRoots = Array.from(doc.layers).reverse().map(item => item.id);
    this.diagnostic = { source_index: sourceIndex, anchor_index: anchorIndex, destination_index: destination,
      expected_root_ids: expectedRoots, actual_root_ids: actualRoots };
    if (expectedRoots.length !== actualRoots.length || expectedRoots.some((id, i) => actualRoots[i] !== id)) return fail();
    layer.name = insertion.layer_name;
    layer.visible = true; layer.opacity = 100; layer.blendMode = this.ps.constants.BlendMode.NORMAL;
    await this.placeLayer(layer, insertion.placement);
    // Resolve old layers by the observed identity after insertion shifts root paths.
    for (const reference of insertion.hidden_layers) {
      const old = Array.from(doc.layers).find(item => item.id === reference.layer_id);
      if (!old || !old.visible || old.name !== reference.name) return fail();
      old.visible = false;
    }
  }

  async createOutput(kind) {
    const nonce = this.randomBytes(32), name = "output_" + encode(nonce) + (kind === "jpg" ? ".jpg" : ".psd");
    nonce.fill(0);
    const folder = await this.storage.localFileSystem.getTemporaryFolder();
    const entry = await folder.createFile(name, { overwrite: false });
    let released = false;
    return { getEntry() { if (released) return fail(); return entry; },
      async dispose() { if (!released) { released = true; await entry.delete(); } } };
  }

  async execute(prepared, boundary, pulse) {
    const plan = prepared.plan, outputs = new Map(), performance = prepared.performance;
    const measure = (name, callback, fields = {}) => performance
      ? performance.measureAsync(name, callback, fields) : callback();
    const measuredBoundary = () => measure("photoshop_boundary", boundary);
    const measuredPulse = () => measure("photoshop_pulse", pulse);
    this.failure = null;
    this.supersededLayerIds = new Map();
    this.replacementLayerIds = new Map();
    this.stage = "plan_preflight";
    requireExecutable(plan);
    if (this.documentId !== null || this.ps.app.documents.length || isHostModal(this.ps.core)) return fail("BRIDGE_HOST_BLOCKED");
    const entry = prepared.leases.get(plan.document.template_resource_id)?.getEntry();
    if (!entry) return fail("BRIDGE_RESOURCE_BINDING_INVALID");
    try {
      await measuredBoundary();
      this.documentName = entry.name;
      this.stage = "open_copy";
      await measure("photoshop_open", () => this.modalCall(async () => {
        this.opening = true;
        try {
          const opened = await this.ps.app.open(entry);
          // Capture only a document returned by opening the authenticated, application-owned copy.
          if (opened && Number.isSafeInteger(opened.id)) this.documentId = opened.id;
        } finally { this.opening = false; }
      }, measuredPulse));
      if (this.documentId === null) {
        const docs = Array.from(this.ps.app.documents);
        if (docs.length !== 1 || docs[0].name !== entry.name) return fail("BRIDGE_HOST_BLOCKED");
        this.documentId = docs[0].id;
      }
      let before;
      this.stage = "template_preflight";
      await measure("photoshop_preflight", () => this.modalCall(async () => {
        this.stage = "document_identity";
        const doc = this.current();
        this.stage = "canvas_dimensions";
        if (numeric(doc.width) !== plan.document.canvas.width || numeric(doc.height) !== plan.document.canvas.height) return fail();
        this.stage = "canvas_color_descriptor";
        // The supported 26.8.1 host throws a string from the DOM mode getter.
        // Read the same explicit document's native properties; never infer RGB from the filename.
        const color = await this.ps.action.batchPlay(["mode", "depth"].map(property => ({ _obj: "get",
          _target: [{ _property: property }, { _ref: "document", _id: doc.id }],
          _options: { dialogOptions: "dontDisplay" } })), { synchronousExecution: true });
        if (color.length !== 2 || color[0]?.mode?._value !== "RGBColor" || color[1]?.depth !== 8) return fail();
        this.stage = "target_preflight";
        if (plan.output.editable_psd.enabled) {
          before = await this.snapshot(doc);
          this.preflight(plan, before);
        } else {
          before = Array.from(doc.layers).reverse().map((layer, index) => ({ id: layer.id, path: String(index + 1) }));
          await this.preflightKnownTargets(plan, doc);
        }
      }, measuredPulse));
      if (plan.size_table?.insertion) {
        await measuredBoundary(); this.stage = "size_table_insert";
        await measure("photoshop_size_table_insert", () => this.modalCall(async context => {
          if (context.isCancelled) return fail("BRIDGE_HOST_BLOCKED");
          await this.insertSizeTable(prepared, before);
        }, measuredPulse));
      }
      if (plan.targets.length) {
        // Keep one authenticated boundary outside Photoshop's modal, then perform the
        // whole page's target work in one modal. This removes one boundary/modal pair
        // per target without weakening the per-target identity, geometry, or cleanup
        // checks below. Pulse between targets keeps cancellation and session loss
        // observable while the page remains inside the owned modal.
        this.stage = "replacement_boundary";
        await measuredBoundary();
        this.stage = "replacement_modal";
        await measure("photoshop_replace_batch", () => this.modalCall(async context => {
          for (let targetIndex = 0; targetIndex < plan.targets.length; targetIndex++) {
            if (targetIndex > 0) await measuredPulse();
            if (context.isCancelled) return fail("BRIDGE_HOST_BLOCKED");
            const target = plan.targets[targetIndex];
            await measure("photoshop_replace", async () => {
              this.stage = "replacement_target";
              let layer = resolveLayer(this.current(), target.layer_path);
              if (layer.id !== target.expected_layer.layer_id) return fail();
              if (target.expected_layer.kind === "pixel") {
                this.stage = "prepare_pixel_target";
                // Only the selected target in the application-owned document is
                // converted. No source PSD, user step or placeholder identity is changed.
                const converted = await this.ps.action.batchPlay([
                  { _obj: "select", _target: [{ _ref: "layer", _id: layer.id }], makeVisible: false,
                    _options: { dialogOptions: "dontDisplay" } },
                  { _obj: "newPlacedLayer", _options: { dialogOptions: "dontDisplay" } }
                ], { synchronousExecution: true });
                if (converted.length !== 2 || converted.some(reply => reply._obj === "error")) return fail();
                layer = resolveLayer(this.current(), target.layer_path);
                if (normalized(layer.kind) !== "smartobject") return fail();
                const convertedDescriptor = await this.descriptor(layer), expected = target.expected_layer;
                if ((convertedDescriptor.group === true) !== expected.clipping
                  || (convertedDescriptor.hasUserMask === true) !== expected.user_mask
                  || (convertedDescriptor.hasVectorMask === true) !== expected.vector_mask
                  || Math.round(numeric(layer.opacity) * 255 / 100) !== expected.opacity
                  || normalized(layer.blendMode) !== normalized(expected.blend_mode === "norm" ? "normal" : expected.blend_mode)) return fail();
              }
              this.replacementLayerIds.set(target.layer_path.join("/"), layer.id);
              this.stage = "replacement_resource";
              const source = prepared.leases.get(target.asset_resource_id)?.getEntry();
              if (!source) return fail("BRIDGE_RESOURCE_BINDING_INVALID");
              this.stage = "replacement_token";
              const token = this.storage.localFileSystem.createSessionToken(source);
              this.stage = "replace_contents";
              const replies = await this.ps.action.batchPlay([
                { _obj: "select", _target: [{ _ref: "layer", _id: layer.id }], makeVisible: false, _options: { dialogOptions: "dontDisplay" } },
                { _obj: "placedLayerReplaceContents", _target: [{ _ref: "layer", _enum: "ordinal", _value: "targetEnum" }],
                  null: { _path: token, _kind: "local" }, _options: { dialogOptions: "dontDisplay" } }
              ], { synchronousExecution: true });
              if (replies.length !== 2 || replies.some(reply => reply._obj === "error")) return fail();
              layer = resolveLayer(this.current(), target.layer_path);
              if (layer.id !== this.replacementLayerIds.get(target.layer_path.join("/"))) return fail();
              const p = target.layout.placement;
              if (plan.document.content_type === "ordinary_set") {
                const angle = ordinaryRotation(await this.descriptor(layer), p);
                if (Math.abs(angle) > 1e-7) {
                  this.stage = "placement_unrotate";
                  const rotated = await withSubpixelTransform(this.ps, this.storage, () => this.ps.action.batchPlay([{ _obj: "transform",
                    _target: [{ _ref: "layer", _id: layer.id }],
                    freeTransformCenterState: { _enum: "quadCenterState", _value: "QCSAverage" },
                    angle: { _unit: "angleUnit", _value: -angle },
                    _options: { dialogOptions: "dontDisplay" } }], { synchronousExecution: true }));
                  if (rotated.length !== 1 || rotated[0]?._obj === "error") return fail();
                }
              }
              const box = axisAlignedBox(await this.descriptor(layer), p);
              this.stage = "placement_transform";
              this.diagnostic.placement = { before: box, requested: { left: p.left, top: p.top, width: p.width, height: p.height } };
              const sx = p.width / box.width, sy = p.height / box.height;
              // ReplaceContents can retain the template's existing unequal axis scales.
              // Correct each axis to the explicitly validated, source-proportional final rectangle.
              // The supported host snaps transforms to pixels when the user's preference
              // is enabled. Preserve and restore that setting around this one operation.
              const transformed = await withSubpixelTransform(this.ps, this.storage, () => this.ps.action.batchPlay([{ _obj: "transform",
                _target: [{ _ref: "layer", _id: layer.id }],
                freeTransformCenterState: { _enum: "quadCenterState", _value: "QCSAverage" },
                offset: { _obj: "offset",
                  horizontal: { _unit: "pixelsUnit", _value: p.left + p.width / 2 - box.left - box.width / 2 },
                  vertical: { _unit: "pixelsUnit", _value: p.top + p.height / 2 - box.top - box.height / 2 } },
                width: { _unit: "percentUnit", _value: sx * 100 },
                height: { _unit: "percentUnit", _value: sy * 100 },
                _options: { dialogOptions: "dontDisplay" } }], { synchronousExecution: true }));
              if (transformed.length !== 1 || transformed[0]?._obj === "error") return fail();
              this.stage = "placement_verify";
              let final = axisAlignedBox(await this.descriptor(layer), p);
              this.diagnostic.placement.final = final;
              const dimensionsOutsideTolerance = ["width", "height"]
                .some(key => Math.abs(final[key] - p[key]) > 0.1);
              if (dimensionsOutsideTolerance) return fail();
              // Photoshop 26.8.1 can retain the requested scale but leave a
              // subpixel translation residue when scale and offset are sent in
              // the same transform descriptor. Do not widen the geometry gate:
              // if only the position is outside tolerance, correct that residue
              // once from the host-read geometry, then run the same strict final
              // verification. A second correction is intentionally not retried.
              if (["left", "top"].some(key => Math.abs(final[key] - p[key]) > 0.1)) {
                this.stage = "placement_correction";
                const corrected = await withSubpixelTransform(this.ps, this.storage, () => this.ps.action.batchPlay([{ _obj: "transform",
                  _target: [{ _ref: "layer", _id: layer.id }],
                  freeTransformCenterState: { _enum: "quadCenterState", _value: "QCSAverage" },
                  offset: { _obj: "offset",
                    horizontal: { _unit: "pixelsUnit", _value: p.left + p.width / 2 - final.left - final.width / 2 },
                    vertical: { _unit: "pixelsUnit", _value: p.top + p.height / 2 - final.top - final.height / 2 } },
                  _options: { dialogOptions: "dontDisplay" } }], { synchronousExecution: true }));
                if (corrected.length !== 1 || corrected[0]?._obj === "error") return fail();
                final = axisAlignedBox(await this.descriptor(layer), p);
                this.diagnostic.placement.correction = { after: final };
              }
              this.stage = "placement_verify";
              this.diagnostic.placement.final = final;
              if (["left", "top", "width", "height"].some(key => Math.abs(final[key] - p[key]) > 0.1)) return fail();
              this.stage = "superseded_hide";
              for (const old of target.superseded_layers) {
                const superseded = resolveLayer(this.current(), old.layer_path);
                if (plan.output.editable_psd.enabled && superseded.id !== before.find(item => item.path === old.layer_path.join("/"))?.id
                  || old.expected_layer_id !== undefined && superseded.id !== old.expected_layer_id
                  || !plan.output.editable_psd.enabled && (superseded.id !== this.supersededLayerIds.get(old.layer_path.join("/"))
                    || superseded.id === layer.id)
                  || normalized(superseded.kind) !== "smartobject"
                  || superseded.visible !== old.expected_visible) return fail();
                superseded.visible = false;
                if (superseded.visible !== false) return fail();
              }
            }, { index: targetIndex });
          }
        }, measuredPulse), { count: plan.targets.length });
      }
      if (plan.output.editable_psd.enabled) {
        await measuredBoundary();
        this.stage = "structure_audit";
        await measure("photoshop_structure_audit", () => this.modalCall(
          async () => this.audit(plan, before, await this.snapshot(this.current())), measuredPulse));
        const psdKind = "editable_psd";
        this.stage = "save_psd";
        await measure("photoshop_save_psd", async () => {
          outputs.set(psdKind, await this.createOutput(psdKind));
          await this.modalCall(async () => this.current().saveAs.psd(outputs.get(psdKind).getEntry(),
            { embedColorProfile: true }, true), measuredPulse);
        }, { output_kind: psdKind });
        await measuredBoundary();
      }
      this.stage = "save_jpg";
      await measure("photoshop_save_jpg", async () => {
        outputs.set("jpg", await this.createOutput("jpg"));
        await this.modalCall(async () => {
          const doc = this.current(), jpg = plan.output.jpg;
          const intents = { relative_colorimetric: "RELATIVECOLORIMETRIC", perceptual: "PERCEPTUAL",
            saturation: "SATURATION", absolute_colorimetric: "ABSOLUTECOLORIMETRIC" };
          await doc.convertProfile(jpg.color_profile, this.ps.constants.Intent[intents[jpg.rendering_intent]], jpg.black_point_compensation, jpg.dither);
          if (doc.colorProfileName !== jpg.color_profile) return fail();
          await doc.saveAs.jpg(outputs.get("jpg").getEntry(), { quality: 12, embedColorProfile: true }, true);
        }, measuredPulse);
      }, { output_kind: "jpg" });
      await measuredBoundary();
      await measure("photoshop_close", () => this.close(measuredPulse));
      this.stage = "output_ready";
      return { outputs, evidence: { plan_digest: plan.plan_digest, output_executed: true, target_count: plan.targets.length,
        fixed_structure_unchanged: plan.output.editable_psd.enabled, canvas: plan.document.canvas, profile: plan.output.jpg.color_profile } };
    } catch (error) {
      let closed = false;
      // A failed authenticated pulse cannot authorize its own cleanup. Enter a
      // fresh local modal and let current() prove the document is still ours.
      try {
        await measure("photoshop_cleanup_close", () => this.close(async () => {}));
        closed = this.documentId === null;
      }
      catch { /* Never close a document whose identity is uncertain. */ }
      const cleanup = await Promise.allSettled([...outputs.values()].map(output => output.dispose()));
      if (error?.message === "BRIDGE_PLAN_INVALID" && closed && this.inspect().ready
        && cleanup.every(result => result.status === "fulfilled"))
        this.failure = { code: "BRIDGE_PLAN_INVALID", cleanupConfirmed: true };
      throw error;
    }
  }

  async close(pulse) {
    if (this.documentId === null) return;
    await this.modalCall(async () => this.current().closeWithoutSaving(), pulse);
    if (Array.from(this.ps.app.documents).some(doc => doc.id === this.documentId)) return fail("BRIDGE_HOST_BLOCKED");
    this.documentId = null; this.documentName = null;
  }
}

module.exports = { PhotoshopPageExecutor, resolveLayer, axisAlignedBox, ordinaryRotation, requireExecutable, identityWarp };
