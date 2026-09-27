from __future__ import annotations

import copy
import hashlib
import json
import os
import sys
import time
from pathlib import Path
from typing import Any

from PIL import ImageChops
from psd_tools import PSDImage
from psd_tools.constants import EffectOSType, Tag
from psd_tools.psd import descriptor as psd_descriptor
from psd_tools.psd.effects_layer import BevelInfo
from psd_tools.terminology import Unit


PROTOCOL_VERSION = "psd-inspection/v1"
MAX_REQUEST_CHARS = 64 * 1024


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def peak_working_set_bytes() -> int:
    if os.name != "nt":
        return 0

    import ctypes
    from ctypes import wintypes

    class ProcessMemoryCountersEx(ctypes.Structure):
        _fields_ = [
            ("cb", wintypes.DWORD),
            ("PageFaultCount", wintypes.DWORD),
            ("PeakWorkingSetSize", ctypes.c_size_t),
            ("WorkingSetSize", ctypes.c_size_t),
            ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
            ("QuotaPagedPoolUsage", ctypes.c_size_t),
            ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
            ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
            ("PagefileUsage", ctypes.c_size_t),
            ("PeakPagefileUsage", ctypes.c_size_t),
            ("PrivateUsage", ctypes.c_size_t),
        ]

    counters = ProcessMemoryCountersEx()
    counters.cb = ctypes.sizeof(counters)
    get_current_process = ctypes.windll.kernel32.GetCurrentProcess
    get_current_process.restype = wintypes.HANDLE
    handle = get_current_process()
    get_process_memory_info = ctypes.windll.psapi.GetProcessMemoryInfo
    get_process_memory_info.argtypes = [
        wintypes.HANDLE,
        ctypes.c_void_p,
        wintypes.DWORD,
    ]
    if not get_process_memory_info(handle, ctypes.byref(counters), counters.cb):
        raise ctypes.WinError()
    return int(counters.PeakWorkingSetSize)


def json_value(value: Any) -> Any:
    raw = getattr(value, "value", value)
    if isinstance(raw, bytes):
        try:
            return raw.decode("ascii")
        except UnicodeDecodeError:
            return raw.hex().upper()
    if isinstance(raw, (str, int, float, bool)) or raw is None:
        return raw
    return str(raw)


def clipping_base_record(layer: Any, path: list[int]) -> dict[str, Any]:
    return {
        "path": path,
        "layer_id": layer.layer_id,
        "name": layer.name,
        "kind": layer.kind,
        "visible": layer.visible,
        "effective_visible": layer.is_visible(),
        "bbox": [layer.left, layer.top, layer.right, layer.bottom],
        "has_mask": layer.has_mask(),
        "has_vector_mask": layer.has_vector_mask(),
        "opacity": layer.opacity,
        "blend_mode": json_value(layer.blend_mode),
    }


def layer_record(
    layer: Any,
    path: list[int],
    clipping_base: dict[str, Any] | None,
) -> dict[str, Any]:
    smart_object = None
    if layer.kind == "smartobject":
        value = layer.smart_object
        embedded_content_sha256 = None
        embedded_content_available = False
        # Only hash bytes that are embedded in the PSD.  External smart
        # objects are intentionally not opened here: a PSD is untrusted input
        # and resolving its fullPath/relPath would cross the worker boundary.
        if value.kind == "data":
            embedded_content_sha256 = hashlib.sha256(value.data).hexdigest().upper()
            embedded_content_available = True
        smart_object = {
            "unique_id": value.unique_id,
            "filename": value.filename,
            "kind": value.kind,
            "file_type": value.filetype,
            "detected_file_type": value.detected_filetype,
            "file_size": value.filesize,
            "is_psd": value.is_psd(),
            "transform_box": list(value.transform_box),
            "embedded_content_sha256": embedded_content_sha256,
            "embedded_content_available": embedded_content_available,
        }

    return {
        "path": path,
        "layer_id": layer.layer_id,
        "name": layer.name,
        "kind": layer.kind,
        "is_group": layer.is_group(),
        "visible": layer.visible,
        "effective_visible": layer.is_visible(),
        "clipping": bool(layer.clipping),
        "opacity": layer.opacity,
        "fill_opacity": layer.fill_opacity,
        "blend_mode": json_value(layer.blend_mode),
        "bbox": [layer.left, layer.top, layer.right, layer.bottom],
        "has_mask": layer.has_mask(),
        "has_vector_mask": layer.has_vector_mask(),
        "clipping_base": clipping_base,
        "smart_object": smart_object,
    }


def flatten_layers(
    parent: Any,
    prefix: list[int] | None = None,
) -> list[tuple[Any, dict[str, Any]]]:
    prefix = prefix or []
    records: list[tuple[Any, dict[str, Any]]] = []
    indexed_layers = [
        (layer, [*prefix, index])
        for index, layer in enumerate(parent)
    ]
    clipping_bases: dict[int, dict[str, Any]] = {}
    for layer, path in indexed_layers:
        base = clipping_base_record(layer, path)
        for clipped_layer in layer.clip_layers:
            clipping_bases[id(clipped_layer)] = base

    for layer, path in indexed_layers:
        records.append((layer, layer_record(layer, path, clipping_bases.get(id(layer)))))
        if layer.is_group():
            records.extend(flatten_layers(layer, path))
    return records


def _viewport(layer: Any, psd: Any) -> tuple[int, int, int, int] | None:
    left = max(0, int(layer.left))
    top = max(0, int(layer.top))
    right = min(int(psd.width), int(layer.right))
    bottom = min(int(psd.height), int(layer.bottom))
    return (left, top, right, bottom) if right > left and bottom > top else None


def _nonzero_count(mask: Any) -> int:
    histogram = mask.histogram()
    return int(sum(histogram[1:]))


def _difference_mask(first: Any, second: Any) -> Any:
    difference = ImageChops.difference(first.convert("RGBA"), second.convert("RGBA"))
    red, green, blue, alpha = difference.split()
    mask = ImageChops.lighter(ImageChops.lighter(red, green), ImageChops.lighter(blue, alpha))
    difference.close()
    return mask


def pixel_evidence(psd: Any, layer: Any, complete_document: Any) -> dict[str, Any]:
    unavailable = {
        "available": False,
        "rendered_pixel_count": 0,
        "composite_contribution_pixel_count": 0,
        "composite_contribution_bbox": None,
    }
    if layer.kind != "smartobject":
        return unavailable

    viewport = _viewport(layer, psd)
    if viewport is None or not layer.is_visible() or int(layer.opacity) <= 0:
        return {
            **unavailable,
            "available": True,
        }

    complete_view = None
    without_layer = None
    difference_mask = None
    try:
        # A clipped smart object composites as transparent in isolation because
        # its clipping base is outside the layer. The shape channel establishes
        # that the placed layer itself has rendered pixels; removing the layer
        # from the full document below establishes its final visible effect.
        rendered_shape = layer.numpy("shape")
        if rendered_shape is None:
            return unavailable
        rendered_pixel_count = int((rendered_shape > 0).sum())

        complete_view = complete_document.crop(viewport)
        without_layer = psd.composite(
            viewport=viewport,
            force=True,
            color=0.0,
            alpha=0.0,
            layer_filter=lambda candidate: candidate.is_visible() and candidate is not layer,
            ignore_preview=True,
            apply_icc=False,
        )
        difference_mask = _difference_mask(complete_view, without_layer)
        contribution_count = _nonzero_count(difference_mask)
        relative_bbox = difference_mask.getbbox()
        contribution_bbox = None
        if relative_bbox is not None:
            contribution_bbox = [
                relative_bbox[0] + viewport[0],
                relative_bbox[1] + viewport[1],
                relative_bbox[2] + viewport[0],
                relative_bbox[3] + viewport[1],
            ]
        return {
            "available": True,
            "rendered_pixel_count": rendered_pixel_count,
            "composite_contribution_pixel_count": contribution_count,
            "composite_contribution_bbox": contribution_bbox,
        }
    except MemoryError:
        raise
    except Exception:
        return unavailable
    finally:
        for image in (difference_mask, complete_view, without_layer):
            if image is not None:
                image.close()


def attach_pixel_evidence(
    psd: Any,
    layer_entries: list[tuple[Any, dict[str, Any]]],
) -> None:
    complete_document = psd.composite(
        force=True,
        color=0.0,
        alpha=0.0,
        layer_filter=lambda candidate: candidate.is_visible(),
        ignore_preview=True,
        apply_icc=False,
    )
    try:
        for layer, record in layer_entries:
            record["pixel_evidence"] = pixel_evidence(psd, layer, complete_document)
    finally:
        complete_document.close()


def structure_signature(layers: list[dict[str, Any]]) -> str:
    normalized = [
        {
            "path": layer["path"],
            "layer_id": layer["layer_id"],
            "name": layer["name"],
            "kind": layer["kind"],
            "visible": layer["visible"],
            "clipping": layer["clipping"],
            "opacity": layer["opacity"],
            "fill_opacity": layer["fill_opacity"],
            "blend_mode": layer["blend_mode"],
            "bbox": layer["bbox"],
            "has_mask": layer["has_mask"],
            "has_vector_mask": layer["has_vector_mask"],
            "smart_object_unique_id": (
                layer["smart_object"]["unique_id"]
                if layer["smart_object"] is not None
                else None
            ),
        }
        for layer in layers
    ]
    payload = json.dumps(
        normalized,
        ensure_ascii=False,
        separators=(",", ":"),
        sort_keys=True,
    ).encode("utf-8")
    return hashlib.sha256(payload).hexdigest().upper()


def effect_descriptor_value(value: Any, root: bool = False) -> Any:
    """Canonical typed effect data; unknown values fail closed, never use repr.

    PS 26.8.1 materializes the legacy classic interpolation method and a derived
    enabled-effect count on save. Only those defaults and the observed localized
    built-in labels are normalized. All actual effect parameters remain.
    """
    if isinstance(value, psd_descriptor.Descriptor):
        items = dict(value.items())
        if root:
            count = sum(1 for item in items.values()
                        for effect in (list(item) if isinstance(item, psd_descriptor.List) else [item])
                        if isinstance(effect, psd_descriptor.Descriptor)
                        and isinstance(effect.get(b"enab"), psd_descriptor.Bool)
                        and effect[b"enab"].value is True)
            if b"numModifyingFX" in items:
                counter = items[b"numModifyingFX"]
                if type(counter) is not psd_descriptor.Integer or counter.value != count:
                    raise ValueError("Unsupported effect count")
            items[b"numModifyingFX"] = psd_descriptor.Integer(count)
        if value.classID == b"GrFl" and b"gs99" not in items:
            items[b"gs99"] = psd_descriptor.Enumerated(
                typeID=b"gradientInterpolationMethodType", enum=b"Gcls")
        if value.classID == b"Grdn" and isinstance(items.get(b"Nm  "), psd_descriptor.String):
            if items[b"Nm  "].value in (
                "$$$/DefaultGradient/ForegroundToTransparent=Foreground to Transparent\0",
                "前景色到透明渐变\0",
            ):
                items[b"Nm  "] = psd_descriptor.String("builtin:ForegroundToTransparent")
        # A no-edit PS 26.8.1 save renames this disabled outer-glow contour.
        # Restrict the alias to the observed two-point linear curve; preserve
        # every point, extra field and all other effect/contour names.
        if value.classID == b"OrGl" and disabled_effect(value):
            contour = items.get(b"TrnS")
            if linear_contour(contour):
                contour = copy.deepcopy(contour)
                contour[b"Nm  "] = psd_descriptor.String("builtin:Linear")
                items[b"TrnS"] = contour
        return [type(value).__name__, value.classID.hex(), value.name,
                getattr(value, "version", None), getattr(value, "data_version", None),
                [[key.hex(), effect_descriptor_value(item)] for key, item in sorted(items.items())]]
    if isinstance(value, psd_descriptor.List):
        return ["List", [effect_descriptor_value(item) for item in value]]
    if type(value) is psd_descriptor.Enumerated:
        return ["Enumerated", value.typeID.hex(), value.enum.hex()]
    if type(value) is psd_descriptor.UnitFloat:
        return ["UnitFloat", value.unit.value.hex(), value.value]
    if type(value) in (psd_descriptor.Bool, psd_descriptor.Integer,
                       psd_descriptor.Double, psd_descriptor.String, psd_descriptor.LargeInteger):
        return [type(value).__name__, value.value]
    raise ValueError("Unsupported effect descriptor type")


def disabled_effect(value: Any) -> bool:
    return isinstance(value, psd_descriptor.Descriptor) and all(
        type(value.get(key)) is psd_descriptor.Bool and value[key].value is False
        for key in (b"enab", b"present"))


def linear_contour(value: Any) -> bool:
    if not isinstance(value, psd_descriptor.Descriptor) or value.classID != b"ShpC":
        return False
    name, curve = value.get(b"Nm  "), value.get(b"Crv ")
    return (set(value) == {b"Nm  ", b"Crv "}
            and type(name) is psd_descriptor.String and name.value in ("默认\0", "线性\0")
            and type(curve) is psd_descriptor.List and len(curve) == 2
            and all(isinstance(point, psd_descriptor.Descriptor)
                    and set(point) == {b"Hrzn", b"Vrtc"}
                    and all(type(point.get(key)) is psd_descriptor.Double and point[key].value == coordinate
                            for key in (b"Hrzn", b"Vrtc"))
                    for point, coordinate in zip(curve, (0.0, 255.0))))


def legacy_effect_value(block: Any, modern_blocks: list[Any]) -> Any:
    bevel = block.get(EffectOSType.BEVEL)
    if bevel is None:
        return block.tobytes().hex()
    if type(bevel) is not BevelInfo:
        raise ValueError("Unsupported legacy bevel")
    # PS rewrites the disabled 50% compatibility cache from 127 to 128.
    # Require the modern percentage evidence as well; legacy-only effects,
    # enabled effects and all other opacity values retain exact comparison.
    modern_bevels = [item.get(b"ebbl") for item in modern_blocks]
    if (bevel.version == 2 and bevel.enabled == 0
            and bevel.highlight_opacity in (127, 128) and bevel.shadow_opacity in (127, 128)
            and modern_bevels and all(disabled_effect(item) and all(
                type(item.get(key)) is psd_descriptor.UnitFloat
                and item[key].unit == Unit.Percent and item[key].value == 50.0
                for key in (b"hglO", b"sdwO")) for item in modern_bevels)):
        block = copy.deepcopy(block)
        block[EffectOSType.BEVEL].highlight_opacity = 128
        block[EffectOSType.BEVEL].shadow_opacity = 128
    # psd-tools BevelInfo.write repeats the base colors for version 2 instead
    # of serializing these real colors. Include them explicitly in evidence.
    colors = [None if color is None else color.tobytes().hex()
              for color in (bevel.real_highlight_color, bevel.real_shadow_color)]
    return [block.tobytes().hex(), colors]


def effects_hash(layer: Any) -> str | None:
    blocks = []
    modern_tags = (Tag.OBJECT_BASED_EFFECTS_LAYER_INFO, Tag.OBJECT_BASED_EFFECTS_LAYER_INFO_V0,
                   Tag.OBJECT_BASED_EFFECTS_LAYER_INFO_V1)
    modern_blocks = [layer.tagged_blocks.get_data(tag) for tag in modern_tags if tag in layer.tagged_blocks]
    for tag in (*modern_tags, Tag.EFFECTS_LAYER):
        if tag not in layer.tagged_blocks:
            continue
        block = layer.tagged_blocks.get_data(tag)
        content = (legacy_effect_value(block, modern_blocks) if tag == Tag.EFFECTS_LAYER
                   else effect_descriptor_value(block, root=True))
        blocks.append([bytes(tag).hex(), content])
    if not blocks:
        return None
    payload = json.dumps(blocks, ensure_ascii=False, separators=(",", ":"), allow_nan=False).encode("utf-8")
    return hashlib.sha256(payload).hexdigest().upper()


def text_style_hash(layer: Any) -> str:
    """Cover editable typography independently of its cached raster pixels.

    Keep complete EngineData (fonts, style/paragraph runs, resource dictionaries),
    transform and warp. The descriptor's computed bounds are not style input;
    PS 26.8.1 refreshes them on save while layer pixels/bounds and EngineData stay
    unchanged. Those rendered bounds/pixels are checked separately by the host.
    """
    data = layer._data
    entries = dict(data.text_data.items())
    allowed = {b"Txt ", b"textGridding", b"Ornt", b"AntA", b"bounds",
               b"boundingBox", b"TextIndex", b"EngineData", b"TxMg"}
    if set(entries) - allowed or not {b"EngineData", b"Txt ", b"Ornt", b"AntA"} <= set(entries):
        raise ValueError("Unsupported text descriptor")
    # PS 26.8.1 was observed to materialize this no-mixing value when saving.
    entries.setdefault(b"TxMg", psd_descriptor.Enumerated(typeID=b"TxMg", enum=b"TxNM"))
    engine = layer._engine_data
    if any(engine.get(key) is None for key in ("EngineDict", "ResourceDict", "DocumentResources")):
        raise ValueError("Missing editable text data")
    value = ["text-style/v1", data.version, data.text_version, data.warp_version,
             list(data.transform), [data.left, data.top, data.right, data.bottom],
             engine.tobytes().hex(), effect_descriptor_value(data.warp),
             [[key.hex(), effect_descriptor_value(item)] for key, item in sorted(entries.items())
              if key not in (b"EngineData", b"bounds")]]
    payload = json.dumps(value, ensure_ascii=False, separators=(",", ":"), allow_nan=False).encode("utf-8")
    return hashlib.sha256(payload).hexdigest().upper()


def adjustment_hash(layer: Any) -> str:
    tags = {"curves": Tag.CURVES, "exposure": Tag.EXPOSURE}
    tag = tags.get(layer.kind)
    if tag is None or tag not in layer.tagged_blocks:
        raise ValueError("Unsupported adjustment")
    block = layer.tagged_blocks.get_data(tag)
    # Keep all channels/points and the extra curves payload. No normalization:
    # a saved descriptor that differs needs independent compatibility evidence.
    payload = block.tobytes()
    if not payload:
        raise ValueError("Missing adjustment data")
    return hashlib.sha256(b"adjustment/v1\0" + bytes(tag) + payload).hexdigest().upper()


def empty_mask_hash(mask: Any) -> str:
    # A zero-area mask is a constant background with flags/parameters, not
    # missing evidence. A nonempty mask that failed decoding stays rejected.
    if mask.width != 0 or mask.height != 0:
        raise ValueError("Nonempty mask has no decoded pixels")
    return hashlib.sha256(b"empty-mask/v1\0" + mask.data.tobytes()).hexdigest().upper()


def shape_content_hash(layer: Any) -> str:
    # Fixed solid vector shapes are preserved, never edited by RENDER. Retain
    # every tagged block byte, including fill, vector/stroke/live-shape data and
    # unknown additions. No name/metadata/default normalization is permitted.
    if layer.kind != "shape" or not layer.has_vector_mask() or Tag.SOLID_COLOR_SHEET_SETTING not in layer.tagged_blocks:
        raise ValueError("Unsupported shape content")
    digest = hashlib.sha256(b"fixed-solid-shape/v1\0")
    for tag, block in sorted(layer.tagged_blocks.items(), key=lambda item: bytes(item[0])):
        payload = block.tobytes()
        if not payload:
            raise ValueError("Missing shape block")
        digest.update(bytes(tag))
        digest.update(len(payload).to_bytes(8, "big"))
        digest.update(payload)
    return digest.hexdigest().upper()


def render_content_evidence(layer: Any) -> dict[str, Any]:
    # Optional RENDER evidence. Never resolve linked smart objects or change PSD bytes.
    # Unknown adjustment/fill types stay unavailable until their semantics are covered.
    result = {"available": False, "pixel_sha256": None, "text_sha256": None,
              "mask_sha256": None, "vector_mask_sha256": None, "effects_sha256": None,
              "text_style_sha256": None, "adjustment_sha256": None}
    if layer.kind not in ("pixel", "type", "smartobject", "group", "curves", "exposure", "shape"):
        return result

    def image_hash(image: Any) -> str | None:
        if image is None:
            return None
        try:
            digest = hashlib.sha256()
            digest.update(image.mode.encode("ascii"))
            digest.update(str(image.size).encode("ascii"))
            digest.update(image.tobytes())
            return digest.hexdigest().upper()
        finally:
            image.close()

    def blocks_hash(tags: tuple[Any, ...]) -> str | None:
        blocks = [(tag, layer.tagged_blocks.get_data(tag)) for tag in tags if tag in layer.tagged_blocks]
        if not blocks:
            return None
        digest = hashlib.sha256()
        for tag, block in blocks:
            digest.update(bytes(tag))
            digest.update(block.tobytes() if hasattr(block, "tobytes") else bytes(block))
        return digest.hexdigest().upper()

    try:
        if not layer.is_group() and layer.has_pixels():
            result["pixel_sha256"] = image_hash(layer.topil())
            if result["pixel_sha256"] is None:
                return result
        if layer.kind == "type":
            result["text_sha256"] = hashlib.sha256(layer.text.encode("utf-8")).hexdigest().upper()
            result["text_style_sha256"] = text_style_hash(layer)
        if layer.kind in ("curves", "exposure"):
            result["adjustment_sha256"] = adjustment_hash(layer)
        if layer.kind == "shape":
            result["adjustment_sha256"] = shape_content_hash(layer)
        if layer.has_mask():
            result["mask_sha256"] = image_hash(layer.mask.topil())
            if result["mask_sha256"] is None:
                result["mask_sha256"] = empty_mask_hash(layer.mask)
        if layer.has_vector_mask():
            result["vector_mask_sha256"] = blocks_hash((Tag.VECTOR_MASK_SETTING1, Tag.VECTOR_MASK_SETTING2))
            if result["vector_mask_sha256"] is None:
                return result
        result["effects_sha256"] = effects_hash(layer)
        result["available"] = True
        return result
    except MemoryError:
        raise
    except Exception:
        return result


def inspect(request: dict[str, Any]) -> dict[str, Any]:
    if request.get("schema_version") != PROTOCOL_VERSION:
        return failure(
            str(request.get("request_id", "unknown")),
            "TPL_PSD_PROTOCOL_UNSUPPORTED",
            "PSD inspection protocol version is not supported.",
            "Update the desktop application and PSD inspection worker together.",
            False,
        )

    request_id = request.get("request_id")
    input_value = request.get("input_path")
    preview_value = request.get("preview_path")
    if not isinstance(request_id, str) or not request_id or len(request_id) > 128:
        return failure(
            "unknown",
            "TPL_PSD_REQUEST_INVALID",
            "PSD inspection request id is invalid.",
            "Retry the template import.",
            False,
        )
    if not isinstance(input_value, str) or not input_value:
        return failure(
            request_id,
            "TPL_PSD_REQUEST_INVALID",
            "PSD inspection input is invalid.",
            "Retry the template import.",
            False,
        )
    if preview_value is not None and not isinstance(preview_value, str):
        return failure(
            request_id,
            "TPL_PSD_REQUEST_INVALID",
            "PSD preview output is invalid.",
            "Retry the template import.",
            False,
        )

    input_path = Path(input_value)
    preview_path = Path(preview_value) if preview_value else None
    try:
        hash_before = sha256(input_path)
        size_bytes = input_path.stat().st_size
    except (FileNotFoundError, PermissionError, OSError):
        return failure(
            request_id,
            "TPL_SOURCE_UNREADABLE",
            "The application-owned PSD copy cannot be read.",
            "Retry the copy or check local storage permissions.",
            True,
        )

    started = time.perf_counter()
    try:
        psd = PSDImage.open(input_path)
        opened_ms = round((time.perf_counter() - started) * 1000, 3)
        layer_entries = flatten_layers(psd)
        if request.get("include_render_content_evidence") is True:
            for layer, record in layer_entries:
                record["content_evidence"] = render_content_evidence(layer)
        layers = [record for _, record in layer_entries]
        if any(
            layer["clipping"] and layer["clipping_base"] is None
            for layer in layers
        ):
            raise ValueError("Clipping base relationship is unavailable")
        attach_pixel_evidence(psd, layer_entries)
        inspected_ms = round((time.perf_counter() - started) * 1000, 3)
        preview = None
        if preview_path is not None:
            preview_path.parent.mkdir(parents=True, exist_ok=True)
            composite = psd.composite(apply_icc=True)
            if composite is None:
                raise ValueError("PSD composite is unavailable")
            composite.save(preview_path, format="PNG")
            preview = {
                "width": composite.width,
                "height": composite.height,
                "mode": composite.mode,
                "sha256": sha256(preview_path),
                "size_bytes": preview_path.stat().st_size,
            }
            composite.close()
        hash_after = sha256(input_path)
    except MemoryError:
        return failure(
            request_id,
            "TPL_PSD_RESOURCE_LIMIT",
            "PSD inspection exceeded the worker memory limit.",
            "Use Photoshop compatibility inspection for this PSD.",
            False,
        )
    except Exception:
        return failure(
            request_id,
            "TPL_PSD_UNSUPPORTED_STRUCTURE",
            "The PSD is damaged or uses an unsupported structure.",
            "Choose another PSD or run Photoshop compatibility inspection.",
            False,
        )

    if hash_before != hash_after:
        return failure(
            request_id,
            "TPL_SOURCE_CHANGED_DURING_INSPECTION",
            "The application-owned PSD copy changed during inspection.",
            "Retry from a new verified copy.",
            True,
        )

    completed_ms = round((time.perf_counter() - started) * 1000, 3)
    return {
        "schema_version": PROTOCOL_VERSION,
        "request_id": request_id,
        "ok": True,
        "error": None,
        "inspection": {
            "file_sha256": hash_before,
            "size_bytes": size_bytes,
            "canvas": {
                "width": psd.width,
                "height": psd.height,
                "depth": psd.depth,
                "color_mode": getattr(psd.color_mode, "name", str(psd.color_mode)),
            },
            "layer_count": len(layers),
            "smart_object_count": sum(
                1 for layer in layers if layer["smart_object"] is not None
            ),
            "mask_count": sum(1 for layer in layers if layer["has_mask"]),
            "vector_mask_count": sum(
                1 for layer in layers if layer["has_vector_mask"]
            ),
            "clipping_count": sum(1 for layer in layers if layer["clipping"]),
            "structure_signature": structure_signature(layers),
            "timing_ms": {
                "open": opened_ms,
                "inspect": inspected_ms,
                "total": completed_ms,
            },
            "process_peak_working_set_bytes": peak_working_set_bytes(),
            "preview": preview,
            "layers": layers,
        },
    }


def failure(
    request_id: str,
    code: str,
    message: str,
    user_action: str,
    retryable: bool,
) -> dict[str, Any]:
    return {
        "schema_version": PROTOCOL_VERSION,
        "request_id": request_id,
        "ok": False,
        "inspection": None,
        "error": {
            "code": code,
            "message": message,
            "user_action": user_action,
            "retryable": retryable,
        },
    }


def main() -> int:
    if hasattr(sys.stdin, "reconfigure"):
        sys.stdin.reconfigure(encoding="utf-8")
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")

    raw = sys.stdin.read(MAX_REQUEST_CHARS + 1)
    if len(raw) > MAX_REQUEST_CHARS:
        response = failure(
            "unknown",
            "TPL_PSD_REQUEST_TOO_LARGE",
            "PSD inspection request is too large.",
            "Retry the template import.",
            False,
        )
    else:
        try:
            request = json.loads(raw)
            response = inspect(request) if isinstance(request, dict) else failure(
                "unknown",
                "TPL_PSD_REQUEST_INVALID",
                "PSD inspection request is invalid.",
                "Retry the template import.",
                False,
            )
        except json.JSONDecodeError:
            response = failure(
                "unknown",
                "TPL_PSD_REQUEST_INVALID",
                "PSD inspection request is invalid.",
                "Retry the template import.",
                False,
            )

    json.dump(response, sys.stdout, ensure_ascii=False, separators=(",", ":"))
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
