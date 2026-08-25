const fs = require("fs");
const path = require("path");
const vm = require("vm");

const root = path.resolve(__dirname, "..");
const mainPath = path.join(root, "uxp-validation-plugin", "main.js");
const singlePlanPath = path.join(root, "uxp-validation-plugin", "render-plans", "single-frame-g03-render-plan.json");
const multiPlanPath = path.join(root, "uxp-validation-plugin", "render-plans", "multi-frame-g05-render-plan.json");
const wholeProductPlanPath = path.join(root, "uxp-validation-plugin", "render-plans", "whole-product-a6015-render-plan.json");
const tv13FailurePlanPath = path.join(root, "uxp-validation-plugin", "render-plans", "tv13-controlled-failure-render-plan.json");
const elements = new Map();

function element(id) {
  if (!elements.has(id)) {
    elements.set(id, {
      id,
      disabled: true,
      textContent: "",
      addEventListener() {}
    });
  }
  return elements.get(id);
}

const photoshopApp = { version: "test", documents: [], activeDocument: null };
const photoshopAction = {};
const photoshopCore = {};
const uxpLocalFileSystem = {};
const sandbox = {
  console,
  fetch: async () => { throw new Error("fetch is not available in unit tests"); },
  setTimeout,
  clearTimeout,
  Map,
  Set,
  Date,
  JSON,
  Number,
  Math,
  Array,
  String,
  Boolean,
  Error,
  Uint8Array,
  ArrayBuffer,
  document: { getElementById: element },
  require(moduleName) {
    if (moduleName === "photoshop") {
      return {
        app: photoshopApp,
        action: photoshopAction,
        core: photoshopCore,
        constants: { LayerKind: { SMARTOBJECT: "smartObject" } }
      };
    }
    if (moduleName === "uxp") {
      return { storage: { localFileSystem: uxpLocalFileSystem, formats: { binary: "binary" } } };
    }
    if (moduleName === "./jpeg-icc") {
      return {
        SRGB_PROFILE_SHA256: "test",
        inspectJpegIcc() {},
        ensureJpegHasSrgbProfile() {}
      };
    }
    throw new Error(`Unexpected module: ${moduleName}`);
  }
};

const source = fs.readFileSync(mainPath, "utf8") + `
globalThis.__testExports = {
  assertRenderPlan,
  resolveLayerPath,
  compareStructureSnapshots,
  compareAuditNames,
  findOpenDocumentByName,
  assertOpenedDocument,
  reacquireControlledDocument,
  serializeModalValidationFailure,
  restoreModalValidationFailure,
  attachModalBoundaryError,
  inspectProperty,
  inspectDocumentReference,
  createPhotoshopOpenDescriptor,
  openDocumentAcrossModalBoundary,
  ValidationError
};`;
vm.runInNewContext(source, sandbox, { filename: mainPath });
const {
  assertRenderPlan,
  resolveLayerPath,
  compareStructureSnapshots,
  compareAuditNames,
  findOpenDocumentByName,
  assertOpenedDocument,
  reacquireControlledDocument,
  serializeModalValidationFailure,
  restoreModalValidationFailure,
  attachModalBoundaryError,
  inspectProperty,
  inspectDocumentReference,
  createPhotoshopOpenDescriptor,
  openDocumentAcrossModalBoundary,
  ValidationError
} = sandbox.__testExports;

const wholePageFunctionStart = source.indexOf("async function executeWholeProductPage");
const wholePlanFunctionStart = source.indexOf("async function executeWholeProductPlan", wholePageFunctionStart);
const wholePageFunctionSource = source.slice(wholePageFunctionStart, wholePlanFunctionStart);
const openHelperFunctionStart = source.indexOf("async function openDocumentAcrossModalBoundary");
const reacquireFunctionStart = source.indexOf("function reacquireControlledDocument", openHelperFunctionStart);
const openHelperFunctionSource = source.slice(openHelperFunctionStart, reacquireFunctionStart);
expect(wholePageFunctionStart >= 0 && wholePlanFunctionStart > wholePageFunctionStart, "Whole-product page execution function must be present.");
expect(wholePageFunctionSource.includes("await openDocumentAcrossModalBoundary("), "Whole-product pages must use the dedicated open modal boundary.");
expect(!wholePageFunctionSource.includes("await app.open("), "Whole-product processing must not open the PSD inside its processing modal.");
expect(wholePageFunctionSource.includes("open: openEvidence"), "Whole-product page evidence must include the safe open-strategy summary.");
expect(openHelperFunctionStart >= 0 && reacquireFunctionStart > openHelperFunctionStart, "The Action Manager open helper must be present.");
expect(openHelperFunctionSource.includes("localFileSystem.createSessionToken(entry)"), "Whole-product opening must create a session token for the selected template entry.");
expect(openHelperFunctionSource.includes("action.batchPlay([descriptor]"), "Whole-product opening must use Action Manager batchPlay.");
expect(!openHelperFunctionSource.includes("app.open("), "The whole-product open helper must not fall back to app.open.");
expect(wholePageFunctionSource.includes("document = assertOpenedDocument(document, pageSpec.templateName);"), "The processing modal must use the document resolved outside the open modal.");
expect(
  wholePageFunctionSource.indexOf("await openDocumentAcrossModalBoundary(") < wholePageFunctionSource.indexOf("modalResult = await core.executeAsModal("),
  "The open modal must complete before the processing modal begins."
);

function loadJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, "utf8"));
}

function clone(value) {
  return JSON.parse(JSON.stringify(value));
}

function expect(condition, message) {
  if (!condition) throw new Error(message);
}

function expectCode(code, operation) {
  let thrown = null;
  try {
    operation();
  } catch (error) {
    thrown = error;
  }
  expect(thrown && thrown.code === code, `Expected ${code}, received ${thrown ? thrown.code || thrown.message : "no error"}`);
}

const descriptorToken = "test-open-session-token";
const openDescriptor = createPhotoshopOpenDescriptor(descriptorToken);
expect(openDescriptor._obj === "open", "The Action Manager descriptor must use the open event.");
expect(openDescriptor.target && openDescriptor.target._path === descriptorToken, "The open descriptor target path must contain the session token.");
expect(openDescriptor.target._kind === "local", "The open descriptor target must be a local session token.");
expect(openDescriptor._options && openDescriptor._options.dialogOptions === "silent", "The open descriptor must use Photoshop's silent dialog mode.");
expect(Object.keys(openDescriptor).sort().join(",") === "_obj,_options,target", "The open descriptor must contain only the approved top-level fields.");
expect(Object.keys(openDescriptor.target).sort().join(",") === "_kind,_path", "The open descriptor target must contain only the local token fields.");
expect(Object.keys(openDescriptor._options).join(",") === "dialogOptions", "The open descriptor options must contain only dialogOptions.");
expect(!Object.prototype.hasOwnProperty.call(openDescriptor, "null"), "The open descriptor must not use the legacy null field.");
expect(!Object.prototype.hasOwnProperty.call(openDescriptor, "_target"), "The open descriptor must not use an Action Manager _target reference.");

const singlePlan = loadJson(singlePlanPath);
const multiPlan = loadJson(multiPlanPath);
const wholeProductPlan = loadJson(wholeProductPlanPath);
const tv13FailurePlan = loadJson(tv13FailurePlanPath);
expect(assertRenderPlan(singlePlan).frameCount === 1, "Single-frame plan was not accepted.");
expect(assertRenderPlan(multiPlan).frameCount === 4, "Multi-frame plan was not accepted.");
const wholeProductSpec = assertRenderPlan(wholeProductPlan);
expect(wholeProductSpec.pageCount === 6, "Whole-product plan must contain six approved pages.");
expect(wholeProductSpec.frameCount === 11, "Whole-product plan must contain eleven approved frames.");
expect(assertRenderPlan(tv13FailurePlan).frameCount === 11, "The TV-13 controlled failure plan must preserve the approved six-page fixture.");
expect(tv13FailurePlan.validation.controlled_failure.completed_page_count === 2, "TV-13 must stop after two completed page outputs.");
expect(tv13FailurePlan.validation.controlled_failure.next_frame_id === "G-详情-04.frame-4", "TV-13 must identify the first unstarted frame.");
expect(wholeProductPlan.output.jpg.enabled === true, "Whole-product plan must enable JPG output.");
expect(wholeProductPlan.output.editable_psd.enabled === false, "Whole-product plan must represent the default JPG-only workflow.");
expect(wholeProductPlan.pages[0].frames[0].target.expected_clipping === false, "G-详情-01 front_main must preserve its actual non-clipping structure.");
expect(wholeProductPlan.pages[0].frames[0].target.superseded_layers[0].expected_clipping === false, "G-详情-01 lower replacement-stack layer must preserve its actual non-clipping structure.");
const wholeSupersededContracts = wholeProductPlan.pages.flatMap((page) => page.frames.flatMap((frame) => frame.target.superseded_layers));
expect(wholeSupersededContracts.length === 14, "Whole-product plan must carry fourteen explicit superseded-layer contracts.");
expect(wholeSupersededContracts.filter((contract) => contract.expected_clipping === false).length === 1, "Only the calibrated G-详情-01 lower layer may be non-clipping.");

const generatedPlan = clone(multiPlan);
generatedPlan.input_policy.allow_generated_imagery = true;
expectCode("INVALID_RENDER_PLAN", () => assertRenderPlan(generatedPlan));

const missingFramePlan = clone(multiPlan);
missingFramePlan.pages[0].frames.pop();
expectCode("INVALID_RENDER_PLAN", () => assertRenderPlan(missingFramePlan));

const wrongPathPlan = clone(multiPlan);
wrongPathPlan.pages[0].frames[0].target.layer_path = "2";
expectCode("INVALID_RENDER_PLAN", () => assertRenderPlan(wrongPathPlan));

const wrongClippingPlan = clone(multiPlan);
wrongClippingPlan.pages[0].frames[1].target.expected_clipping = false;
expectCode("INVALID_RENDER_PLAN", () => assertRenderPlan(wrongClippingPlan));

const wrongSourcePlan = clone(multiPlan);
wrongSourcePlan.pages[0].frames[2].source.relative_path = "fixture/source/not-approved.jpg";
expectCode("INVALID_RENDER_PLAN", () => assertRenderPlan(wrongSourcePlan));

const wholeMissingPage = clone(wholeProductPlan);
wholeMissingPage.pages.pop();
expectCode("INVALID_RENDER_PLAN", () => assertRenderPlan(wholeMissingPage));

const wholeDuplicatePage = clone(wholeProductPlan);
wholeDuplicatePage.pages[1].page_id = wholeDuplicatePage.pages[0].page_id;
expectCode("INVALID_RENDER_PLAN", () => assertRenderPlan(wholeDuplicatePage));

const wholeDuplicateFrame = clone(wholeProductPlan);
wholeDuplicateFrame.pages[5].frames[0].frame_id = wholeDuplicateFrame.pages[0].frames[0].frame_id;
expectCode("INVALID_RENDER_PLAN", () => assertRenderPlan(wholeDuplicateFrame));

const wholeWrongTemplateHash = clone(wholeProductPlan);
wholeWrongTemplateHash.pages[0].template_sha256 = "0".repeat(64);
expectCode("INVALID_RENDER_PLAN", () => assertRenderPlan(wholeWrongTemplateHash));

const wholeWrongSourceHash = clone(wholeProductPlan);
wholeWrongSourceHash.pages[0].frames[0].source.sha256 = "0".repeat(64);
expectCode("INVALID_RENDER_PLAN", () => assertRenderPlan(wholeWrongSourceHash));

const wholeWrongLayerPath = clone(wholeProductPlan);
wholeWrongLayerPath.pages[3].frames[0].target.layer_path = "2";
expectCode("INVALID_RENDER_PLAN", () => assertRenderPlan(wholeWrongLayerPath));

const wholeWrongG01Clipping = clone(wholeProductPlan);
wholeWrongG01Clipping.pages[0].frames[0].target.expected_clipping = true;
expectCode("INVALID_RENDER_PLAN", () => assertRenderPlan(wholeWrongG01Clipping));

const wholeWrongWindowPath = clone(wholeProductPlan);
wholeWrongWindowPath.pages[3].frames[0].target.window.layer_path = "2";
expectCode("INVALID_RENDER_PLAN", () => assertRenderPlan(wholeWrongWindowPath));

const wholeWrongSupersededPath = clone(wholeProductPlan);
wholeWrongSupersededPath.pages[3].frames[0].target.superseded_layers[0].layer_path = "4";
expectCode("INVALID_RENDER_PLAN", () => assertRenderPlan(wholeWrongSupersededPath));

const wholeWrongSupersededClipping = clone(wholeProductPlan);
wholeWrongSupersededClipping.pages[0].frames[0].target.superseded_layers[0].expected_clipping = true;
expectCode("INVALID_RENDER_PLAN", () => assertRenderPlan(wholeWrongSupersededClipping));

const wholeLegacySupersededPaths = clone(wholeProductPlan);
delete wholeLegacySupersededPaths.pages[0].frames[0].target.superseded_layers;
wholeLegacySupersededPaths.pages[0].frames[0].target.superseded_paths = ["2"];
expectCode("INVALID_RENDER_PLAN", () => assertRenderPlan(wholeLegacySupersededPaths));

const wholeGeneratedImagery = clone(wholeProductPlan);
wholeGeneratedImagery.input_policy.allow_generated_imagery = true;
expectCode("INVALID_RENDER_PLAN", () => assertRenderPlan(wholeGeneratedImagery));

const wholeEditablePsd = clone(wholeProductPlan);
wholeEditablePsd.output.editable_psd.enabled = true;
expectCode("INVALID_RENDER_PLAN", () => assertRenderPlan(wholeEditablePsd));

const wholeWrongTargetPoint = clone(wholeProductPlan);
wholeWrongTargetPoint.pages[0].frames[0].target_point = [null, 0.5];
expectCode("INVALID_RENDER_PLAN", () => assertRenderPlan(wholeWrongTargetPoint));

const wholeWrongQuality = clone(wholeProductPlan);
wholeWrongQuality.output.jpg.quality = 8;
expectCode("INVALID_RENDER_PLAN", () => assertRenderPlan(wholeWrongQuality));

const tv13WrongCheckpoint = clone(tv13FailurePlan);
tv13WrongCheckpoint.validation.controlled_failure.completed_page_count = 1;
expectCode("INVALID_RENDER_PLAN", () => assertRenderPlan(tv13WrongCheckpoint));

const tv13WrongCode = clone(tv13FailurePlan);
tv13WrongCode.validation.controlled_failure.error_code = "ARBITRARY_FAILURE";
expectCode("INVALID_RENDER_PLAN", () => assertRenderPlan(tv13WrongCode));

const tv13ExtraField = clone(tv13FailurePlan);
tv13ExtraField.validation.controlled_failure.arbitrary = true;
expectCode("INVALID_RENDER_PLAN", () => assertRenderPlan(tv13ExtraField));

const wholeWrongWindowKind = clone(wholeProductPlan);
wholeWrongWindowKind.pages[3].frames[0].target.window.expected_kind = "smartObject";
expectCode("INVALID_RENDER_PLAN", () => assertRenderPlan(wholeWrongWindowKind));

const wholeWrongPageOrder = clone(wholeProductPlan);
[wholeWrongPageOrder.pages[0], wholeWrongPageOrder.pages[1]] = [wholeWrongPageOrder.pages[1], wholeWrongPageOrder.pages[0]];
expectCode("INVALID_RENDER_PLAN", () => assertRenderPlan(wholeWrongPageOrder));

const topToBottom = Array.from({ length: 18 }, (_, index) => ({ id: 100 + index, name: `layer-${index}`, kind: "pixel", layers: [] }));
const resolved = resolveLayerPath({ layers: topToBottom }, "3", "psd_bottom_to_top");
expect(resolved.layer === topToBottom[14], "PSD bottom-to-top path 3 was not converted to UXP index 14.");
expectCode("INVALID_LAYER_PATH_ORDER", () => resolveLayerPath({ layers: topToBottom }, "3", "uxp_top_to_bottom"));

const before = [
  { layer_path: "1", id: 1, name: "window", kind: "pixel", visible: true, clipping: false, has_user_mask: false, has_vector_mask: false, opacity: 100, blend_mode: "normal", bounds: { left: 0, top: 0, right: 10, bottom: 10 } },
  { layer_path: "3", id: 3, name: "target", kind: "smartObject", visible: true, clipping: true, has_user_mask: false, has_vector_mask: false, opacity: 100, blend_mode: "normal", bounds: { left: 0, top: 0, right: 10, bottom: 10 } }
];
const allowedAfter = clone(before);
allowedAfter[1].bounds.right = 20;
expect(compareStructureSnapshots(before, allowedAfter, ["3"]).passed, "Target geometry should be allowed to change.");

const renamedTarget = clone(before);
renamedTarget[1].name = "7Y9A3750";
const renamedTargetAudit = compareStructureSnapshots(before, renamedTarget, ["3"]);
expect(renamedTargetAudit.passed, "The replaced target smart-object name should be allowed to follow the source filename.");
expect(renamedTargetAudit.target_name_changes.length === 1, "Target smart-object name changes should be recorded in the audit.");

const movedWindow = clone(before);
movedWindow[0].bounds.right = 11;
expect(!compareStructureSnapshots(before, movedWindow, ["3"]).passed, "Non-target geometry change was not rejected.");

const renamedWindow = clone(before);
renamedWindow[0].name = "unexpected-fixed-layer-name";
expect(!compareStructureSnapshots(before, renamedWindow, ["3"]).passed, "Non-target layer name changes must still be rejected.");

const lostClipping = clone(before);
lostClipping[1].clipping = false;
expect(!compareStructureSnapshots(before, lostClipping, ["3"]).passed, "Target clipping change was not rejected.");

expect(compareAuditNames("care_label", "care_label").mode === "exact", "Exact audit names should match exactly.");
expect(compareAuditNames("\tsleeve", "sleeve").mode === "trimmed_boundary_whitespace", "UXP boundary whitespace normalization should be accepted.");
expect(compareAuditNames("\tfront", " front ").matches, "Equivalent boundary whitespace should be accepted.");
expect(!compareAuditNames("sleeve", "collar").matches, "Different audit name bodies must be rejected.");
expect(!compareAuditNames("\t", "").matches, "Empty normalized audit names must be rejected.");

const controlledDocument = { name: "controlled-copy.psd", id: 7 };
expect(findOpenDocumentByName([controlledDocument], controlledDocument.name) === controlledDocument, "Controlled document was not reacquired by name.");
expect(findOpenDocumentByName([], controlledDocument.name, false) === null, "Optional document lookup should allow a closed document.");
expectCode("WORKING_DOCUMENT_NOT_UNIQUE", () => findOpenDocumentByName([], controlledDocument.name));
expectCode("WORKING_DOCUMENT_NOT_UNIQUE", () => findOpenDocumentByName([controlledDocument, { ...controlledDocument, id: 8 }], controlledDocument.name));

const returnedDocument = { name: "controlled-copy.psd", id: 11 };
photoshopApp.documents = [];
photoshopApp.activeDocument = null;
expect(assertOpenedDocument(returnedDocument, returnedDocument.name) === returnedDocument, "app.open return value should be accepted before app.documents refreshes.");
expectCode("OPENED_DOCUMENT_MISMATCH", () => assertOpenedDocument(null, returnedDocument.name));
expectCode("OPENED_DOCUMENT_MISMATCH", () => assertOpenedDocument({ name: "wrong.psd", id: 12 }, returnedDocument.name));

expect(inspectProperty(returnedDocument, "name").value === returnedDocument.name, "Readable document properties should be recorded.");
expect(inspectProperty(null, "name").error === "OBJECT_IS_NULL", "Null document references must be distinguished from getter errors.");
const throwingDocument = {};
Object.defineProperty(throwingDocument, "name", { get() { throw new Error("name getter failed"); } });
const throwingInspection = inspectDocumentReference(throwingDocument);
expect(throwingInspection.is_null === false, "A throwing document proxy must not be reported as null.");
expect(throwingInspection.name.error === "name getter failed", "Document getter failures must remain visible in diagnostics.");

const refreshedDocument = { name: returnedDocument.name, id: 11 };
photoshopApp.activeDocument = refreshedDocument;
photoshopApp.documents = [];
expect(reacquireControlledDocument(returnedDocument.name) === refreshedDocument, "Active document should be accepted while app.documents is stale.");
photoshopApp.activeDocument = { name: "other.psd", id: 20 };
expectCode("WORKING_DOCUMENT_NOT_UNIQUE", () => reacquireControlledDocument(returnedDocument.name));
photoshopApp.documents = [{ name: returnedDocument.name, id: 21 }, { name: returnedDocument.name, id: 22 }];
expectCode("WORKING_DOCUMENT_NOT_UNIQUE", () => reacquireControlledDocument(returnedDocument.name));
photoshopApp.documents = [{ name: returnedDocument.name, id: 23 }];
expect(reacquireControlledDocument(returnedDocument.name).id === 23, "Document collection should remain a fallback when activeDocument is not the working copy.");
photoshopApp.documents = [];
photoshopApp.activeDocument = null;

const originalModalError = new ValidationError("BINDING_AUDIT_MISMATCH", "Binding audit mismatch.", {
  frame_id: "G-detail-05.frame-3",
  expected_audit_name: "care_label",
  actual_name: "normalized-name"
});
const serializedModalError = attachModalBoundaryError(
  serializeModalValidationFailure(originalModalError),
  new Error("The document with an id of undefined does not exist.")
);
let restoredModalError = null;
try {
  restoreModalValidationFailure(serializedModalError);
} catch (error) {
  restoredModalError = error;
}
expect(restoredModalError && restoredModalError.code === originalModalError.code, "Modal validation error code was not preserved.");
expect(restoredModalError.details.frame_id === originalModalError.details.frame_id, "Modal validation frame id was not preserved.");
expect(restoredModalError.details.expected_audit_name === originalModalError.details.expected_audit_name, "Modal validation expected name was not preserved.");
expect(restoredModalError.details.actual_name === originalModalError.details.actual_name, "Modal validation actual name was not preserved.");
expect(restoredModalError.details.modal_boundary_error.code === "UNEXPECTED_ERROR", "Secondary modal boundary error was not attached.");
expect(restoredModalError.details.modal_boundary_error.message.includes("undefined"), "Secondary modal boundary message was not preserved.");

async function runAsyncTests() {
  const boundaryDocument = { name: "split-modal-copy.psd", id: 31 };
  const boundaryEntry = { name: boundaryDocument.name };
  const boundaryToken = "test-boundary-session-token";
  const events = [];
  let insideModal = false;
  let capturedDescriptors = null;
  let capturedBatchPlayOptions = null;

  photoshopApp.documents = [];
  photoshopApp.activeDocument = null;
  uxpLocalFileSystem.createSessionToken = (entry) => {
    expect(!insideModal, "The template session token should be created before entering the open modal.");
    expect(entry === boundaryEntry, "The session token must be created for the selected template entry.");
    events.push("session.token");
    return boundaryToken;
  };
  photoshopAction.batchPlay = async (descriptors, options) => {
    expect(insideModal, "Action Manager open must run inside the dedicated open modal.");
    capturedDescriptors = descriptors;
    capturedBatchPlayOptions = options;
    events.push("action.batchPlay");
    return [{ documentID: boundaryDocument.id, unsafe_echo: boundaryToken }];
  };
  photoshopCore.executeAsModal = async (operation) => {
    expect(!insideModal, "The open modal must not be nested.");
    insideModal = true;
    events.push("modal.start");
    const result = await operation({ reportProgress() {} });
    events.push("modal.end");
    insideModal = false;
    setTimeout(() => {
      photoshopApp.activeDocument = boundaryDocument;
      photoshopApp.documents = [boundaryDocument];
      events.push("document.published");
    }, 0);
    return result;
  };

  const opened = await openDocumentAcrossModalBoundary(
    boundaryEntry,
    boundaryDocument.name,
    "Split modal test"
  );
  expect(opened.document === boundaryDocument, "The document must be resolved after the open modal returns.");
  expect(events.join(",") === "session.token,modal.start,action.batchPlay,modal.end,document.published", "Document visibility must be observed only after the Action Manager modal exits.");
  expect(Array.isArray(capturedDescriptors) && capturedDescriptors.length === 1, "Action Manager open must send exactly one descriptor.");
  expect(capturedDescriptors[0].target._path === boundaryToken, "batchPlay must receive the template session token in target._path.");
  expect(capturedDescriptors[0].target._kind === "local", "batchPlay must receive a local file target.");
  expect(capturedDescriptors[0]._obj === "open", "batchPlay must execute the Photoshop open action.");
  expect(capturedBatchPlayOptions && Object.keys(capturedBatchPlayOptions).length === 0, "Action Manager open must use the official empty batchPlay options object.");
  expect(opened.evidence.strategy === "batchplay_session_token", "Open evidence must identify the Action Manager session-token strategy.");
  expect(opened.evidence.descriptor_result_count === 1, "Open evidence must record the safe descriptor result count.");
  expect(opened.evidence.descriptor_document_id === boundaryDocument.id, "Open evidence must record the safe Action Manager document id when returned.");
  expect(opened.evidence.document_id === boundaryDocument.id, "Open evidence must record the resolved Photoshop document id.");
  expect(!JSON.stringify(opened.evidence).includes(boundaryToken), "Open evidence must never contain the session token or arbitrary descriptor output.");
  expect(opened.timing.open_modal_ms >= 0, "Open-modal timing must be recorded.");
  expect(opened.timing.document_visibility_wait_ms >= 0, "Outside-modal visibility timing must be recorded.");

  photoshopApp.documents = [];
  photoshopApp.activeDocument = null;
  photoshopAction.batchPlay = async () => [];
  photoshopCore.executeAsModal = async (operation) => operation({ reportProgress() {} });
  let emptyDescriptorError = null;
  try {
    await openDocumentAcrossModalBoundary(boundaryEntry, boundaryDocument.name, "Empty descriptor result test");
  } catch (error) {
    emptyDescriptorError = error;
  }
  expect(emptyDescriptorError && emptyDescriptorError.code === "OPEN_TEMPLATE_FAILED", "An empty Action Manager result must fail immediately with OPEN_TEMPLATE_FAILED.");
  expect(emptyDescriptorError.details.descriptor_result_count === 0, "An empty Action Manager result must record a zero result count.");
  expect(emptyDescriptorError.details.host_error_code === "INVALID_DESCRIPTOR_RESULT_COUNT", "An empty Action Manager result must retain its safe diagnostic code.");

  photoshopAction.batchPlay = async () => [{
    _obj: "error",
    result: -50,
    message: `Host rejected ${boundaryToken}`,
    arbitrary_host_payload: { token: boundaryToken }
  }];
  photoshopCore.executeAsModal = async (operation) => operation({ reportProgress() {} });
  let resolvedDescriptorError = null;
  try {
    await openDocumentAcrossModalBoundary(boundaryEntry, boundaryDocument.name, "Resolved error descriptor test");
  } catch (error) {
    resolvedDescriptorError = error;
  }
  expect(resolvedDescriptorError && resolvedDescriptorError.code === "OPEN_TEMPLATE_FAILED", "A resolved Action Manager error descriptor must fail immediately with OPEN_TEMPLATE_FAILED.");
  expect(resolvedDescriptorError.details.strategy === "batchplay_session_token", "Resolved open errors must preserve the safe strategy identifier.");
  expect(resolvedDescriptorError.details.descriptor_result_count === 1, "Resolved open errors must record the descriptor result count.");
  expect(resolvedDescriptorError.details.host_error_code === "-50", "Resolved open errors must retain the safe host error code.");
  expect(!JSON.stringify(resolvedDescriptorError).includes(boundaryToken), "Resolved open errors must redact the session token and discard arbitrary host payloads.");

  photoshopAction.batchPlay = async () => {
    const error = new Error(`Promise rejected ${boundaryToken}`);
    error.code = `REJECTED-${boundaryToken}`;
    throw error;
  };
  let rejectedBatchPlayError = null;
  try {
    await openDocumentAcrossModalBoundary(boundaryEntry, boundaryDocument.name, "Rejected batchPlay test");
  } catch (error) {
    rejectedBatchPlayError = error;
  }
  expect(rejectedBatchPlayError && rejectedBatchPlayError.code === "OPEN_TEMPLATE_FAILED", "A rejected batchPlay promise must fail with OPEN_TEMPLATE_FAILED.");
  expect(rejectedBatchPlayError.details.descriptor_result_count === 0, "A rejected batchPlay promise must report no descriptor result.");
  expect(!JSON.stringify(rejectedBatchPlayError).includes(boundaryToken), "Rejected batchPlay diagnostics must redact the session token.");

  photoshopAction.batchPlay = async () => [{ documentID: 99 }];
  photoshopCore.executeAsModal = async (operation) => {
    const result = await operation({ reportProgress() {} });
    setTimeout(() => {
      photoshopApp.activeDocument = boundaryDocument;
      photoshopApp.documents = [boundaryDocument];
    }, 0);
    return result;
  };
  let mismatchedDocumentError = null;
  try {
    await openDocumentAcrossModalBoundary(boundaryEntry, boundaryDocument.name, "Document id mismatch test");
  } catch (error) {
    mismatchedDocumentError = error;
  }
  expect(mismatchedDocumentError && mismatchedDocumentError.code === "OPENED_DOCUMENT_MISMATCH", "A mismatched Action Manager and DOM document id must stop the run.");
  expect(mismatchedDocumentError.details.descriptor_document_id === 99, "The mismatch evidence must retain the safe Action Manager document id.");
  expect(mismatchedDocumentError.details.document_id === boundaryDocument.id, "The mismatch evidence must retain the resolved DOM document id.");

  delete uxpLocalFileSystem.createSessionToken;
  delete photoshopAction.batchPlay;
  delete photoshopCore.executeAsModal;
  photoshopApp.documents = [];
  photoshopApp.activeDocument = null;
}

runAsyncTests()
  .then(() => console.log("UXP RenderPlan unit tests: PASS"))
  .catch((error) => {
    console.error(error);
    process.exitCode = 1;
  });
