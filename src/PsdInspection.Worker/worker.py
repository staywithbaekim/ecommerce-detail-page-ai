from __future__ import annotations

import hashlib
import json
import os
import sys
import time
from pathlib import Path
from typing import Any

from psd_tools import PSDImage


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
        smart_object = {
            "unique_id": value.unique_id,
            "filename": value.filename,
            "kind": value.kind,
            "file_type": value.filetype,
            "detected_file_type": value.detected_filetype,
            "file_size": value.filesize,
            "is_psd": value.is_psd(),
            "transform_box": list(value.transform_box),
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


def flatten_layers(parent: Any, prefix: list[int] | None = None) -> list[dict[str, Any]]:
    prefix = prefix or []
    records: list[dict[str, Any]] = []
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
        records.append(layer_record(layer, path, clipping_bases.get(id(layer))))
        if layer.is_group():
            records.extend(flatten_layers(layer, path))
    return records


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
        layers = flatten_layers(psd)
        if any(
            layer["clipping"] and layer["clipping_base"] is None
            for layer in layers
        ):
            raise ValueError("Clipping base relationship is unavailable")
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
