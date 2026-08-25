from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path
from typing import Any, Iterable

from psd_tools import PSDImage


EXPECTED_BASELINE_HASH = "DD997E7F47B8792744C4EBF57261A5109B50D195BEDFCC8B89FE836DCE7F6949"
EXPECTED_REPLACEMENT_HASH = "C04DDDD4C7802277D9996F347BA6FD4285987147941EED5A69638E0443696D57"
EXPECTED_OLD_PAYLOAD_HASH = "04720404A97FBE6DFD6EB138946C129545F37020F81346D98AA0616668C0B0DB"
SMART_OBJECT_PATH = (2,)
TEXT_LAYER_PATH = (3, 14, 6)
TEXT_LAYER_BASELINE = "\u4e0a\u8eab\u8212\u9002\u66f4\u67d4\u8f6f"
TEXT_MARKER = "TV08 EDIT OK"


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def bytes_sha256(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest().upper()


def layer_at(document: PSDImage, path: tuple[int, ...]):
    layer: Any = document
    for index in path:
        layer = layer[index]
    return layer


def walk_layers(group: Any, prefix: tuple[int, ...] = ()) -> Iterable[tuple[tuple[int, ...], Any]]:
    for index, layer in enumerate(group):
        path = prefix + (index,)
        yield path, layer
        if layer.is_group():
            yield from walk_layers(layer, path)


def layer_pixel_hash(layer: Any) -> str | None:
    if layer.is_group() or not layer.has_pixels():
        return None
    image = layer.topil()
    if image is None:
        return None
    digest = hashlib.sha256()
    digest.update(image.mode.encode("ascii"))
    digest.update(str(image.size).encode("ascii"))
    digest.update(image.tobytes())
    return digest.hexdigest().upper()


def layer_metadata(layer: Any) -> dict[str, Any]:
    return {
        "kind": layer.kind,
        "name": layer.name,
        "layer_id": layer.layer_id,
        "visible": bool(layer.visible),
        "opacity": int(layer.opacity),
        "blend_mode": str(layer.blend_mode),
        "clipping": bool(layer.clipping),
        "bbox": tuple(int(value) for value in layer.bbox),
        "text": layer.text if layer.kind == "type" else None,
        "smart_payload_sha256": (
            bytes_sha256(layer.smart_object.data) if layer.kind == "smartobject" else None
        ),
        "pixel_sha256": layer_pixel_hash(layer),
    }


def inspect_baseline(baseline: Path, replacement: Path) -> tuple[PSDImage, dict[tuple[int, ...], Any], dict[str, Any]]:
    baseline_hash = file_sha256(baseline)
    replacement_hash = file_sha256(replacement)
    if baseline_hash != EXPECTED_BASELINE_HASH:
        raise ValueError(f"Baseline PSD hash mismatch: {baseline_hash}")
    if replacement_hash != EXPECTED_REPLACEMENT_HASH:
        raise ValueError(f"Replacement JPG hash mismatch: {replacement_hash}")

    document = PSDImage.open(baseline)
    layers = dict(walk_layers(document))
    smart = layer_at(document, SMART_OBJECT_PATH)
    text = layer_at(document, TEXT_LAYER_PATH)
    type_count = sum(layer.kind == "type" for layer in layers.values())

    if smart.kind != "smartobject" or smart.name != "back_main":
        raise ValueError("Baseline smart-object path does not resolve to back_main.")
    old_payload_hash = bytes_sha256(smart.smart_object.data)
    if old_payload_hash != EXPECTED_OLD_PAYLOAD_HASH:
        raise ValueError(f"Baseline back_main payload mismatch: {old_payload_hash}")
    if text.kind != "type" or text.text != TEXT_LAYER_BASELINE:
        raise ValueError("Baseline text path does not resolve to the approved editable text layer.")
    if type_count != 16:
        raise ValueError(f"Baseline TypeLayer count is {type_count}, expected 16.")

    facts = {
        "baseline_sha256": baseline_hash,
        "replacement_sha256": replacement_hash,
        "document": {
            "width": document.width,
            "height": document.height,
            "depth": document.depth,
            "color_mode": int(document.color_mode),
            "layer_count_recursive": len(layers),
            "type_layer_count": type_count,
        },
        "smart_object": {
            "path": list(SMART_OBJECT_PATH),
            "layer_id": smart.layer_id,
            "name": smart.name,
            "filename": smart.smart_object.filename,
            "payload_sha256": old_payload_hash,
        },
        "text_layer": {
            "path": list(TEXT_LAYER_PATH),
            "layer_id": text.layer_id,
            "name": text.name,
            "text": text.text,
        },
    }
    return document, layers, facts


def verify_returned(baseline: Path, edited: Path, replacement: Path) -> dict[str, Any]:
    baseline_document, baseline_layers, baseline_facts = inspect_baseline(baseline, replacement)
    edited_hash = file_sha256(edited)
    if edited_hash == EXPECTED_BASELINE_HASH:
        raise ValueError("Edited PSD hash is unchanged from the baseline.")

    edited_document = PSDImage.open(edited)
    edited_layers = dict(walk_layers(edited_document))
    if (edited_document.width, edited_document.height, edited_document.depth, int(edited_document.color_mode)) != (
        baseline_document.width,
        baseline_document.height,
        baseline_document.depth,
        int(baseline_document.color_mode),
    ):
        raise ValueError("Edited PSD document dimensions, depth, or color mode changed.")
    if set(edited_layers) != set(baseline_layers):
        raise ValueError("Edited PSD layer paths differ from the baseline.")

    smart = layer_at(edited_document, SMART_OBJECT_PATH)
    text = layer_at(edited_document, TEXT_LAYER_PATH)
    if smart.kind != "smartobject" or smart.name != "back_main":
        raise ValueError("Returned PSD no longer has back_main as a smart object at path 2.")
    smart_payload_hash = bytes_sha256(smart.smart_object.data)
    if smart_payload_hash != EXPECTED_REPLACEMENT_HASH:
        raise ValueError(f"Returned back_main payload mismatch: {smart_payload_hash}")
    if text.kind != "type" or text.text != TEXT_MARKER:
        raise ValueError("Returned text layer is not an editable TypeLayer with TV08 EDIT OK contents.")

    target_paths = {SMART_OBJECT_PATH, TEXT_LAYER_PATH}
    target_ancestors = {(3,), (3, 14)}
    unchanged_layers = 0
    pixel_layers_checked = 0
    for path in sorted(baseline_layers):
        baseline_layer = baseline_layers[path]
        edited_layer = edited_layers[path]
        before = layer_metadata(baseline_layer)
        after = layer_metadata(edited_layer)

        if before["kind"] != after["kind"] or before["layer_id"] != after["layer_id"]:
            raise ValueError(f"Layer identity changed at path {path}.")
        if path in target_paths:
            continue

        comparable_keys = ["name", "visible", "opacity", "blend_mode", "clipping", "text", "smart_payload_sha256"]
        if path not in target_ancestors:
            comparable_keys.append("bbox")
        if not baseline_layer.is_group():
            comparable_keys.append("pixel_sha256")
            if before["pixel_sha256"] is not None:
                pixel_layers_checked += 1

        changed_keys = [key for key in comparable_keys if before[key] != after[key]]
        if changed_keys:
            raise ValueError(f"Non-target layer changed at path {path}: {', '.join(changed_keys)}")
        unchanged_layers += 1

    return {
        "schema": "uxp-tv08-return-deep-verification/v1",
        "passed": True,
        "baseline": baseline_facts,
        "edited": {
            "path": str(edited.resolve()),
            "sha256": edited_hash,
            "document_reopened_by_parser": True,
            "smart_object": {
                "path": list(SMART_OBJECT_PATH),
                "layer_id": smart.layer_id,
                "name": smart.name,
                "filename": smart.smart_object.filename,
                "payload_sha256": smart_payload_hash,
            },
            "text_layer": {
                "path": list(TEXT_LAYER_PATH),
                "layer_id": text.layer_id,
                "name": text.name,
                "text": text.text,
            },
            "unchanged_non_target_layers": unchanged_layers,
            "decoded_pixel_layers_compared": pixel_layers_checked,
        },
        "manual_screenshot_review_required": True,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--baseline", required=True, type=Path)
    parser.add_argument("--replacement", required=True, type=Path)
    parser.add_argument("--edited", type=Path)
    parser.add_argument("--baseline-only", action="store_true")
    args = parser.parse_args()

    try:
        if args.baseline_only:
            _, _, facts = inspect_baseline(args.baseline, args.replacement)
            result = {
                "schema": "uxp-tv08-baseline-deep-inspection/v1",
                "passed": True,
                "baseline": facts,
            }
        else:
            if args.edited is None:
                parser.error("--edited is required unless --baseline-only is used")
            result = verify_returned(args.baseline, args.edited, args.replacement)
        print(json.dumps(result, ensure_ascii=True, indent=2))
        return 0
    except Exception as error:
        print(
            json.dumps(
                {
                    "schema": "uxp-tv08-return-deep-verification/v1",
                    "passed": False,
                    "error_type": type(error).__name__,
                    "error": str(error),
                },
                ensure_ascii=True,
                indent=2,
            ),
            file=sys.stderr,
        )
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
