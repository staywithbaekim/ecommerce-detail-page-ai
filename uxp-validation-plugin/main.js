const { app, action, core, constants } = require("photoshop");
const { storage } = require("uxp");
const { SRGB_PROFILE_SHA256, inspectJpegIcc, ensureJpegHasSrgbProfile } = require("./jpeg-icc");

const localFileSystem = storage.localFileSystem;
const BRIDGE_URL = "http://127.0.0.1:17842";
const PLUGIN_VERSION = "0.4.7";
const PSD_LAYER_PATH_ORDER = "psd_bottom_to_top";
const SRGB_PROFILE = "sRGB IEC61966-2.1";
const SRGB_INTENT = "relative_colorimetric";
const VALIDATION_SPECS = {
  single_frame_copy: {
    pageId: "G-详情-03",
    templateName: "G-详情-03-validation-copy.psd",
    expectedCanvas: [790, 1252],
    frameCount: 1,
    frames: {
      "G-详情-03.frame-2": {
        layerPath: "2",
        auditName: "back_main",
        sourceName: "7Y9A3760.jpg",
        registeredFrame: [0, 0, 790, 1252],
        expectedClipping: false
      }
    },
    outputNames: {
      psd: "G-详情-03-validation-output.psd",
      jpg: "G-详情-03-validation-output.jpg",
      evidence: "G-详情-03-validation-evidence.json"
    }
  },
  multi_frame_copy: {
    pageId: "G-详情-05-细节",
    templateName: "G-详情-05-validation-copy.psd",
    expectedCanvas: [790, 1115],
    frameCount: 4,
    frames: {
      "G-详情-05.frame-3": {
        layerPath: "3",
        auditName: "care_label",
        sourceName: "7Y9A3762.jpg",
        registeredFrame: [406, 698, 766, 985],
        windowLayerPath: "1",
        supersededPaths: ["2"]
      },
      "G-详情-05.frame-7": {
        layerPath: "7",
        auditName: "collar",
        sourceName: "7Y9A3751.jpg",
        registeredFrame: [23, 693, 387, 980],
        windowLayerPath: "5",
        supersededPaths: ["6"]
      },
      "G-详情-05.frame-11": {
        layerPath: "11",
        auditName: "\tsleeve",
        sourceName: "7Y9A3755.jpg",
        registeredFrame: [443, 276, 765, 510],
        windowLayerPath: "9",
        supersededPaths: ["10"]
      },
      "G-详情-05.frame-15": {
        layerPath: "15",
        auditName: "\tfront",
        sourceName: "7Y9A3750.jpg",
        registeredFrame: [23, 188, 422, 672],
        windowLayerPath: "13",
        supersededPaths: ["14"]
      }
    },
    outputNames: {
      psd: "G-详情-05-validation-output.psd",
      jpg: "G-详情-05-validation-output.jpg",
      evidence: "G-详情-05-validation-evidence.json"
    }
  },
  whole_product_copy: {
    pageCount: 6,
    frameCount: 11,
    evidenceName: "A馨妈6015-58-whole-product-validation-evidence.json",
    pages: {
      "G-详情-01": {
        templateName: "G-详情-01-validation-copy.psd",
        templateSha256: "164e4af1cf934f6a6d1543d912b792bbdef7caf6dd42520b157900d060eb62f4",
        expectedCanvas: [790, 1327],
        outputName: "G-详情-01-validation-output.jpg",
        frames: {
          "G-详情-01.frame-3": {
            layerPath: "3",
            auditName: "front_main",
            sourceName: "7Y9A3750.jpg",
            sourceSha256: "c04dddd4c7802277d9996f347ba6fd4285987147941eed5a69638e0443696d57",
            registeredFrame: [0, 0, 790, 1327],
            expectedClipping: false,
            windowLayerPath: "1",
            supersededLayers: [{ layerPath: "2", expectedClipping: false }]
          }
        }
      },
      "G-详情-03": {
        templateName: "G-详情-03-validation-copy.psd",
        templateSha256: "b94e7458a33f645e322f30efde675ccb268be5a72ca2ff4b7618f241eed14a35",
        expectedCanvas: [790, 1252],
        outputName: "G-详情-03-validation-output.jpg",
        frames: {
          "G-详情-03.frame-2": {
            layerPath: "2",
            auditName: "back_main",
            sourceName: "7Y9A3760.jpg",
            sourceSha256: "04720404a97fbe6dfd6eb138946c129545f37020f81346d98aa0616668c0b0db",
            registeredFrame: [0, 0, 790, 1252],
            expectedClipping: false,
            supersededLayers: []
          }
        }
      },
      "G-详情-04-面料": {
        templateName: "G-详情-04-面料-validation-copy.psd",
        templateSha256: "789d4dfc56c5a8a64662ea96ac24ff5612fe612c7c29b57fe681b5c7cc7addcb",
        expectedCanvas: [790, 1182],
        outputName: "G-详情-04-面料-validation-output.jpg",
        frames: {
          "G-详情-04.frame-4": {
            layerPath: "4",
            auditName: "\tfabric_detail",
            sourceName: "7Y9A3758.jpg",
            sourceSha256: "ebc7eafa71cdfd97bb03de773cd0548d5d720da4460bf2a0c1db19736d89d91d",
            registeredFrame: [0, 192, 790, 1165],
            windowLayerPath: "1",
            supersededLayers: [{ layerPath: "3", expectedClipping: true }]
          }
        }
      },
      "G-详情-05-细节": {
        templateName: "G-详情-05-细节-validation-copy.psd",
        templateSha256: "69bf44a64e47cb8d6817fa8589888163b14ab08a19a0bcfcee02352c3392f5ef",
        expectedCanvas: [790, 1115],
        outputName: "G-详情-05-细节-validation-output.jpg",
        frames: {
          "G-详情-05.frame-3": {
            layerPath: "3",
            auditName: "care_label",
            sourceName: "7Y9A3762.jpg",
            sourceSha256: "b7161b1a39447258c39464706cced111b0736189cfc20c608cb1fae5353b3de4",
            registeredFrame: [406, 698, 766, 985],
            windowLayerPath: "1",
            supersededLayers: [{ layerPath: "2", expectedClipping: true }]
          },
          "G-详情-05.frame-7": {
            layerPath: "7",
            auditName: "collar",
            sourceName: "7Y9A3751.jpg",
            sourceSha256: "f2862b0162aa2fb6bf969c88e9df9f0ec4839e5565b0a45ccf4af8b3721e0df3",
            registeredFrame: [23, 693, 387, 980],
            windowLayerPath: "5",
            supersededLayers: [{ layerPath: "6", expectedClipping: true }]
          },
          "G-详情-05.frame-11": {
            layerPath: "11",
            auditName: "\tsleeve",
            sourceName: "7Y9A3755.jpg",
            sourceSha256: "868e85930c3996e2565490a89e80154cc6ef26edabf58044e635b475779079ca",
            registeredFrame: [443, 276, 765, 510],
            windowLayerPath: "9",
            supersededLayers: [{ layerPath: "10", expectedClipping: true }]
          },
          "G-详情-05.frame-15": {
            layerPath: "15",
            auditName: "\tfront",
            sourceName: "7Y9A3750.jpg",
            sourceSha256: "c04dddd4c7802277d9996f347ba6fd4285987147941eed5a69638e0443696d57",
            registeredFrame: [23, 188, 422, 672],
            windowLayerPath: "13",
            supersededLayers: [{ layerPath: "14", expectedClipping: true }]
          }
        }
      },
      "H-模特图-01": {
        templateName: "H-模特图-01-validation-copy.psd",
        templateSha256: "764c95e05f2229fd7ee4715411270c870446e3bdd7ebcbb0b602814ea536d5e7",
        expectedCanvas: [790, 2425],
        outputName: "H-模特图-01-validation-output.jpg",
        frames: {
          "H-模特图-01.frame-4": {
            layerPath: "4",
            auditName: "20250727_174936",
            sourceName: "7Y9A3760.jpg",
            sourceSha256: "04720404a97fbe6dfd6eb138946c129545f37020f81346d98aa0616668c0b0db",
            registeredFrame: [15, 1258, 780, 2282],
            windowLayerPath: "1",
            supersededLayers: [
              { layerPath: "2", expectedClipping: true },
              { layerPath: "3", expectedClipping: true }
            ]
          },
          "H-模特图-01.frame-9": {
            layerPath: "9",
            auditName: "20250727_175352",
            sourceName: "7Y9A3750.jpg",
            sourceSha256: "c04dddd4c7802277d9996f347ba6fd4285987147941eed5a69638e0443696d57",
            registeredFrame: [0, 101, 790, 1214],
            windowLayerPath: "6",
            supersededLayers: [
              { layerPath: "7", expectedClipping: true },
              { layerPath: "8", expectedClipping: true }
            ]
          }
        }
      },
      "H-模特图-02": {
        templateName: "H-模特图-02-validation-copy.psd",
        templateSha256: "ca5e7a9aa2fcff67b431c1e39a79790d212f38f65e5823ebbfe7745792833599",
        expectedCanvas: [790, 2546],
        outputName: "H-模特图-02-validation-output.jpg",
        frames: {
          "H-模特图-02.frame-4": {
            layerPath: "4",
            auditName: "back",
            sourceName: "7Y9A3760.jpg",
            sourceSha256: "04720404a97fbe6dfd6eb138946c129545f37020f81346d98aa0616668c0b0db",
            registeredFrame: [0, 1339, 790, 2519],
            windowLayerPath: "1",
            supersededLayers: [
              { layerPath: "2", expectedClipping: true },
              { layerPath: "3", expectedClipping: true }
            ]
          },
          "H-模特图-02.frame-9": {
            layerPath: "9",
            auditName: "front",
            sourceName: "7Y9A3750.jpg",
            sourceSha256: "c04dddd4c7802277d9996f347ba6fd4285987147941eed5a69638e0443696d57",
            registeredFrame: [24, 32, 772, 1315],
            windowLayerPath: "6",
            supersededLayers: [
              { layerPath: "7", expectedClipping: true },
              { layerPath: "8", expectedClipping: true }
            ]
          }
        }
      }
    }
  }
};

let loadedPlan = null;
let lastControlledFailureEvidence = null;

const statusNode = document.getElementById("status");
const outputNode = document.getElementById("output");
const executeButton = document.getElementById("execute-plan");

class ValidationError extends Error {
  constructor(code, message, details = {}) {
    super(message);
    this.name = "ValidationError";
    this.code = code;
    this.details = details;
  }
}

function showStatus(message, result) {
  statusNode.textContent = message;
  outputNode.textContent = JSON.stringify(result, null, 2);
}

function structuredError(code, message, details = {}) {
  return { ok: false, error: { code, message, details } };
}

function errorResult(error, details = {}) {
  return structuredError(error.code || "UNEXPECTED_ERROR", error.message || String(error), {
    ...(error.details || {}),
    ...details
  });
}

function serializeModalValidationFailure(error) {
  return { modal_validation_failure: errorResult(error).error };
}

function restoreModalValidationFailure(result) {
  if (!result || !result.modal_validation_failure) return result;
  const failure = result.modal_validation_failure;
  throw new ValidationError(failure.code, failure.message, failure.details || {});
}

function attachModalBoundaryError(result, boundaryError) {
  if (!result || !result.modal_validation_failure || !boundaryError) return result;
  const failure = result.modal_validation_failure;
  return {
    modal_validation_failure: {
      ...failure,
      details: {
        ...(failure.details || {}),
        modal_boundary_error: errorResult(boundaryError).error
      }
    }
  };
}

function assert(condition, code, message, details = {}) {
  if (!condition) throw new ValidationError(code, message, details);
}

function asNumber(value) {
  if (typeof value === "number") return value;
  if (value && typeof value._value === "number") return value._value;
  if (value && typeof value.value === "number") return value.value;
  const numeric = Number(value);
  if (!Number.isFinite(numeric)) throw new ValidationError("INVALID_GEOMETRY", "Photoshop returned a non-numeric geometry value.");
  return numeric;
}

function clamp(value, minimum, maximum) {
  return Math.max(minimum, Math.min(maximum, value));
}

function layerBounds(layer) {
  const bounds = layer.bounds;
  return {
    left: asNumber(bounds.left),
    top: asNumber(bounds.top),
    right: asNumber(bounds.right),
    bottom: asNumber(bounds.bottom)
  };
}

function normalizeWindowsPath(value) {
  return String(value || "").replace(/\//g, "\\").replace(/\\+$/, "").toLowerCase();
}

function parentPath(value) {
  const normalized = normalizeWindowsPath(value);
  const separator = normalized.lastIndexOf("\\");
  return separator < 0 ? "" : normalized.slice(0, separator);
}

function assertRenderPlan(plan) {
  assert(plan && plan.schema === "render-plan/v1", "INVALID_RENDER_PLAN", "RenderPlan schema must be render-plan/v1.");
  assert(plan.job_id, "INVALID_RENDER_PLAN", "RenderPlan requires job_id.");
  const validationMode = plan.validation && plan.validation.mode;
  const spec = VALIDATION_SPECS[validationMode];
  assert(spec, "INVALID_RENDER_PLAN", "This probe only accepts a controlled single_frame_copy, multi_frame_copy, or whole_product_copy validation plan.", {
    validation_mode: validationMode || null
  });
  assert(plan.input_policy && plan.input_policy.source_files_read_only === true, "INVALID_RENDER_PLAN", "Source files must be read-only.");
  assert(plan.input_policy.template_copies_only === true, "INVALID_RENDER_PLAN", "The plan must require template copies.");
  assert(plan.input_policy.allow_generated_imagery === false, "INVALID_RENDER_PLAN", "Generated imagery must be disabled.");
  if (validationMode === "whole_product_copy") return assertWholeProductRenderPlan(plan, spec);
  assert(Array.isArray(plan.pages) && plan.pages.length === 1, "INVALID_RENDER_PLAN", "The controlled validation probe requires exactly one page.");

  const page = plan.pages[0];
  assert(page.page_id === spec.pageId, "INVALID_RENDER_PLAN", `Validation mode ${validationMode} only accepts page ${spec.pageId}.`);
  assert(typeof page.template_copy === "string" && page.template_copy.endsWith(spec.templateName), "INVALID_RENDER_PLAN", `Template copy must end with ${spec.templateName}.`);
  assert(typeof page.template_sha256 === "string" && page.template_sha256.length === 64, "INVALID_RENDER_PLAN", "Template copy requires a SHA-256 audit value.");
  assert(Array.isArray(page.expected_canvas) && arraysEqual(page.expected_canvas, spec.expectedCanvas), "INVALID_RENDER_PLAN", "The page canvas does not match the approved fixture.", {
    expected_canvas: spec.expectedCanvas,
    actual_canvas: page.expected_canvas || null
  });
  assert(Array.isArray(page.frames) && page.frames.length === spec.frameCount, "INVALID_RENDER_PLAN", `Validation mode ${validationMode} requires exactly ${spec.frameCount} registered frame(s).`);

  const seenFrameIds = new Set();
  const seenLayerPaths = new Set();
  const seenSourceNames = new Set();
  for (const frame of page.frames) {
    const expectedFrame = spec.frames[frame.frame_id];
    assert(expectedFrame, "INVALID_RENDER_PLAN", `Unexpected frame id: ${frame.frame_id || "(missing)"}.`);
    assert(!seenFrameIds.has(frame.frame_id), "INVALID_RENDER_PLAN", "RenderPlan contains a duplicate frame id.", { frame_id: frame.frame_id });
    seenFrameIds.add(frame.frame_id);
    assert(frame.target && frame.target.layer_path === expectedFrame.layerPath, "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} has an unexpected calibrated layer path.`);
    assert(!seenLayerPaths.has(frame.target.layer_path), "INVALID_RENDER_PLAN", "RenderPlan targets the same calibrated layer more than once.", { layer_path: frame.target.layer_path });
    seenLayerPaths.add(frame.target.layer_path);
    assert(frame.target.layer_path_order === PSD_LAYER_PATH_ORDER, "INVALID_RENDER_PLAN", `Layer path order must be ${PSD_LAYER_PATH_ORDER}.`);
    assert(frame.target.expected_kind === "smartObject", "INVALID_RENDER_PLAN", "Each calibrated target must be a smart object.");
    assert(frame.target.expected_visible === true, "INVALID_RENDER_PLAN", "Each calibrated target must require a visible layer.");
    assert(frame.target.expected_clipping === (expectedFrame.expectedClipping !== false), "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} has an unexpected clipping expectation.`);
    assert(frame.target.audit_name === expectedFrame.auditName, "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} has an unexpected audit name.`);
    assert(Array.isArray(frame.target.registered_frame) && arraysEqual(frame.target.registered_frame, expectedFrame.registeredFrame), "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} has an unexpected registered frame.`);
    assert(Array.isArray(frame.target.binding_evidence) && ["layer_path", "clipping_structure", "visible_area"].every((item) => frame.target.binding_evidence.includes(item)), "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} is missing required structural binding evidence.`);

    if (validationMode === "multi_frame_copy") {
      assert(frame.target.window && frame.target.window.layer_path === expectedFrame.windowLayerPath, "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} has an unexpected window-layer path.`);
      assert(frame.target.window.layer_path_order === PSD_LAYER_PATH_ORDER, "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} window path order is unsupported.`);
      assert(frame.target.window.expected_visible === true && frame.target.window.expected_clipping === false, "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} window structure is incomplete.`);
      assert(Array.isArray(frame.target.superseded_paths) && arraysEqual(frame.target.superseded_paths, expectedFrame.supersededPaths), "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} has unexpected superseded replacement-stack paths.`);
    }

    assert(frame.source && typeof frame.source.relative_path === "string" && frame.source.relative_path.endsWith(expectedFrame.sourceName), "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} source path must end with ${expectedFrame.sourceName}.`);
    assert(!seenSourceNames.has(expectedFrame.sourceName), "INVALID_RENDER_PLAN", "This fixture requires four distinct approved source files.", { source_name: expectedFrame.sourceName });
    seenSourceNames.add(expectedFrame.sourceName);
    assert(typeof frame.source.sha256 === "string" && frame.source.sha256.length === 64, "INVALID_RENDER_PLAN", "Each source image requires a SHA-256 audit value.");
    assert(frame.fit === "cover", "INVALID_RENDER_PLAN", "The mutation probe only accepts cover fitting.");
    assert(frame.crop && Array.isArray(frame.crop.focal_point) && frame.crop.focal_point.length === 2 && frame.crop.focal_point.every(Number.isFinite), "INVALID_RENDER_PLAN", "Each frame requires a numeric focal point.");
    assert(frame.crop.focal_point.every((value) => value >= 0 && value <= 1), "INVALID_RENDER_PLAN", "Focal-point coordinates must be between 0 and 1.");
    assert(frame.crop.overflow_policy === "clip_to_registered_frame", "INVALID_RENDER_PLAN", "Each frame must clip overflow to its registered frame.");
    assert(frame.transform && Number.isFinite(frame.transform.scale) && frame.transform.scale > 0, "INVALID_RENDER_PLAN", "Each frame requires a positive scale.");
    assert(Array.isArray(frame.transform.offset_px) && frame.transform.offset_px.length === 2 && frame.transform.offset_px.every(Number.isFinite), "INVALID_RENDER_PLAN", "Each frame requires a numeric pixel offset.");
  }
  assert(plan.output && plan.output.jpg && plan.output.jpg.enabled === true, "INVALID_RENDER_PLAN", "The validation run must export JPG.");
  assert(plan.output.jpg.color_profile === SRGB_PROFILE, "INVALID_RENDER_PLAN", `JPG output profile must be ${SRGB_PROFILE}.`);
  assert(plan.output.jpg.conversion_intent === SRGB_INTENT, "INVALID_RENDER_PLAN", `JPG conversion intent must be ${SRGB_INTENT}.`);
  assert(plan.output.jpg.black_point_compensation === true, "INVALID_RENDER_PLAN", "JPG sRGB conversion must enable black-point compensation.");
  assert(plan.output.jpg.dither === true, "INVALID_RENDER_PLAN", "JPG sRGB conversion must enable dithering.");
  assert(plan.output.jpg.embed_color_profile === true, "INVALID_RENDER_PLAN", "JPG output must embed its sRGB color profile.");
  assert(plan.output.editable_psd && plan.output.editable_psd.enabled === true, "INVALID_RENDER_PLAN", "The validation run must export editable PSD.");
  assert(plan.output.jpg.overwrite === false && plan.output.editable_psd.overwrite === false, "INVALID_RENDER_PLAN", "Validation outputs must not overwrite existing files.");
  return spec;
}

function assertWholeProductRenderPlan(plan, spec) {
  assert(Array.isArray(plan.pages) && plan.pages.length === spec.pageCount, "INVALID_RENDER_PLAN", `Whole-product validation requires exactly ${spec.pageCount} pages.`);
  const seenPageIds = new Set();
  const seenFrameIds = new Set();
  let frameCount = 0;

  const approvedPageOrder = Object.keys(spec.pages);
  for (let pageIndex = 0; pageIndex < plan.pages.length; pageIndex += 1) {
    const page = plan.pages[pageIndex];
    const pageSpec = spec.pages[page.page_id];
    assert(pageSpec, "INVALID_RENDER_PLAN", `Unexpected whole-product page: ${page.page_id || "(missing)"}.`);
    assert(page.page_id === approvedPageOrder[pageIndex], "INVALID_RENDER_PLAN", `Whole-product pages must use the approved order; expected ${approvedPageOrder[pageIndex]}.`);
    assert(!seenPageIds.has(page.page_id), "INVALID_RENDER_PLAN", "Whole-product RenderPlan contains a duplicate page id.", { page_id: page.page_id });
    seenPageIds.add(page.page_id);
    assert(fileNameFromRelativePath(page.template_copy) === pageSpec.templateName, "INVALID_RENDER_PLAN", `Page ${page.page_id} must use ${pageSpec.templateName}.`);
    assert(String(page.template_sha256 || "").toLowerCase() === pageSpec.templateSha256, "INVALID_RENDER_PLAN", `Page ${page.page_id} template hash does not match the approved copy.`);
    assert(arraysEqual(page.expected_canvas, pageSpec.expectedCanvas), "INVALID_RENDER_PLAN", `Page ${page.page_id} canvas does not match the approved fixture.`);

    const expectedFrameIds = Object.keys(pageSpec.frames);
    assert(Array.isArray(page.frames) && page.frames.length === expectedFrameIds.length, "INVALID_RENDER_PLAN", `Page ${page.page_id} requires exactly ${expectedFrameIds.length} frame(s).`);
    const seenLayerPaths = new Set();
    for (const frame of page.frames) {
      const frameSpec = pageSpec.frames[frame.frame_id];
      assert(frameSpec, "INVALID_RENDER_PLAN", `Unexpected frame id on ${page.page_id}: ${frame.frame_id || "(missing)"}.`);
      assert(!seenFrameIds.has(frame.frame_id), "INVALID_RENDER_PLAN", "Whole-product RenderPlan contains a duplicate frame id.", { frame_id: frame.frame_id });
      seenFrameIds.add(frame.frame_id);
      frameCount += 1;

      assert(frame.target && frame.target.layer_path === frameSpec.layerPath, "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} has an unexpected calibrated layer path.`);
      assert(!seenLayerPaths.has(frame.target.layer_path), "INVALID_RENDER_PLAN", `Page ${page.page_id} targets the same calibrated layer more than once.`, { layer_path: frame.target.layer_path });
      seenLayerPaths.add(frame.target.layer_path);
      assert(frame.target.layer_path_order === PSD_LAYER_PATH_ORDER, "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} layer path order is unsupported.`);
      assert(frame.target.expected_kind === "smartObject" && frame.target.expected_visible === true, "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} must target a visible smart object.`);
      assert(frame.target.expected_clipping === (frameSpec.expectedClipping !== false), "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} has an unexpected clipping expectation.`);
      assert(frame.target.audit_name === frameSpec.auditName, "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} has an unexpected audit name.`);
      assert(arraysEqual(frame.target.registered_frame, frameSpec.registeredFrame), "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} has an unexpected registered frame.`);
      assert(Array.isArray(frame.target.binding_evidence) && ["layer_path", "clipping_structure", "visible_area"].every((item) => frame.target.binding_evidence.includes(item)), "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} is missing required structural binding evidence.`);

      if (frameSpec.windowLayerPath) {
        assert(frame.target.window && frame.target.window.layer_path === frameSpec.windowLayerPath, "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} has an unexpected window-layer path.`);
        assert(frame.target.window.layer_path_order === PSD_LAYER_PATH_ORDER, "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} window path order is unsupported.`);
        assert(frame.target.window.expected_kind === "pixel", "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} window must be a pixel layer.`);
        assert(frame.target.window.expected_visible === true && frame.target.window.expected_clipping === false, "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} window structure is incomplete.`);
      } else {
        assert(!frame.target.window, "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} must not declare an uncalibrated window layer.`);
      }
      assert(!Object.prototype.hasOwnProperty.call(frame.target, "superseded_paths"), "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} must use structured superseded-layer contracts.`);
      assert(supersededLayerContractsEqual(frame.target.superseded_layers, frameSpec.supersededLayers), "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} has an unexpected superseded replacement-stack contract.`);

      assert(frame.source && fileNameFromRelativePath(frame.source.relative_path) === frameSpec.sourceName, "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} must use ${frameSpec.sourceName}.`);
      assert(String(frame.source.sha256 || "").toLowerCase() === frameSpec.sourceSha256, "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} source hash does not match the approved image.`);
      assert(frame.fit === "cover", "INVALID_RENDER_PLAN", "Whole-product validation only accepts cover fitting.");
      assert(frame.crop && Array.isArray(frame.crop.focal_point) && frame.crop.focal_point.length === 2 && frame.crop.focal_point.every(Number.isFinite), "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} requires a numeric focal point.`);
      assert(frame.crop.focal_point.every((value) => value >= 0 && value <= 1), "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} focal-point coordinates must be between 0 and 1.`);
      assert(frame.crop.overflow_policy === "clip_to_registered_frame", "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} must clip overflow to its registered frame.`);
      assert(Array.isArray(frame.target_point) && frame.target_point.length === 2 && frame.target_point.every(Number.isFinite), "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} requires a numeric target point.`);
      assert(frame.target_point.every((value) => value >= 0 && value <= 1), "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} target-point coordinates must be between 0 and 1.`);
      assert(frame.transform && Number.isFinite(frame.transform.scale) && frame.transform.scale > 0, "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} requires a positive scale.`);
      assert(Array.isArray(frame.transform.offset_px) && frame.transform.offset_px.length === 2 && frame.transform.offset_px.every(Number.isFinite), "INVALID_RENDER_PLAN", `Frame ${frame.frame_id} requires a numeric pixel offset.`);
    }
  }

  assert(seenPageIds.size === spec.pageCount && Object.keys(spec.pages).every((pageId) => seenPageIds.has(pageId)), "INVALID_RENDER_PLAN", "Whole-product RenderPlan is missing an approved page.");
  assert(frameCount === spec.frameCount, "INVALID_RENDER_PLAN", `Whole-product validation requires exactly ${spec.frameCount} frames.`);
  assert(plan.output && plan.output.jpg && plan.output.jpg.enabled === true, "INVALID_RENDER_PLAN", "Whole-product validation must export JPG files.");
  assert(plan.output.jpg.quality === 12, "INVALID_RENDER_PLAN", "Whole-product validation requires the approved maximum JPG quality.");
  assert(plan.output.jpg.color_profile === SRGB_PROFILE, "INVALID_RENDER_PLAN", `JPG output profile must be ${SRGB_PROFILE}.`);
  assert(plan.output.jpg.conversion_intent === SRGB_INTENT, "INVALID_RENDER_PLAN", `JPG conversion intent must be ${SRGB_INTENT}.`);
  assert(plan.output.jpg.black_point_compensation === true && plan.output.jpg.dither === true && plan.output.jpg.embed_color_profile === true, "INVALID_RENDER_PLAN", "Whole-product JPG color policy is incomplete.");
  assert(plan.output.jpg.overwrite === false, "INVALID_RENDER_PLAN", "Whole-product outputs must not overwrite existing files.");
  assert(plan.output.editable_psd && plan.output.editable_psd.enabled === false, "INVALID_RENDER_PLAN", "Whole-product validation represents the default JPG-only workflow.");
  assert(plan.output.editable_psd.overwrite === false, "INVALID_RENDER_PLAN", "Whole-product PSD overwrite must remain disabled.");
  const controlledFailure = plan.validation.controlled_failure;
  if (controlledFailure !== undefined) {
    const keys = Object.keys(controlledFailure).sort();
    assert(plan.job_id === "uxp-a6015-tv13-failure-001", "INVALID_RENDER_PLAN", "The controlled failure plan must use its fixed TV-13 job id.");
    assert(keys.join(",") === "checkpoint,completed_page_count,error_code,next_frame_id,next_page_id", "INVALID_RENDER_PLAN", "The controlled failure contract contains unsupported fields.");
    assert(controlledFailure.checkpoint === "after_completed_pages", "INVALID_RENDER_PLAN", "TV-13 only supports the approved after-completed-pages checkpoint.");
    assert(controlledFailure.completed_page_count === 2, "INVALID_RENDER_PLAN", "TV-13 must fail after exactly two completed page outputs.");
    assert(controlledFailure.error_code === "TV13_CONTROLLED_FAILURE", "INVALID_RENDER_PLAN", "TV-13 must use the approved structured error code.");
    assert(controlledFailure.next_page_id === "G-详情-04-面料", "INVALID_RENDER_PLAN", "TV-13 must stop immediately before the approved third page.");
    assert(controlledFailure.next_frame_id === "G-详情-04.frame-4", "INVALID_RENDER_PLAN", "TV-13 must identify the next unstarted frame.");
  }
  return spec;
}

function arraysEqual(actual, expected) {
  return Array.isArray(actual) && Array.isArray(expected) && actual.length === expected.length && actual.every((value, index) => value === expected[index]);
}

function supersededLayerContractsEqual(actual, expected) {
  return Array.isArray(actual) && Array.isArray(expected) && actual.length === expected.length && actual.every((contract, index) => {
    const approved = expected[index];
    return contract
      && contract.layer_path === approved.layerPath
      && contract.layer_path_order === PSD_LAYER_PATH_ORDER
      && contract.expected_kind === "smartObject"
      && contract.expected_visible === true
      && contract.expected_clipping === approved.expectedClipping;
  });
}

function supersededLayerContracts(target) {
  if (Array.isArray(target.superseded_layers)) return target.superseded_layers;
  return (target.superseded_paths || []).map((layerPath) => ({
    layer_path: layerPath,
    layer_path_order: target.layer_path_order,
    expected_kind: "smartObject",
    expected_visible: true,
    expected_clipping: true
  }));
}

async function writePluginEvidence(name, value) {
  const folder = await localFileSystem.getDataFolder();
  const file = await folder.createFile(name, { overwrite: true });
  await file.write(JSON.stringify(value, null, 2));
}

async function recordPhotoshopProbe() {
  try {
    const result = await core.executeAsModal(async () => {
      const documentResult = await action.batchPlay([
        {
          _obj: "get",
          _target: [{ _ref: "document", _enum: "ordinal", _value: "targetEnum" }],
          _options: { dialogOptions: "dontDisplay" }
        }
      ], { synchronousExecution: true, modalBehavior: "execute" });
      return {
        ok: true,
        probe: {
          photoshop_version: app.version || null,
          active_document: documentResult[0] || null,
          uxp_manifest: "v5",
          plugin_version: PLUGIN_VERSION,
          recorded_at: new Date().toISOString()
        }
      };
    }, { commandName: "UXP validation probe" });
    await writePluginEvidence("uxp-validation-probe.json", result);
    showStatus("Probe recorded", result);
  } catch (error) {
    const result = errorResult(error);
    showStatus("Probe failed safely", result);
  }
}

async function handshakeWithDesktop() {
  const request = {
    protocol: "render-plan/v1",
    plugin_id: "local.ecommerce.uxp-validation",
    plugin_version: PLUGIN_VERSION,
    photoshop_version: app.version || null,
    capabilities: ["probe", "render-plan-receive", "single-frame-copy", "multi-frame-copy", "whole-product-copy", "structured-errors"]
  };
  try {
    const response = await fetch(`${BRIDGE_URL}/handshake`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(request)
    });
    const body = await response.json();
    const result = { ok: response.ok, request, response: body };
    showStatus(response.ok ? "Bridge connected" : "Bridge rejected handshake", result);
  } catch (error) {
    console.error("UXP bridge handshake failed", error);
    showStatus("Bridge unavailable", errorResult(error, { url: BRIDGE_URL }));
  }
}

async function loadRenderPlan() {
  try {
    const file = await localFileSystem.getFileForOpening({ types: ["json"] });
    if (!file) return;
    const plan = JSON.parse(await file.read());
    const spec = assertRenderPlan(plan);
    loadedPlan = plan;
    executeButton.disabled = false;
    showStatus("RenderPlan loaded", {
      ok: true,
      validation_mode: plan.validation.mode,
      job_id: plan.job_id,
      page_ids: plan.pages.map((page) => page.page_id),
      frame_ids: plan.pages.flatMap((page) => page.frames.map((frame) => frame.frame_id)),
      template_names: plan.validation.mode === "whole_product_copy"
        ? Object.values(spec.pages).map((pageSpec) => pageSpec.templateName)
        : [spec.templateName]
    });
  } catch (error) {
    loadedPlan = null;
    executeButton.disabled = true;
    showStatus("RenderPlan rejected", errorResult(error));
  }
}

function assertEntryName(entry, expectedName, code) {
  assert(entry && entry.name === expectedName, code, `Select the exact file named ${expectedName}.`, {
    selected_name: entry ? entry.name : null,
    expected_name: expectedName
  });
}

function fileNameFromRelativePath(value) {
  return String(value || "").replace(/\\/g, "/").split("/").pop();
}

async function selectValidationEntries(plan, spec) {
  showStatus("Select the disposable PSD copy", { expected_name: spec.templateName });
  const templateFile = await localFileSystem.getFileForOpening({ types: ["psd"] });
  if (!templateFile) throw new ValidationError("USER_CANCELLED", "Template selection was canceled.");
  assertEntryName(templateFile, spec.templateName, "WRONG_TEMPLATE_COPY");

  const sourceFilesByFrameId = new Map();
  let sourceContainerPath = "";
  if (plan.validation.mode === "single_frame_copy") {
    const framePlan = plan.pages[0].frames[0];
    const expectedSourceName = fileNameFromRelativePath(framePlan.source.relative_path);
    showStatus("Select the approved source image", { expected_name: expectedSourceName });
    const sourceFile = await localFileSystem.getFileForOpening({ types: ["jpg", "jpeg"] });
    if (!sourceFile) throw new ValidationError("USER_CANCELLED", "Source image selection was canceled.");
    assertEntryName(sourceFile, expectedSourceName, "WRONG_SOURCE_IMAGE");
    sourceFilesByFrameId.set(framePlan.frame_id, sourceFile);
    sourceContainerPath = parentPath(sourceFile.nativePath);
  } else {
    const expectedNames = plan.pages[0].frames.map((frame) => fileNameFromRelativePath(frame.source.relative_path));
    showStatus("Select the folder containing the four approved source images", { expected_names: expectedNames });
    const sourceFolder = await localFileSystem.getFolder();
    if (!sourceFolder) throw new ValidationError("USER_CANCELLED", "Source-folder selection was canceled.");
    const entries = await sourceFolder.getEntries();
    for (const framePlan of plan.pages[0].frames) {
      const expectedName = fileNameFromRelativePath(framePlan.source.relative_path);
      const matches = entries.filter((entry) => entry.isFile && entry.name === expectedName);
      assert(matches.length === 1, "SOURCE_SET_MISMATCH", `The selected source folder must contain exactly one file named ${expectedName}.`, {
        expected_name: expectedName,
        match_count: matches.length
      });
      sourceFilesByFrameId.set(framePlan.frame_id, matches[0]);
    }
    sourceContainerPath = normalizeWindowsPath(sourceFolder.nativePath);
  }

  showStatus("Select a separate output folder", { output_files: Object.values(spec.outputNames) });
  const outputFolder = await localFileSystem.getFolder();
  if (!outputFolder) throw new ValidationError("USER_CANCELLED", "Output folder selection was canceled.");

  const outputPath = normalizeWindowsPath(outputFolder.nativePath);
  assert(outputPath && outputPath !== parentPath(templateFile.nativePath), "OUTPUT_NOT_SEPARATE", "Output folder must not be the template-copy folder.");
  assert(outputPath !== sourceContainerPath, "OUTPUT_NOT_SEPARATE", "Output folder must not be the source-image folder.");

  const existingNames = (await outputFolder.getEntries()).map((entry) => entry.name);
  const collisions = Object.values(spec.outputNames).filter((name) => existingNames.includes(name));
  assert(collisions.length === 0, "OUTPUT_EXISTS", "The selected output folder already contains validation output. Choose a clean folder or move the previous result first.", { collisions });

  const alreadyOpen = app.documents.some((document) => document.name === spec.templateName);
  assert(!alreadyOpen, "TEMPLATE_ALREADY_OPEN", `Close ${spec.templateName} in Photoshop before running the test.`);

  return { templateFile, sourceFilesByFrameId, outputFolder };
}

async function selectWholeProductEntries(plan, spec) {
  const expectedTemplateNames = Object.values(spec.pages).map((pageSpec) => pageSpec.templateName);
  showStatus("Select the folder containing the six disposable PSD copies", { expected_names: expectedTemplateNames });
  const templateFolder = await localFileSystem.getFolder();
  if (!templateFolder) throw new ValidationError("USER_CANCELLED", "Template-folder selection was canceled.");
  const templateEntries = await templateFolder.getEntries();
  const templateFilesByPageId = new Map();
  for (const pagePlan of plan.pages) {
    const pageSpec = spec.pages[pagePlan.page_id];
    const matches = templateEntries.filter((entry) => entry.isFile && entry.name === pageSpec.templateName);
    assert(matches.length === 1, "TEMPLATE_SET_MISMATCH", `The selected template folder must contain exactly one file named ${pageSpec.templateName}.`, {
      expected_name: pageSpec.templateName,
      match_count: matches.length
    });
    templateFilesByPageId.set(pagePlan.page_id, matches[0]);
  }

  const expectedSourceNames = Array.from(new Set(plan.pages.flatMap((page) => page.frames.map((frame) => fileNameFromRelativePath(frame.source.relative_path)))));
  showStatus("Select the folder containing the approved source images", { expected_names: expectedSourceNames });
  const sourceFolder = await localFileSystem.getFolder();
  if (!sourceFolder) throw new ValidationError("USER_CANCELLED", "Source-folder selection was canceled.");
  const sourceEntries = await sourceFolder.getEntries();
  const sourceFilesByName = new Map();
  for (const expectedName of expectedSourceNames) {
    const matches = sourceEntries.filter((entry) => entry.isFile && entry.name === expectedName);
    assert(matches.length === 1, "SOURCE_SET_MISMATCH", `The selected source folder must contain exactly one file named ${expectedName}.`, {
      expected_name: expectedName,
      match_count: matches.length
    });
    sourceFilesByName.set(expectedName, matches[0]);
  }

  const outputNames = Object.fromEntries([
    ...Object.values(spec.pages).map((pageSpec) => [pageSpec.outputName, pageSpec.outputName]),
    ["evidence", spec.evidenceName]
  ]);
  showStatus("Select a clean output folder", { output_files: Object.values(outputNames) });
  const outputFolder = await localFileSystem.getFolder();
  if (!outputFolder) throw new ValidationError("USER_CANCELLED", "Output-folder selection was canceled.");
  const outputPath = normalizeWindowsPath(outputFolder.nativePath);
  assert(outputPath && outputPath !== normalizeWindowsPath(templateFolder.nativePath), "OUTPUT_NOT_SEPARATE", "Output folder must not be the template-copy folder.");
  assert(outputPath !== normalizeWindowsPath(sourceFolder.nativePath), "OUTPUT_NOT_SEPARATE", "Output folder must not be the source-image folder.");
  const existingNames = (await outputFolder.getEntries()).map((entry) => entry.name);
  const collisions = Object.values(outputNames).filter((name) => existingNames.includes(name));
  assert(collisions.length === 0, "OUTPUT_EXISTS", "The selected output folder already contains whole-product validation output.", { collisions });

  const openNames = safeOpenDocuments().map((document) => safeDocumentName(document));
  const conflictingOpenNames = expectedTemplateNames.filter((name) => openNames.includes(name));
  assert(conflictingOpenNames.length === 0, "TEMPLATE_ALREADY_OPEN", "Close every whole-product validation copy in Photoshop before running the test.", {
    open_template_names: conflictingOpenNames
  });

  return {
    templateFolder,
    templateFilesByPageId,
    sourceFolder,
    sourceFilesByName,
    outputFolder,
    outputNames
  };
}

function layerSnapshot(collection) {
  return Array.from(collection).map((layer, uxpIndex) => ({
    uxp_index: uxpIndex,
    psd_index: collection.length - 1 - uxpIndex,
    id: layer.id,
    name: layer.name,
    kind: String(layer.kind),
    visible: layer.visible
  }));
}

function safeLayerValue(read, fallback = null) {
  try {
    const value = read();
    return value === undefined ? fallback : value;
  } catch (error) {
    return fallback;
  }
}

function flattenLayers(collection, parentPath = "") {
  const records = [];
  const layers = Array.from(collection);
  layers.forEach((layer, uxpIndex) => {
    const psdIndex = layers.length - 1 - uxpIndex;
    const layerPath = parentPath ? `${parentPath}/${psdIndex}` : String(psdIndex);
    records.push({ layerPath, layer, uxpIndex, psdIndex });
    if (layer.layers && layer.layers.length) {
      records.push(...flattenLayers(layer.layers, layerPath));
    }
  });
  return records;
}

async function documentStructureSnapshot(document) {
  const flattened = flattenLayers(document.layers);
  const requests = flattened.map(({ layer }) => ({
    _obj: "get",
    _target: [{ _ref: "layer", _id: layer.id }],
    _options: { dialogOptions: "silent" }
  }));
  const descriptors = requests.length
    ? await action.batchPlay(requests, { synchronousExecution: true, modalBehavior: "execute" })
    : [];

  return flattened.map((record, index) => {
    const descriptor = descriptors[index] || {};
    const bounds = safeLayerValue(() => layerBounds(record.layer));
    return {
      layer_path: record.layerPath,
      id: record.layer.id,
      name: record.layer.name,
      kind: String(record.layer.kind),
      visible: record.layer.visible !== false,
      clipping: descriptor.group === true,
      has_user_mask: descriptor.hasUserMask === true,
      has_vector_mask: descriptor.hasVectorMask === true,
      opacity: safeLayerValue(() => asNumber(record.layer.opacity)),
      blend_mode: safeLayerValue(() => String(record.layer.blendMode)),
      bounds
    };
  });
}

function geometryEqual(left, right, tolerance = 0.01) {
  if (left === null || right === null) return left === right;
  return ["left", "top", "right", "bottom"].every((key) => Math.abs(Number(left[key]) - Number(right[key])) <= tolerance);
}

function compareStructureSnapshots(before, after, targetPaths) {
  const afterByPath = new Map(after.map((record) => [record.layer_path, record]));
  const targetSet = new Set(targetPaths);
  const differences = [];
  const targetNameChanges = [];
  const invariantKeys = ["id", "name", "kind", "visible", "clipping", "has_user_mask", "has_vector_mask", "opacity", "blend_mode"];

  if (before.length !== after.length) {
    differences.push({ type: "layer_count", before: before.length, after: after.length });
  }
  for (const beforeRecord of before) {
    const afterRecord = afterByPath.get(beforeRecord.layer_path);
    if (!afterRecord) {
      differences.push({ type: "missing_layer", layer_path: beforeRecord.layer_path });
      continue;
    }
    for (const key of invariantKeys) {
      if (key === "name" && targetSet.has(beforeRecord.layer_path)) {
        if (beforeRecord[key] !== afterRecord[key]) {
          targetNameChanges.push({
            layer_path: beforeRecord.layer_path,
            before: beforeRecord[key],
            after: afterRecord[key]
          });
        }
        continue;
      }
      if (beforeRecord[key] !== afterRecord[key]) {
        differences.push({ type: "property_changed", layer_path: beforeRecord.layer_path, property: key, before: beforeRecord[key], after: afterRecord[key] });
      }
    }
    if (!targetSet.has(beforeRecord.layer_path) && !geometryEqual(beforeRecord.bounds, afterRecord.bounds)) {
      differences.push({ type: "non_target_bounds_changed", layer_path: beforeRecord.layer_path, before: beforeRecord.bounds, after: afterRecord.bounds });
    }
  }
  const beforePaths = new Set(before.map((record) => record.layer_path));
  for (const afterRecord of after) {
    if (!beforePaths.has(afterRecord.layer_path)) differences.push({ type: "unexpected_layer", layer_path: afterRecord.layer_path });
  }

  return {
    passed: differences.length === 0,
    layer_count_before: before.length,
    layer_count_after: after.length,
    target_paths: targetPaths,
    target_name_changes: targetNameChanges,
    target_name_changes_allowed: true,
    non_target_structure_unchanged: differences.length === 0,
    differences
  };
}

function resolveLayerPath(document, layerPath, layerPathOrder) {
  const indexes = layerPath.split("/").map((part) => Number(part));
  assert(indexes.length > 0 && indexes.every(Number.isInteger), "INVALID_LAYER_PATH", "Layer path must contain integer indexes.", { layer_path: layerPath });
  assert(layerPathOrder === PSD_LAYER_PATH_ORDER, "INVALID_LAYER_PATH_ORDER", `Unsupported layer path order: ${layerPathOrder}.`);

  let collection = document.layers;
  let layer = null;
  const resolution = [];
  for (const psdIndex of indexes) {
    assert(psdIndex >= 0 && psdIndex < collection.length, "LAYER_PATH_NOT_FOUND", "Calibrated layer path does not exist in the selected PSD copy.", {
      layer_path: layerPath,
      failed_psd_index: psdIndex,
      collection_length: collection.length
    });
    const uxpIndex = collection.length - 1 - psdIndex;
    layer = collection[uxpIndex];
    resolution.push({
      psd_index: psdIndex,
      uxp_index: uxpIndex,
      collection_length: collection.length,
      id: layer.id,
      name: layer.name,
      kind: String(layer.kind)
    });
    collection = layer.layers || [];
  }
  return { layer, resolution };
}

function isSmartObject(layer) {
  const value = String(layer.kind || "").replace(/[^a-z]/gi, "").toLowerCase();
  return layer.kind === constants.LayerKind.SMARTOBJECT || value === "smartobject";
}

function normalizedLayerKind(layer) {
  return String(layer.kind || "").replace(/[^a-z]/gi, "").toLowerCase();
}

function frameBoundsEqual(bounds, frame, tolerance = 1.5) {
  return Math.abs(bounds.left - frame[0]) <= tolerance
    && Math.abs(bounds.top - frame[1]) <= tolerance
    && Math.abs(bounds.right - frame[2]) <= tolerance
    && Math.abs(bounds.bottom - frame[3]) <= tolerance;
}

function assertRegisteredFrame(document, frame) {
  const [left, top, right, bottom] = frame;
  const width = asNumber(document.width);
  const height = asNumber(document.height);
  assert(right > left && bottom > top, "INVALID_REGISTERED_FRAME", "Registered frame dimensions must be positive.", { frame });
  assert(left >= 0 && top >= 0 && right <= width && bottom <= height, "REGISTERED_FRAME_OUTSIDE_CANVAS", "Registered frame is outside the selected PSD canvas.", {
    frame,
    canvas: [width, height]
  });
  return { width, height };
}

function assertDocumentCanvas(document, expectedCanvas) {
  const actual = [asNumber(document.width), asNumber(document.height)];
  assert(arraysEqual(actual, expectedCanvas), "CANVAS_MISMATCH", "The selected PSD copy does not have the approved fixture canvas.", {
    expected_canvas: expectedCanvas,
    actual_canvas: actual
  });
  return { width: actual[0], height: actual[1] };
}

function compareAuditNames(expectedName, actualName) {
  const expected = String(expectedName == null ? "" : expectedName);
  const actual = String(actualName == null ? "" : actualName);
  if (expected === actual) {
    return { matches: true, mode: "exact", expected, actual };
  }
  const normalizedExpected = expected.trim();
  const normalizedActual = actual.trim();
  const matches = normalizedExpected.length > 0 && normalizedExpected === normalizedActual;
  return {
    matches,
    mode: matches ? "trimmed_boundary_whitespace" : "mismatch",
    expected,
    actual,
    normalized_expected: normalizedExpected,
    normalized_actual: normalizedActual
  };
}

function findOpenDocumentByName(documents, expectedName, required = true) {
  const matches = Array.from(documents || []).filter((candidate) => candidate.name === expectedName);
  if (!required && matches.length === 0) return null;
  assert(matches.length === 1, "WORKING_DOCUMENT_NOT_UNIQUE", "Photoshop must contain exactly one open validation-copy document.", {
    expected_name: expectedName,
    match_count: matches.length,
    open_document_names: Array.from(documents || []).map((candidate) => candidate.name)
  });
  return matches[0];
}

function safeDocumentName(document) {
  try {
    return document ? document.name : null;
  } catch (error) {
    return null;
  }
}

function safeDocumentId(document) {
  try {
    const id = document ? document.id : null;
    return Number.isFinite(id) ? id : null;
  } catch (error) {
    return null;
  }
}

function safeActiveDocument() {
  try {
    return app.activeDocument || null;
  } catch (error) {
    return null;
  }
}

function safeOpenDocuments() {
  try {
    return Array.from(app.documents || []);
  } catch (error) {
    return [];
  }
}

function inspectProperty(target, propertyName) {
  if (target == null) {
    return { readable: false, value: null, error: "OBJECT_IS_NULL" };
  }
  try {
    const value = target[propertyName];
    return { readable: true, value: value == null ? null : value, error: null };
  } catch (error) {
    return { readable: false, value: null, error: error.message || String(error) };
  }
}

function inspectDocumentReference(document) {
  return {
    is_null: document == null,
    type: typeof document,
    name: inspectProperty(document, "name"),
    id: inspectProperty(document, "id")
  };
}

function inspectPhotoshopDocumentState(openedDocument) {
  let activeDocument = null;
  let activeDocumentError = null;
  try {
    activeDocument = app.activeDocument || null;
  } catch (error) {
    activeDocumentError = error.message || String(error);
  }

  let documents = [];
  let documentsError = null;
  try {
    documents = Array.from(app.documents || []);
  } catch (error) {
    documentsError = error.message || String(error);
  }

  return {
    recorded_at: new Date().toISOString(),
    opened_document: inspectDocumentReference(openedDocument),
    active_document_getter_error: activeDocumentError,
    active_document: inspectDocumentReference(activeDocument),
    documents_getter_error: documentsError,
    document_count: documents.length,
    documents: documents.map(inspectDocumentReference)
  };
}

function findInspectableDocument(openedDocument, expectedName) {
  const candidates = [openedDocument];
  try {
    candidates.push(app.activeDocument || null);
  } catch (error) {
    // The diagnostic snapshot records the getter error separately.
  }
  try {
    candidates.push(...Array.from(app.documents || []));
  } catch (error) {
    // The diagnostic snapshot records the getter error separately.
  }
  const seen = new Set();
  for (const candidate of candidates) {
    if (!candidate || seen.has(candidate)) continue;
    seen.add(candidate);
    const name = inspectProperty(candidate, "name");
    if (name.readable && name.value === expectedName) return candidate;
  }
  return null;
}

async function diagnoseAppOpenEntry(entry, strategy) {
  const expectedName = entry.name;
  let openedDocument = null;
  let modalSnapshot = null;
  let openError = null;
  let modalBoundaryError = null;
  const startedAt = Date.now();

  try {
    await core.executeAsModal(async () => {
      try {
        openedDocument = await app.open(entry);
      } catch (error) {
        openError = error.message || String(error);
      }
      modalSnapshot = inspectPhotoshopDocumentState(openedDocument);
    }, { commandName: `Diagnose PSD open: ${strategy}` });
  } catch (error) {
    modalBoundaryError = error.message || String(error);
  }

  const outsideSnapshots = [];
  let controlledDocument = null;
  for (let attempt = 0; attempt < 25; attempt += 1) {
    const snapshot = inspectPhotoshopDocumentState(openedDocument);
    snapshot.attempt = attempt + 1;
    snapshot.elapsed_ms = Date.now() - startedAt;
    outsideSnapshots.push(snapshot);
    controlledDocument = findInspectableDocument(openedDocument, expectedName);
    if (controlledDocument) break;
    await waitForMilliseconds(200);
  }

  let closeResult = { attempted: false, closed: false, error: null };
  if (controlledDocument) {
    closeResult = { attempted: true, closed: false, error: null };
    try {
      await core.executeAsModal(async () => {
        await controlledDocument.closeWithoutSaving();
        closeResult.closed = true;
      }, { commandName: `Close PSD open diagnostic: ${strategy}` });
    } catch (error) {
      closeResult.error = error.message || String(error);
    }
  }

  return {
    strategy,
    entry: {
      name: entry.name,
      native_path: entry.nativePath || null,
      is_file: entry.isFile === true,
      type: typeof entry
    },
    open_error: openError,
    modal_boundary_error: modalBoundaryError,
    modal_snapshot: modalSnapshot,
    outside_snapshots: outsideSnapshots,
    resolved_after_modal: Boolean(controlledDocument),
    close: closeResult,
    elapsed_ms: Date.now() - startedAt
  };
}

async function runPsdOpenDiagnostic() {
  const expectedName = "G-详情-01-validation-copy.psd";
  try {
    showStatus("Select the G-详情-01 validation-copy PSD directly", { expected_name: expectedName });
    const directEntry = await localFileSystem.getFileForOpening();
    if (!directEntry) throw new ValidationError("USER_CANCELLED", "Direct PSD selection was canceled.");
    assertEntryName(directEntry, expectedName, "WRONG_TEMPLATE_COPY");

    const openNames = safeOpenDocuments().map((document) => safeDocumentName(document));
    assert(!openNames.includes(expectedName), "TEMPLATE_ALREADY_OPEN", `Close ${expectedName} before running the diagnostic.`);

    const directResult = await diagnoseAppOpenEntry(directEntry, "direct_file_picker");
    const result = {
      ok: false,
      schema: "uxp-psd-open-diagnostic/v1",
      plugin_version: PLUGIN_VERSION,
      photoshop_version: app.version || null,
      expected_name: expectedName,
      input_modified: false,
      save_or_export_attempted: false,
      direct_file_picker: directResult,
      folder_child_entry: null,
      conclusion: null
    };

    const directPassed = !directResult.open_error
      && !directResult.modal_boundary_error
      && directResult.resolved_after_modal
      && directResult.close.closed;
    if (!directPassed) {
      result.conclusion = "DIRECT_PICKER_OPEN_UNRESOLVED";
      await writePluginEvidence("uxp-psd-open-diagnostic.json", result);
      showStatus("PSD open diagnostic stopped safely", result);
      return;
    }

    showStatus("Select the folder containing the same G-详情-01 validation-copy PSD", { expected_name: expectedName });
    const templateFolder = await localFileSystem.getFolder();
    if (!templateFolder) throw new ValidationError("USER_CANCELLED", "Template-folder selection was canceled.");
    const matches = (await templateFolder.getEntries()).filter((entry) => entry.isFile && entry.name === expectedName);
    assert(matches.length === 1, "TEMPLATE_SET_MISMATCH", `The selected folder must contain exactly one ${expectedName}.`, {
      expected_name: expectedName,
      match_count: matches.length
    });
    const childEntry = matches[0];
    assert(
      normalizeWindowsPath(childEntry.nativePath) === normalizeWindowsPath(directEntry.nativePath),
      "DIAGNOSTIC_ENTRY_PATH_MISMATCH",
      "The direct file and folder child entry must refer to the same PSD copy.",
      { direct_path: directEntry.nativePath || null, folder_child_path: childEntry.nativePath || null }
    );
    const sessionToken = localFileSystem.createSessionToken(childEntry);
    const childResult = await diagnoseAppOpenEntry(childEntry, "folder_child_entry");
    childResult.session_token_created = typeof sessionToken === "string" && sessionToken.length > 0;
    result.folder_child_entry = childResult;
    const childPassed = !childResult.open_error
      && !childResult.modal_boundary_error
      && childResult.resolved_after_modal
      && childResult.close.closed;
    result.ok = directPassed && childPassed;
    result.conclusion = childPassed
      ? "BOTH_ENTRY_SOURCES_OPEN_AFTER_MODAL"
      : "FOLDER_CHILD_ENTRY_OPEN_UNRESOLVED";
    await writePluginEvidence("uxp-psd-open-diagnostic.json", result);
    showStatus(result.ok ? "PSD open diagnostic completed" : "PSD open diagnostic isolated the failing path", result);
  } catch (error) {
    const result = errorResult(error, {
      plugin_version: PLUGIN_VERSION,
      photoshop_version: app.version || null,
      input_modified: false,
      save_or_export_attempted: false
    });
    try {
      await writePluginEvidence("uxp-psd-open-diagnostic.json", result);
    } catch (evidenceError) {
      console.error("Failed to write PSD open diagnostic evidence", evidenceError);
    }
    showStatus(error.code === "USER_CANCELLED" ? "Diagnostic canceled safely" : "PSD open diagnostic failed safely", result);
  }
}

async function closeResidualValidationDocument(documentName) {
  const result = { attempted: true, closed: false, error: null };
  try {
    await core.executeAsModal(async () => {
      const candidate = reacquireControlledDocument(documentName, false);
      if (candidate) {
        await candidate.closeWithoutSaving();
        result.closed = true;
      }
    }, { commandName: "Close residual validation copy" });
  } catch (error) {
    result.error = error.message || String(error);
  }
  return result;
}

function assertOpenedDocument(openedDocument, expectedName) {
  const actualName = safeDocumentName(openedDocument);
  assert(openedDocument && actualName === expectedName, "OPENED_DOCUMENT_MISMATCH", "Photoshop did not return the selected validation-copy document.", {
    expected_name: expectedName,
    opened_document_name: actualName,
    active_document_name: safeDocumentName(safeActiveDocument()),
    open_document_names: safeOpenDocuments().map(safeDocumentName)
  });
  return openedDocument;
}

function waitForMilliseconds(milliseconds) {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}

async function waitForOpenedDocument(openedDocument, expectedName) {
  let lastObserved = {
    opened_document_name: safeDocumentName(openedDocument),
    active_document_name: safeDocumentName(safeActiveDocument()),
    open_document_names: safeOpenDocuments().map(safeDocumentName)
  };
  for (let attempt = 0; attempt < 25; attempt += 1) {
    const openedName = safeDocumentName(openedDocument);
    if (openedDocument && openedName === expectedName) return openedDocument;
    const activeDocument = safeActiveDocument();
    if (activeDocument && safeDocumentName(activeDocument) === expectedName) return activeDocument;
    const matches = safeOpenDocuments().filter((candidate) => safeDocumentName(candidate) === expectedName);
    if (matches.length === 1) return matches[0];
    lastObserved = {
      opened_document_name: openedName,
      active_document_name: safeDocumentName(activeDocument),
      open_document_names: safeOpenDocuments().map(safeDocumentName),
      attempt: attempt + 1
    };
    await waitForMilliseconds(200);
  }
  throw new ValidationError("OPENED_DOCUMENT_MISMATCH", "Photoshop did not expose the selected validation-copy document after waiting for the host document collection to refresh.", {
    expected_name: expectedName,
    ...lastObserved
  });
}

function createPhotoshopOpenDescriptor(sessionToken) {
  assert(typeof sessionToken === "string" && sessionToken.length > 0, "INVALID_SESSION_TOKEN", "Photoshop open requires a non-empty local file session token.");
  return {
    _obj: "open",
    target: { _path: sessionToken, _kind: "local" },
    _options: { dialogOptions: "silent" }
  };
}

function redactSessionToken(text, sessionToken) {
  const value = text == null ? "" : String(text);
  return sessionToken ? value.split(sessionToken).join("[REDACTED_SESSION_TOKEN]") : value;
}

function photoshopOpenFailure(error, sessionToken, descriptorResultCount) {
  const details = error instanceof ValidationError && error.code === "OPEN_TEMPLATE_FAILED"
    ? error.details || {}
    : {
        host_error_code: error && error.code != null ? redactSessionToken(error.code, sessionToken) : null,
        host_error_message: redactSessionToken(error && error.message ? error.message : error, sessionToken)
      };
  return new ValidationError("OPEN_TEMPLATE_FAILED", "Photoshop Action Manager could not open the selected validation-copy document.", {
    strategy: "batchplay_session_token",
    descriptor_result_count: descriptorResultCount,
    host_error_code: details.host_error_code == null ? null : redactSessionToken(details.host_error_code, sessionToken),
    host_error_message: details.host_error_message == null ? null : redactSessionToken(details.host_error_message, sessionToken)
  });
}

async function openDocumentAcrossModalBoundary(entry, expectedName, commandName) {
  const sessionToken = localFileSystem.createSessionToken(entry);
  const descriptor = createPhotoshopOpenDescriptor(sessionToken);
  let descriptorResultCount = 0;
  let descriptorDocumentId = null;
  let capturedOpenFailure = null;
  let modalBoundaryError = null;
  const modalStartedAt = Date.now();

  try {
    await core.executeAsModal(async (executionContext) => {
      try {
        executionContext.reportProgress({ value: 0.05, commandName: `Opening ${expectedName}` });
        const descriptorResults = await action.batchPlay([descriptor], {});
        descriptorResultCount = Array.isArray(descriptorResults) ? descriptorResults.length : 0;
        if (descriptorResultCount !== 1) {
          throw new ValidationError("OPEN_TEMPLATE_FAILED", "Photoshop Action Manager returned an unexpected number of open results.", {
            host_error_code: "INVALID_DESCRIPTOR_RESULT_COUNT",
            host_error_message: `Expected one open result, received ${descriptorResultCount}.`
          });
        }
        const firstResult = Array.isArray(descriptorResults) ? descriptorResults[0] : null;
        if (firstResult && firstResult._obj === "error") {
          throw new ValidationError("OPEN_TEMPLATE_FAILED", "Photoshop Action Manager returned an error descriptor while opening the validation copy.", {
            host_error_code: firstResult.result == null ? null : firstResult.result,
            host_error_message: firstResult.message == null ? null : firstResult.message
          });
        }
        descriptorDocumentId = firstResult && Number.isFinite(firstResult.documentID)
          ? firstResult.documentID
          : null;
      } catch (error) {
        capturedOpenFailure = serializeModalValidationFailure(photoshopOpenFailure(error, sessionToken, descriptorResultCount));
      }
    }, { commandName });
  } catch (error) {
    modalBoundaryError = error;
  }

  const openModalMs = Date.now() - modalStartedAt;
  const safeModalBoundaryError = modalBoundaryError
    ? photoshopOpenFailure(modalBoundaryError, sessionToken, descriptorResultCount)
    : null;
  if (capturedOpenFailure) {
    restoreModalValidationFailure(attachModalBoundaryError(capturedOpenFailure, safeModalBoundaryError));
  }
  if (safeModalBoundaryError) throw safeModalBoundaryError;

  const visibilityWaitStartedAt = Date.now();
  try {
    const document = await waitForOpenedDocument(null, expectedName);
    const documentId = safeDocumentId(document);
    assert(descriptorDocumentId == null || documentId === descriptorDocumentId, "OPENED_DOCUMENT_MISMATCH", "The Action Manager result and resolved Photoshop document do not identify the same document.", {
      expected_name: expectedName,
      strategy: "batchplay_session_token",
      descriptor_result_count: descriptorResultCount,
      descriptor_document_id: descriptorDocumentId,
      document_id: documentId
    });
    return {
      document,
      openedDocument: null,
      evidence: {
        strategy: "batchplay_session_token",
        descriptor_result_count: descriptorResultCount,
        descriptor_document_id: descriptorDocumentId,
        document_id: documentId
      },
      timing: {
        open_modal_ms: openModalMs,
        document_visibility_wait_ms: Date.now() - visibilityWaitStartedAt
      }
    };
  } catch (error) {
    if (error instanceof ValidationError) {
      error.details = {
        ...(error.details || {}),
        open_modal_ms: openModalMs,
        document_visibility_wait_ms: Date.now() - visibilityWaitStartedAt
      };
    }
    throw error;
  }
}

function reacquireControlledDocument(expectedName, required = true) {
  const documents = safeOpenDocuments();
  const collectionMatches = documents.filter((candidate) => safeDocumentName(candidate) === expectedName);
  const activeDocument = safeActiveDocument();
  const activeName = safeDocumentName(activeDocument);
  assert(collectionMatches.length <= 1, "WORKING_DOCUMENT_NOT_UNIQUE", "Photoshop exposed more than one open validation-copy document.", {
    expected_name: expectedName,
    match_count: collectionMatches.length,
    active_document_name: activeName,
    open_document_names: documents.map(safeDocumentName)
  });
  if (activeDocument && activeName === expectedName) return activeDocument;
  if (collectionMatches.length === 1) return collectionMatches[0];
  if (!required) return null;
  throw new ValidationError("WORKING_DOCUMENT_NOT_UNIQUE", "Photoshop did not expose the open validation-copy document after the operation.", {
    expected_name: expectedName,
    match_count: 0,
    active_document_name: activeName,
    open_document_names: documents.map(safeDocumentName)
  });
}

function resolveCurrentTarget(document, binding) {
  const resolved = resolveLayerPath(
    document,
    binding.framePlan.target.layer_path,
    binding.framePlan.target.layer_path_order
  );
  assert(resolved.layer.id === binding.evidence.target.id, "TARGET_ID_CHANGED", "The calibrated target layer identity changed during the multi-frame run.", {
    frame_id: binding.framePlan.frame_id,
    expected_id: binding.evidence.target.id,
    actual_id: resolved.layer.id,
    path_resolution: resolved.resolution
  });
  return resolved;
}

function preflightFrameBindings(document, pagePlan, structureBefore) {
  const structureByPath = new Map(structureBefore.map((record) => [record.layer_path, record]));
  const seenTargetIds = new Set();
  const bindings = [];

  for (const framePlan of pagePlan.frames) {
    assertRegisteredFrame(document, framePlan.target.registered_frame);
    const pathOrder = framePlan.target.layer_path_order;
    const resolvedTarget = resolveLayerPath(document, framePlan.target.layer_path, pathOrder);
    const targetLayer = resolvedTarget.layer;
    const targetStructure = structureByPath.get(framePlan.target.layer_path);
    assert(targetStructure, "BINDING_STRUCTURE_MISSING", "The target layer is absent from the structural snapshot.", { frame_id: framePlan.frame_id });
    assert(isSmartObject(targetLayer), "TARGET_NOT_SMART_OBJECT", "A calibrated target is not a smart object.", {
      frame_id: framePlan.frame_id,
      layer_path: framePlan.target.layer_path,
      actual_kind: String(targetLayer.kind),
      path_resolution: resolvedTarget.resolution
    });
    assert(targetLayer.visible !== false, "TARGET_NOT_VISIBLE", "A calibrated target is hidden.", { frame_id: framePlan.frame_id });
    assert(targetStructure.clipping === framePlan.target.expected_clipping, "TARGET_CLIPPING_MISMATCH", "A calibrated target no longer has the expected clipping relationship.", {
      frame_id: framePlan.frame_id,
      expected_clipping: framePlan.target.expected_clipping,
      actual_clipping: targetStructure.clipping
    });
    const auditNameComparison = compareAuditNames(framePlan.target.audit_name, targetLayer.name);
    assert(auditNameComparison.matches, "BINDING_AUDIT_MISMATCH", "The resolved smart object does not match the calibrated audit name.", {
      frame_id: framePlan.frame_id,
      expected_audit_name: framePlan.target.audit_name,
      actual_name: targetLayer.name,
      audit_name_comparison: auditNameComparison,
      path_resolution: resolvedTarget.resolution
    });
    assert(!seenTargetIds.has(targetLayer.id), "DUPLICATE_TARGET_LAYER", "Two calibrated frames resolve to the same Photoshop layer.", {
      frame_id: framePlan.frame_id,
      target_id: targetLayer.id
    });
    seenTargetIds.add(targetLayer.id);

    let windowEvidence = null;
    if (framePlan.target.window) {
      const resolvedWindow = resolveLayerPath(document, framePlan.target.window.layer_path, framePlan.target.window.layer_path_order);
      const windowLayer = resolvedWindow.layer;
      const windowStructure = structureByPath.get(framePlan.target.window.layer_path);
      assert(windowStructure, "WINDOW_STRUCTURE_MISSING", "The calibrated window layer is absent from the structural snapshot.", { frame_id: framePlan.frame_id });
      assert(normalizedLayerKind(windowLayer) === "pixel", "WINDOW_KIND_MISMATCH", "The calibrated window base is no longer a pixel layer.", {
        frame_id: framePlan.frame_id,
        actual_kind: String(windowLayer.kind)
      });
      assert(windowLayer.visible !== false, "WINDOW_NOT_VISIBLE", "The calibrated window base is hidden.", { frame_id: framePlan.frame_id });
      assert(windowStructure.clipping === false, "WINDOW_CLIPPING_MISMATCH", "The calibrated window base unexpectedly became clipped.", { frame_id: framePlan.frame_id });
      const windowBounds = layerBounds(windowLayer);
      assert(frameBoundsEqual(windowBounds, framePlan.target.registered_frame), "WINDOW_BOUNDS_MISMATCH", "The calibrated window bounds no longer match the registered frame.", {
        frame_id: framePlan.frame_id,
        registered_frame: framePlan.target.registered_frame,
        actual_bounds: windowBounds
      });
      windowEvidence = {
        layer_path: framePlan.target.window.layer_path,
        id: windowLayer.id,
        name: windowLayer.name,
        kind: String(windowLayer.kind),
        visible: windowLayer.visible,
        clipping: windowStructure.clipping,
        bounds: windowBounds,
        path_resolution: resolvedWindow.resolution
      };
    }

    const supersededEvidence = [];
    for (const supersededContract of supersededLayerContracts(framePlan.target)) {
      const supersededPath = supersededContract.layer_path;
      const resolvedSuperseded = resolveLayerPath(document, supersededPath, supersededContract.layer_path_order);
      const supersededLayer = resolvedSuperseded.layer;
      const supersededStructure = structureByPath.get(supersededPath);
      assert(supersededStructure, "SUPERSEDED_STRUCTURE_MISSING", "A calibrated lower replacement-stack layer is absent from the structural snapshot.", {
        frame_id: framePlan.frame_id,
        layer_path: supersededPath
      });
      assert(isSmartObject(supersededLayer), "SUPERSEDED_LAYER_MISMATCH", "A calibrated lower replacement-stack layer is no longer a smart object.", {
        frame_id: framePlan.frame_id,
        layer_path: supersededPath,
        actual_kind: String(supersededLayer.kind)
      });
      assert(supersededLayer.visible === supersededContract.expected_visible, "SUPERSEDED_VISIBILITY_MISMATCH", "A calibrated lower replacement-stack layer no longer has the expected visibility.", {
        frame_id: framePlan.frame_id,
        layer_path: supersededPath,
        expected_visible: supersededContract.expected_visible,
        actual_visible: supersededLayer.visible
      });
      assert(supersededStructure.clipping === supersededContract.expected_clipping, "SUPERSEDED_CLIPPING_MISMATCH", "A calibrated lower replacement-stack layer no longer has the expected clipping relationship.", {
        frame_id: framePlan.frame_id,
        layer_path: supersededPath,
        expected_clipping: supersededContract.expected_clipping,
        actual_clipping: supersededStructure.clipping
      });
      supersededEvidence.push({
        layer_path: supersededPath,
        layer_path_order: supersededContract.layer_path_order,
        id: supersededLayer.id,
        name: supersededLayer.name,
        kind: String(supersededLayer.kind),
        visible: supersededLayer.visible,
        clipping: supersededStructure.clipping,
        expected_visible: supersededContract.expected_visible,
        expected_clipping: supersededContract.expected_clipping,
        path_resolution: resolvedSuperseded.resolution
      });
    }

    bindings.push({
      framePlan,
      targetLayer,
      evidence: {
        frame_id: framePlan.frame_id,
        target: {
          layer_path: framePlan.target.layer_path,
          id: targetLayer.id,
          name: targetLayer.name,
          expected_audit_name: framePlan.target.audit_name,
          audit_name_match: auditNameComparison.mode,
          kind: String(targetLayer.kind),
          visible: targetLayer.visible,
          clipping: targetStructure.clipping,
          bounds: layerBounds(targetLayer),
          path_resolution: resolvedTarget.resolution
        },
        window: windowEvidence,
        superseded: supersededEvidence
      }
    });
  }

  return bindings;
}

async function selectLayer(layer) {
  await action.batchPlay([
    {
      _obj: "select",
      _target: [{ _ref: "layer", _id: layer.id }],
      makeVisible: false,
      _options: { dialogOptions: "dontDisplay" }
    }
  ], { synchronousExecution: true, modalBehavior: "execute" });
}

async function replaceSmartObjectContents(layer, sourceToken) {
  await selectLayer(layer);
  await action.batchPlay([
    {
      _obj: "placedLayerReplaceContents",
      _target: [{ _ref: "layer", _enum: "ordinal", _value: "targetEnum" }],
      null: { _path: sourceToken, _kind: "local" },
      _options: { dialogOptions: "dontDisplay" }
    }
  ], { synchronousExecution: true, modalBehavior: "execute" });
}

async function fitLayerToFrame(layer, framePlan) {
  const [frameLeft, frameTop, frameRight, frameBottom] = framePlan.target.registered_frame;
  const frameWidth = frameRight - frameLeft;
  const frameHeight = frameBottom - frameTop;
  const before = layerBounds(layer);
  const beforeWidth = before.right - before.left;
  const beforeHeight = before.bottom - before.top;
  assert(beforeWidth > 0 && beforeHeight > 0, "EMPTY_REPLACED_LAYER", "The replaced smart object has invalid bounds.", { before });

  const coverRatio = Math.max(frameWidth / beforeWidth, frameHeight / beforeHeight);
  const scalePercent = coverRatio * framePlan.transform.scale * 100;
  await layer.scale(scalePercent, scalePercent, constants.AnchorPosition.MIDDLECENTER);

  const scaled = layerBounds(layer);
  const scaledWidth = scaled.right - scaled.left;
  const scaledHeight = scaled.bottom - scaled.top;
  const focalX = clamp(Number(framePlan.crop.focal_point[0]), 0, 1);
  const focalY = clamp(Number(framePlan.crop.focal_point[1]), 0, 1);
  const targetPoint = Array.isArray(framePlan.target_point) ? framePlan.target_point : [0.5, 0.5];
  const targetX = clamp(Number(targetPoint[0]), 0, 1);
  const targetY = clamp(Number(targetPoint[1]), 0, 1);
  const offsetX = Number(framePlan.transform.offset_px[0]);
  const offsetY = Number(framePlan.transform.offset_px[1]);

  let desiredLeft = frameLeft + frameWidth * targetX - scaledWidth * focalX + offsetX;
  let desiredTop = frameTop + frameHeight * targetY - scaledHeight * focalY + offsetY;
  desiredLeft = clamp(desiredLeft, frameRight - scaledWidth, frameLeft);
  desiredTop = clamp(desiredTop, frameBottom - scaledHeight, frameTop);
  await layer.translate(desiredLeft - scaled.left, desiredTop - scaled.top);

  const fitted = layerBounds(layer);
  const tolerance = 1.5;
  assert(fitted.left <= frameLeft + tolerance && fitted.top <= frameTop + tolerance && fitted.right >= frameRight - tolerance && fitted.bottom >= frameBottom - tolerance, "FRAME_NOT_COVERED", "The transformed smart object does not cover the calibrated frame.", {
    frame: framePlan.target.registered_frame,
    fitted_bounds: fitted
  });

  return { before, scaled, fitted, scale_percent: scalePercent };
}

function documentColorProfileSnapshot(document) {
  try {
    const name = document.colorProfileName || null;
    const rawType = document.colorProfileType;
    const type = rawType === undefined || rawType === null ? null : String(rawType);
    return {
      name,
      type,
      assigned: Boolean(name) && !String(type || "").toLowerCase().includes("none")
    };
  } catch (error) {
    throw new ValidationError("COLOR_PROFILE_INSPECTION_FAILED", "Photoshop did not expose the document color-profile state after conversion.", {
      cause: error.message || String(error)
    });
  }
}

function isSrgbProfileName(value) {
  return String(value || "").toLowerCase().replace(/[^a-z0-9]/g, "").includes("srgb");
}

async function convertDocumentForJpg(document, jpgPlan) {
  const startedAt = Date.now();
  const before = documentColorProfileSnapshot(document);
  await document.convertProfile(
    jpgPlan.color_profile,
    constants.Intent.RELATIVECOLORIMETRIC,
    jpgPlan.black_point_compensation,
    jpgPlan.dither
  );
  const after = documentColorProfileSnapshot(document);
  assert(after.assigned, "JPG_COLOR_PROFILE_NOT_ASSIGNED", "Photoshop completed convertProfile without assigning a document color profile.", { before, after });
  assert(isSrgbProfileName(after.name), "JPG_COLOR_PROFILE_MISMATCH", "Photoshop did not report an sRGB document profile after conversion.", {
    expected: jpgPlan.color_profile,
    before,
    after
  });
  return {
    destination_profile: jpgPlan.color_profile,
    intent: jpgPlan.conversion_intent,
    black_point_compensation: jpgPlan.black_point_compensation,
    dither: jpgPlan.dither,
    embedding_requested: jpgPlan.embed_color_profile,
    document_profile_before: before,
    document_profile_after: after,
    conversion_ms: Date.now() - startedAt
  };
}

function exactArrayBuffer(bytes) {
  return bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength);
}

async function ensureValidSrgbProfile(jpgFile) {
  const startedAt = Date.now();
  try {
    const exportedBytes = await jpgFile.read({ format: storage.formats.binary });
    const before = inspectJpegIcc(exportedBytes);
    const tagged = ensureJpegHasSrgbProfile(exportedBytes);
    if (tagged.modified) {
      await jpgFile.write(exactArrayBuffer(tagged.jpeg), { format: storage.formats.binary });
    }

    const verifiedBytes = await jpgFile.read({ format: storage.formats.binary });
    const after = inspectJpegIcc(verifiedBytes);
    assert(after.valid_srgb, "JPG_ICC_VERIFICATION_FAILED", "The exported JPG does not contain a complete, valid sRGB ICC profile after post-processing.", {
      before,
      after,
      expected_sha256: SRGB_PROFILE_SHA256
    });

    return {
      photoshop_icc_before_postprocess: before,
      photoshop_embedded_before_postprocess: before.present,
      adapter_injected: tagged.modified,
      embedding_verified: true,
      profile_bytes: after.profile_bytes,
      profile_description: after.description,
      canonical_profile: after.canonical_srgb,
      profile_sha256: after.canonical_srgb ? SRGB_PROFILE_SHA256 : null,
      postprocess_ms: Date.now() - startedAt
    };
  } catch (error) {
    if (error instanceof ValidationError) throw error;
    throw new ValidationError("JPG_ICC_POSTPROCESS_FAILED", "The exported JPG could not be read, tagged, and verified as sRGB.", {
      cause: error.message || String(error)
    });
  }
}

async function cleanupValidationOutputs(outputFolder, knownEntries, outputNames) {
  const candidates = new Map();
  for (const entry of knownEntries.filter(Boolean)) candidates.set(entry.name, entry);

  let discoveryError = null;
  if (outputFolder) {
    try {
      for (const entry of await outputFolder.getEntries()) {
        if (Object.values(outputNames).includes(entry.name)) candidates.set(entry.name, entry);
      }
    } catch (error) {
      discoveryError = error.message || String(error);
    }
  }

  const attempts = [];
  for (const [name, entry] of candidates) {
    try {
      await entry.delete();
      attempts.push({ name, removed: true });
    } catch (error) {
      attempts.push({ name, removed: false, error: error.message || String(error) });
      console.error("Failed to remove partial validation output", name, error);
    }
  }

  let residualOutputs = [];
  let verificationError = null;
  if (outputFolder) {
    try {
      const remainingNames = (await outputFolder.getEntries()).map((entry) => entry.name);
      residualOutputs = Object.values(outputNames).filter((name) => remainingNames.includes(name));
    } catch (error) {
      verificationError = error.message || String(error);
    }
  } else if (knownEntries.some(Boolean)) {
    verificationError = "Output folder is unavailable for cleanup verification.";
  }

  return {
    confirmed: !discoveryError && !verificationError && residualOutputs.length === 0,
    attempts,
    residual_outputs: residualOutputs,
    discovery_error: discoveryError,
    verification_error: verificationError
  };
}

async function executeWholeProductPage(plan, pagePlan, pageSpec, templateFile, sourceFilesByName, jpgFile) {
  let document = null;
  let openedDocument = null;
  const attemptedFrameIds = [];
  const mutatedFrameIds = [];
  const completedFrameIds = [];
  let executionStage = "not_started";
  let activeFrameId = null;
  const pageStartedAt = Date.now();
  const sources = pagePlan.frames.map((framePlan) => {
    const sourceName = fileNameFromRelativePath(framePlan.source.relative_path);
    const file = sourceFilesByName.get(sourceName);
    assert(file, "SOURCE_SET_MISMATCH", "A required approved source file was not selected.", {
      page_id: pagePlan.page_id,
      frame_id: framePlan.frame_id,
      source_name: sourceName
    });
    return {
      frame_id: framePlan.frame_id,
      file,
      session_token: localFileSystem.createSessionToken(file),
      expected_sha256: framePlan.source.sha256
    };
  });
  const sourceByFrameId = new Map(sources.map((source) => [source.frame_id, source]));
  let openTiming = { open_modal_ms: 0, document_visibility_wait_ms: 0 };
  let openEvidence = null;
  let capturedModalFailure = null;
  let modalBoundaryError = null;
  let modalResult = null;
  let processingModalMs = 0;

  executionStage = "open_template_copy";
  try {
    const opened = await openDocumentAcrossModalBoundary(
      templateFile,
      pageSpec.templateName,
      `Open whole-product page: ${pagePlan.page_id}`
    );
    openedDocument = opened.openedDocument;
    document = opened.document;
    openTiming = opened.timing;
    openEvidence = opened.evidence;
  } catch (error) {
    const residualClose = await closeResidualValidationDocument(pageSpec.templateName);
    throw new ValidationError(error.code || "OPEN_TEMPLATE_FAILED", error.message || String(error), {
      ...(error.details || {}),
      page_id: pagePlan.page_id,
      execution_stage: executionStage,
      active_frame_id: null,
      attempted_frame_ids: [],
      mutated_frame_ids: [],
      completed_frame_ids: [],
      residual_document_close: residualClose
    });
  }

  const processingModalStartedAt = Date.now();
  try {
    modalResult = await core.executeAsModal(async (executionContext) => {
      try {
        executionStage = "validate_opened_document";
        document = assertOpenedDocument(document, pageSpec.templateName);
        executionStage = "snapshot_before";
        const canvas = assertDocumentCanvas(document, pageSpec.expectedCanvas);
        const structureBefore = await documentStructureSnapshot(document);

        executionStage = "preflight_bindings";
        executionContext.reportProgress({ value: 0.15, commandName: `Preflighting ${pagePlan.page_id}` });
        const bindings = preflightFrameBindings(document, pagePlan, structureBefore);
        const frameResults = [];
        for (let index = 0; index < bindings.length; index += 1) {
          const binding = bindings[index];
          const source = sourceByFrameId.get(binding.framePlan.frame_id);
          activeFrameId = binding.framePlan.frame_id;
          executionContext.reportProgress({
            value: 0.22 + (index / bindings.length) * 0.38,
            commandName: `Replacing ${binding.framePlan.frame_id}`
          });
          executionStage = "resolve_target_before_replace";
          const targetBeforeReplace = resolveCurrentTarget(document, binding).layer;
          attemptedFrameIds.push(binding.framePlan.frame_id);
          executionStage = "replace_smart_object_contents";
          await replaceSmartObjectContents(targetBeforeReplace, source.session_token);
          mutatedFrameIds.push(binding.framePlan.frame_id);
          executionStage = "reacquire_document_after_replace";
          document = reacquireControlledDocument(pageSpec.templateName);
          executionStage = "resolve_target_after_replace";
          const targetAfterReplace = resolveCurrentTarget(document, binding).layer;
          executionStage = "fit_replaced_layer";
          const geometry = await fitLayerToFrame(targetAfterReplace, binding.framePlan);
          completedFrameIds.push(binding.framePlan.frame_id);
          frameResults.push({
            frame_id: binding.framePlan.frame_id,
            source_name: source.file.name,
            expected_source_sha256: source.expected_sha256,
            binding: binding.evidence,
            target_after: {
              id: targetAfterReplace.id,
              name: targetAfterReplace.name,
              kind: String(targetAfterReplace.kind),
              visible: targetAfterReplace.visible,
              bounds: layerBounds(targetAfterReplace)
            },
            geometry
          });
        }

        activeFrameId = null;
        executionStage = "snapshot_after";
        executionContext.reportProgress({ value: 0.65, commandName: `Auditing ${pagePlan.page_id}` });
        const structureAfter = await documentStructureSnapshot(document);
        const structuralAudit = compareStructureSnapshots(
          structureBefore,
          structureAfter,
          pagePlan.frames.map((frame) => frame.target.layer_path)
        );
        assert(structuralAudit.passed, "LAYER_TREE_CHANGED", "The whole-product operation changed fixed layer structure, clipping, masks, or non-target geometry.", {
          page_id: pagePlan.page_id,
          ...structuralAudit
        });

        executionStage = "convert_jpg_color_profile";
        executionContext.reportProgress({ value: 0.78, commandName: `Converting ${pagePlan.page_id} to sRGB` });
        const jpgColor = await convertDocumentForJpg(document, plan.output.jpg);
        executionStage = "save_jpg";
        executionContext.reportProgress({ value: 0.9, commandName: `Exporting ${pagePlan.page_id}` });
        const jpgSaveStartedAt = Date.now();
        await document.saveAs.jpg(jpgFile, {
          quality: plan.output.jpg.quality,
          embedColorProfile: plan.output.jpg.embed_color_profile
        }, true);

        return {
          canvas: [canvas.width, canvas.height],
          frame_results: frameResults,
          structure_before: structureBefore,
          structure_after: structureAfter,
          structural_audit: structuralAudit,
          jpg_color: jpgColor,
          attempted_frame_ids: attemptedFrameIds.slice(),
          mutated_frame_ids: mutatedFrameIds.slice(),
          completed_frame_ids: completedFrameIds.slice(),
          jpg_save_ms: Date.now() - jpgSaveStartedAt
        };
      } catch (error) {
        capturedModalFailure = serializeModalValidationFailure(error);
        capturedModalFailure.modal_validation_failure.details = {
          ...(capturedModalFailure.modal_validation_failure.details || {}),
          page_id: pagePlan.page_id,
          execution_stage: executionStage,
          active_frame_id: activeFrameId,
          attempted_frame_ids: attemptedFrameIds.slice(),
          mutated_frame_ids: mutatedFrameIds.slice(),
          completed_frame_ids: completedFrameIds.slice()
        };
        return null;
      } finally {
        let currentDocument = null;
        let lookupError = null;
        try {
          currentDocument = reacquireControlledDocument(pageSpec.templateName, false);
        } catch (error) {
          lookupError = error;
        }
        const fallbackDocument = safeDocumentName(document) === pageSpec.templateName
          ? document
          : (safeDocumentName(openedDocument) === pageSpec.templateName ? openedDocument : null);
        try {
          const documentToClose = currentDocument || fallbackDocument;
          if (documentToClose) await documentToClose.closeWithoutSaving();
          else if (lookupError) throw lookupError;
        } finally {
          document = null;
          openedDocument = null;
        }
      }
    }, { commandName: `UXP whole-product validation: ${pagePlan.page_id}` });
  } catch (error) {
    modalBoundaryError = error;
  }
  processingModalMs = Date.now() - processingModalStartedAt;

  if (capturedModalFailure) {
    const residualClose = await closeResidualValidationDocument(pageSpec.templateName);
    capturedModalFailure.modal_validation_failure.details = {
      ...(capturedModalFailure.modal_validation_failure.details || {}),
      residual_document_close: residualClose
    };
    restoreModalValidationFailure(attachModalBoundaryError(capturedModalFailure, modalBoundaryError));
  }
  if (modalBoundaryError) {
    modalBoundaryError.residual_document_close = await closeResidualValidationDocument(pageSpec.templateName);
    throw modalBoundaryError;
  }
  assert(modalResult, "WHOLE_PRODUCT_PAGE_FAILED", "Photoshop did not return a result for the whole-product page.", { page_id: pagePlan.page_id });
  const jpgIcc = await ensureValidSrgbProfile(jpgFile);
  return {
    page_id: pagePlan.page_id,
    template_copy_name: templateFile.name,
    template_copy_path: templateFile.nativePath,
    expected_template_sha256: pagePlan.template_sha256,
    open: openEvidence,
    frame_ids: pagePlan.frames.map((frame) => frame.frame_id),
    sources: sources.map((source) => ({
      frame_id: source.frame_id,
      source_name: source.file.name,
      source_path: source.file.nativePath,
      expected_source_sha256: source.expected_sha256
    })),
    output: {
      jpg_name: jpgFile.name,
      jpg_path: jpgFile.nativePath,
      jpg_profile: jpgIcc,
      source_template_saved: false
    },
    timing: {
      photoshop_modal_ms: openTiming.open_modal_ms + processingModalMs,
      open_modal_ms: openTiming.open_modal_ms,
      document_visibility_wait_ms: openTiming.document_visibility_wait_ms,
      processing_modal_ms: processingModalMs,
      total_ms: Date.now() - pageStartedAt,
      color_conversion_ms: modalResult.jpg_color.conversion_ms,
      jpg_save_ms: modalResult.jpg_save_ms,
      jpg_icc_postprocess_ms: jpgIcc.postprocess_ms
    },
    photoshop: modalResult
  };
}

async function executeWholeProductPlan() {
  let outputFolder = null;
  let outputNames = {};
  let evidenceFile = null;
  const createdOutputFiles = [];
  const completedPageResults = [];
  let activePageId = null;
  const startedAt = Date.now();

  try {
    const spec = assertRenderPlan(loadedPlan);
    const selected = await selectWholeProductEntries(loadedPlan, spec);
    outputFolder = selected.outputFolder;
    outputNames = selected.outputNames;

    for (let index = 0; index < loadedPlan.pages.length; index += 1) {
      const pagePlan = loadedPlan.pages[index];
      const pageSpec = spec.pages[pagePlan.page_id];
      activePageId = pagePlan.page_id;
      showStatus(`Rendering page ${index + 1} of ${loadedPlan.pages.length}`, {
        page_id: pagePlan.page_id,
        completed_page_ids: completedPageResults.map((page) => page.page_id)
      });
      const jpgFile = await outputFolder.createFile(pageSpec.outputName, { overwrite: false });
      createdOutputFiles.push(jpgFile);
      const pageResult = await executeWholeProductPage(
        loadedPlan,
        pagePlan,
        pageSpec,
        selected.templateFilesByPageId.get(pagePlan.page_id),
        selected.sourceFilesByName,
        jpgFile
      );
      completedPageResults.push(pageResult);

      const controlledFailure = loadedPlan.validation.controlled_failure;
      if (controlledFailure && completedPageResults.length === controlledFailure.completed_page_count) {
        activePageId = controlledFailure.next_page_id;
        throw new ValidationError(controlledFailure.error_code, "TV-13 reached the approved partial-output checkpoint and stopped intentionally.", {
          checkpoint: controlledFailure.checkpoint,
          completed_page_count: completedPageResults.length,
          next_page_id: controlledFailure.next_page_id,
          next_frame_id: controlledFailure.next_frame_id
        });
      }
    }

    activePageId = null;
    const evidence = {
      ok: true,
      schema: "uxp-whole-product-validation-evidence/v1",
      validation_mode: "whole_product_copy",
      job_id: loadedPlan.job_id,
      product_id: "A馨妈6015-58",
      plugin_version: PLUGIN_VERSION,
      photoshop_version: app.version || null,
      recorded_at: new Date().toISOString(),
      input_policy: loadedPlan.input_policy,
      output_policy: loadedPlan.output,
      page_count: completedPageResults.length,
      frame_count: completedPageResults.reduce((total, page) => total + page.frame_ids.length, 0),
      page_results: completedPageResults,
      prior_controlled_failure: lastControlledFailureEvidence,
      output: {
        jpg_count: completedPageResults.length,
        jpg_paths: completedPageResults.map((page) => page.output.jpg_path),
        source_templates_saved: false,
        all_fixed_structures_unchanged: completedPageResults.every((page) => page.photoshop.structural_audit.passed),
        all_jpg_profiles_verified: completedPageResults.every((page) => page.output.jpg_profile.embedding_verified)
      },
      timing: {
        total_ms: Date.now() - startedAt,
        photoshop_modal_ms: completedPageResults.reduce((total, page) => total + page.timing.photoshop_modal_ms, 0),
        color_conversion_ms: completedPageResults.reduce((total, page) => total + page.timing.color_conversion_ms, 0),
        jpg_save_ms: completedPageResults.reduce((total, page) => total + page.timing.jpg_save_ms, 0),
        jpg_icc_postprocess_ms: completedPageResults.reduce((total, page) => total + page.timing.jpg_icc_postprocess_ms, 0)
      },
      hash_verification: "Fixture input and output hashes are verified outside UXP before and after the run."
    };

    evidenceFile = await outputFolder.createFile(spec.evidenceName, { overwrite: false });
    await evidenceFile.write(JSON.stringify(evidence, null, 2));
    createdOutputFiles.push(evidenceFile);
    await writePluginEvidence("uxp-validation-last-run.json", evidence);
    lastControlledFailureEvidence = null;
    showStatus("Whole-product copy test completed", evidence);
  } catch (error) {
    const cleanup = await cleanupValidationOutputs(outputFolder, createdOutputFiles, outputNames);
    const result = errorResult(error, {
      job_id: loadedPlan ? loadedPlan.job_id : null,
      active_page_id: activePageId,
      completed_page_ids_before_failure: completedPageResults.map((page) => page.page_id),
      failed_after_partial_output: completedPageResults.length > 0 || createdOutputFiles.length > 0,
      partial_outputs_removed: cleanup.confirmed,
      output_cleanup: cleanup,
      source_templates_saved: false,
      elapsed_ms: Date.now() - startedAt
    });
    if (error.code === "TV13_CONTROLLED_FAILURE") {
      lastControlledFailureEvidence = result;
    }
    try {
      await writePluginEvidence("uxp-validation-last-run.json", result);
      if (error.code === "TV13_CONTROLLED_FAILURE") {
        await writePluginEvidence("uxp-validation-tv13-failure.json", result);
      }
    } catch (evidenceError) {
      console.error("Failed to write whole-product failure evidence", evidenceError);
    }
    const status = error.code === "USER_CANCELLED"
      ? "Test canceled safely"
      : (error.code === "TV13_CONTROLLED_FAILURE" ? "TV-13 controlled failure cleaned safely" : "Whole-product test stopped safely");
    showStatus(status, result);
  }
}

async function executeLoadedPlan() {
  if (loadedPlan && loadedPlan.validation && loadedPlan.validation.mode === "whole_product_copy") {
    await executeWholeProductPlan();
    return;
  }
  if (!loadedPlan) {
    showStatus("No plan loaded", structuredError("PLAN_NOT_LOADED", "Load a controlled RenderPlan before execution."));
    return;
  }

  let document = null;
  let openedDocument = null;
  let psdFile = null;
  let jpgFile = null;
  let evidenceFile = null;
  let outputFolder = null;
  let spec = null;
  const attemptedFrameIds = [];
  const mutatedFrameIds = [];
  const completedFrameIds = [];
  let executionStage = "not_started";
  let activeFrameId = null;
  const startedAt = Date.now();

  try {
    spec = assertRenderPlan(loadedPlan);
    const pagePlan = loadedPlan.pages[0];
    const selectedEntries = await selectValidationEntries(loadedPlan, spec);
    const { templateFile, sourceFilesByFrameId } = selectedEntries;
    outputFolder = selectedEntries.outputFolder;
    const sources = pagePlan.frames.map((framePlan) => {
      const file = sourceFilesByFrameId.get(framePlan.frame_id);
      assert(file, "SOURCE_SET_MISMATCH", "A required approved source file was not selected.", { frame_id: framePlan.frame_id });
      return {
        frame_id: framePlan.frame_id,
        file,
        session_token: localFileSystem.createSessionToken(file),
        expected_sha256: framePlan.source.sha256
      };
    });
    const sourceByFrameId = new Map(sources.map((source) => [source.frame_id, source]));

    psdFile = await outputFolder.createFile(spec.outputNames.psd, { overwrite: false });
    jpgFile = await outputFolder.createFile(spec.outputNames.jpg, { overwrite: false });
    const modalStartedAt = Date.now();

    let capturedModalFailure = null;
    let modalBoundaryError = null;
    let modalResult = null;
    try {
      modalResult = await core.executeAsModal(async (executionContext) => {
        try {
        executionStage = "open_template_copy";
        executionContext.reportProgress({ value: 0.05, commandName: "Opening PSD copy" });
        openedDocument = await app.open(templateFile);
        document = assertOpenedDocument(openedDocument, spec.templateName);
        executionStage = "snapshot_before";
        const canvas = assertDocumentCanvas(document, spec.expectedCanvas);
        const topLevelLayers = layerSnapshot(document.layers);
        const structureBefore = await documentStructureSnapshot(document);

        executionContext.reportProgress({ value: 0.12, commandName: "Preflighting all calibrated bindings" });
        executionStage = "preflight_bindings";
        const bindings = preflightFrameBindings(document, pagePlan, structureBefore);
        const frameResults = [];
        for (let index = 0; index < bindings.length; index += 1) {
          const binding = bindings[index];
          const source = sourceByFrameId.get(binding.framePlan.frame_id);
          activeFrameId = binding.framePlan.frame_id;
          executionContext.reportProgress({
            value: 0.2 + (index / bindings.length) * 0.38,
            commandName: `Replacing ${binding.framePlan.frame_id}`
          });
          executionStage = "resolve_target_before_replace";
          const targetBeforeReplace = resolveCurrentTarget(document, binding).layer;
          attemptedFrameIds.push(binding.framePlan.frame_id);
          executionStage = "replace_smart_object_contents";
          await replaceSmartObjectContents(targetBeforeReplace, source.session_token);
          mutatedFrameIds.push(binding.framePlan.frame_id);
          executionStage = "reacquire_document_after_replace";
          document = reacquireControlledDocument(spec.templateName);
          executionStage = "resolve_target_after_replace";
          const targetAfterReplace = resolveCurrentTarget(document, binding).layer;
          executionStage = "fit_replaced_layer";
          const geometry = await fitLayerToFrame(targetAfterReplace, binding.framePlan);
          completedFrameIds.push(binding.framePlan.frame_id);
          frameResults.push({
            frame_id: binding.framePlan.frame_id,
            source_name: source.file.name,
            expected_source_sha256: source.expected_sha256,
            binding: binding.evidence,
            target_after: {
              id: targetAfterReplace.id,
              name: targetAfterReplace.name,
              kind: String(targetAfterReplace.kind),
              visible: targetAfterReplace.visible,
              bounds: layerBounds(targetAfterReplace)
            },
            geometry
          });
        }

        activeFrameId = null;
        executionStage = "snapshot_after";
        executionContext.reportProgress({ value: 0.6, commandName: "Auditing fixed layers and clipping structure" });
        const structureAfter = await documentStructureSnapshot(document);
        const structuralAudit = compareStructureSnapshots(
          structureBefore,
          structureAfter,
          pagePlan.frames.map((frame) => frame.target.layer_path)
        );
        assert(structuralAudit.passed, "LAYER_TREE_CHANGED", "The multi-frame operation changed fixed layer structure, clipping, masks, or non-target geometry.", structuralAudit);

        executionStage = "save_editable_psd";
        executionContext.reportProgress({ value: 0.65, commandName: "Saving editable PSD copy" });
        const psdSaveStartedAt = Date.now();
        await document.saveAs.psd(psdFile, { embedColorProfile: true }, true);
        const psdSaveMs = Date.now() - psdSaveStartedAt;

        executionStage = "convert_jpg_color_profile";
        executionContext.reportProgress({ value: 0.78, commandName: "Converting JPG copy to sRGB" });
        const jpgColor = await convertDocumentForJpg(document, loadedPlan.output.jpg);

        executionStage = "save_jpg";
        executionContext.reportProgress({ value: 0.88, commandName: "Exporting sRGB JPG copy" });
        const jpgSaveStartedAt = Date.now();
        await document.saveAs.jpg(jpgFile, {
          quality: loadedPlan.output.jpg.quality,
          embedColorProfile: loadedPlan.output.jpg.embed_color_profile
        }, true);
        const jpgSaveMs = Date.now() - jpgSaveStartedAt;

        return {
          canvas: [canvas.width, canvas.height],
          frame_results: frameResults,
          structure_before: structureBefore,
          structure_after: structureAfter,
          structural_audit: structuralAudit,
          top_level_layers: topLevelLayers,
          jpg_color: jpgColor,
          attempted_frame_ids: attemptedFrameIds.slice(),
          mutated_frame_ids: mutatedFrameIds.slice(),
          completed_frame_ids: completedFrameIds.slice(),
          psd_save_ms: psdSaveMs,
          jpg_save_ms: jpgSaveMs
        };
        } catch (error) {
          capturedModalFailure = serializeModalValidationFailure(error);
          capturedModalFailure.modal_validation_failure.details = {
            ...(capturedModalFailure.modal_validation_failure.details || {}),
            execution_stage: executionStage,
            active_frame_id: activeFrameId,
            attempted_frame_ids: attemptedFrameIds.slice(),
            mutated_frame_ids: mutatedFrameIds.slice(),
            completed_frame_ids: completedFrameIds.slice()
          };
          return null;
        } finally {
          const currentDocument = reacquireControlledDocument(spec.templateName, false);
          const fallbackDocument = safeDocumentName(document) === spec.templateName
            ? document
            : (safeDocumentName(openedDocument) === spec.templateName ? openedDocument : null);
          try {
            const documentToClose = currentDocument || fallbackDocument;
            if (documentToClose) await documentToClose.closeWithoutSaving();
          } finally {
            document = null;
            openedDocument = null;
          }
        }
      }, { commandName: loadedPlan.validation.mode === "multi_frame_copy" ? "UXP multi-frame copy validation" : "UXP single-frame copy validation" });
    } catch (error) {
      modalBoundaryError = error;
    }
    if (capturedModalFailure) {
      restoreModalValidationFailure(attachModalBoundaryError(capturedModalFailure, modalBoundaryError));
    }
    if (modalBoundaryError) throw modalBoundaryError;
    const photoshopResult = modalResult;

    const modalElapsedMs = Date.now() - modalStartedAt;
    const jpgIcc = await ensureValidSrgbProfile(jpgFile);
    const evidenceSchema = loadedPlan.validation.mode === "multi_frame_copy"
      ? "uxp-multi-frame-validation-evidence/v1"
      : "uxp-validation-evidence/v2";

    const evidence = {
      ok: true,
      schema: evidenceSchema,
      validation_mode: loadedPlan.validation.mode,
      job_id: loadedPlan.job_id,
      page_id: pagePlan.page_id,
      frame_ids: pagePlan.frames.map((frame) => frame.frame_id),
      plugin_version: PLUGIN_VERSION,
      photoshop_version: app.version || null,
      recorded_at: new Date().toISOString(),
      input: {
        template_copy_name: templateFile.name,
        template_copy_path: templateFile.nativePath,
        expected_template_sha256: pagePlan.template_sha256,
        sources: sources.map((source) => ({
          frame_id: source.frame_id,
          source_name: source.file.name,
          source_path: source.file.nativePath,
          expected_source_sha256: source.expected_sha256
        })),
        hash_verification: "Fixture hashes are verified outside UXP before and after the run."
      },
      bindings: pagePlan.frames.map((frame) => ({
        frame_id: frame.frame_id,
        layer_path: frame.target.layer_path,
        layer_path_order: frame.target.layer_path_order,
        expected_kind: frame.target.expected_kind,
        expected_visible: frame.target.expected_visible,
        expected_clipping: frame.target.expected_clipping,
        audit_name: frame.target.audit_name,
        registered_frame: frame.target.registered_frame,
        window: frame.target.window || null,
        superseded_paths: frame.target.superseded_paths || [],
        binding_evidence: frame.target.binding_evidence
      })),
      output: {
        psd_path: psdFile.nativePath,
        jpg_path: jpgFile.nativePath,
        jpg_color_profile: photoshopResult.jpg_color.destination_profile,
        jpg_profile: {
          embedding_requested: photoshopResult.jpg_color.embedding_requested,
          photoshop_embedded_before_postprocess: jpgIcc.photoshop_embedded_before_postprocess,
          adapter_injected: jpgIcc.adapter_injected,
          embedding_verified: jpgIcc.embedding_verified,
          profile_bytes: jpgIcc.profile_bytes,
          profile_description: jpgIcc.profile_description,
          canonical_profile: jpgIcc.canonical_profile,
          profile_sha256: jpgIcc.profile_sha256
        },
        source_template_saved: false
      },
      timing: {
        photoshop_modal_ms: modalElapsedMs,
        total_ms: Date.now() - startedAt,
        color_conversion_ms: photoshopResult.jpg_color.conversion_ms,
        psd_save_ms: photoshopResult.psd_save_ms,
        jpg_save_ms: photoshopResult.jpg_save_ms,
        jpg_icc_postprocess_ms: jpgIcc.postprocess_ms
      },
      photoshop: photoshopResult,
      jpg_icc: jpgIcc
    };

    evidenceFile = await outputFolder.createFile(spec.outputNames.evidence, { overwrite: false });
    await evidenceFile.write(JSON.stringify(evidence, null, 2));
    await writePluginEvidence("uxp-validation-last-run.json", evidence);
    showStatus(loadedPlan.validation.mode === "multi_frame_copy" ? "Multi-frame copy test completed" : "Single-frame copy test completed", evidence);
  } catch (error) {
    if (document) {
      try {
        await core.executeAsModal(async () => {
          await document.closeWithoutSaving();
        }, { commandName: "Close failed validation copy" });
      } catch (closeError) {
        console.error("Failed to close validation document without saving", closeError);
      }
      document = null;
    }
    const cleanup = await cleanupValidationOutputs(
      outputFolder,
      [evidenceFile, jpgFile, psdFile],
      spec ? spec.outputNames : {}
    );

    const result = errorResult(error, {
      job_id: loadedPlan ? loadedPlan.job_id : null,
      page_id: loadedPlan ? loadedPlan.pages[0].page_id : null,
      frame_ids: loadedPlan ? loadedPlan.pages[0].frames.map((frame) => frame.frame_id) : [],
      attempted_frame_ids_before_failure: attemptedFrameIds,
      mutated_frame_ids_before_failure: mutatedFrameIds,
      completed_frame_ids_before_failure: completedFrameIds,
      execution_stage: executionStage,
      active_frame_id: activeFrameId,
      failed_after_partial_mutation: attemptedFrameIds.length > 0,
      partial_outputs_removed: cleanup.confirmed,
      output_cleanup: cleanup,
      source_template_saved: false,
      elapsed_ms: Date.now() - startedAt
    });
    try {
      await writePluginEvidence("uxp-validation-last-run.json", result);
    } catch (evidenceError) {
      console.error("Failed to write plugin failure evidence", evidenceError);
    }
    showStatus(error.code === "USER_CANCELLED" ? "Test canceled safely" : "RenderPlan test stopped safely", result);
  }
}

document.getElementById("probe").addEventListener("click", recordPhotoshopProbe);
document.getElementById("handshake").addEventListener("click", handshakeWithDesktop);
document.getElementById("load-plan").addEventListener("click", loadRenderPlan);
executeButton.addEventListener("click", executeLoadedPlan);
