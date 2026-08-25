from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path
from typing import Any, Iterable

from psd_tools import PSDImage


EXPECTED_TEMPLATE_HASH = "69BF44A64E47CB8D6817FA8589888163B14AB08A19A0BCFCEE02352C3392F5EF"
EXPECTED_TARGETS = {
    (3,): (541, "care_label", "7Y9A3762.jpg", "B7161B1A39447258C39464706CCED111B0736189CFC20C608CB1FAE5353B3DE4"),
    (7,): (540, "collar", "7Y9A3751.jpg", "F2862B0162AA2FB6BF969C88E9DF9F0EC4839E5565B0A45CCF4AF8B3721E0DF3"),
    (11,): (539, "sleeve", "7Y9A3755.jpg", "868E85930C3996E2565490A89E80154CC6EF26EDABF58044E635B475779079CA"),
    (15,): (538, "front", "7Y9A3750.jpg", "C04DDDD4C7802277D9996F347BA6FD4285987147941EED5A69638E0443696D57"),
}


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest().upper()


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


def smart_payload_hash(layer: Any) -> str | None:
    if layer.kind != "smartobject":
        return None
    data = layer.smart_object.data
    return sha256_bytes(data) if data is not None else None


def layer_metadata(layer: Any, include_pixels: bool) -> dict[str, Any]:
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
        "smart_payload_sha256": smart_payload_hash(layer),
        "pixel_sha256": layer_pixel_hash(layer) if include_pixels else None,
    }


def verify(baseline_path: Path, output_path: Path, evidence_path: Path) -> dict[str, Any]:
    if sha256_file(baseline_path) != EXPECTED_TEMPLATE_HASH:
        raise ValueError("Baseline PSD hash does not match the approved TV-06 fixture.")

    evidence = json.loads(evidence_path.read_text(encoding="utf-8"))
    if not evidence.get("ok") or evidence.get("schema") != "uxp-multi-frame-validation-evidence/v1":
        raise ValueError("Evidence is not a successful TV-06 multi-frame record.")
    if evidence.get("plugin_version") != "0.3.5":
        raise ValueError("Evidence was not produced by plugin 0.3.5.")
    audit = evidence.get("photoshop", {}).get("structural_audit", {})
    if not audit.get("passed") or audit.get("differences"):
        raise ValueError("Evidence reports a failed structural audit.")

    baseline = PSDImage.open(baseline_path)
    output = PSDImage.open(output_path)
    baseline_layers = dict(walk_layers(baseline))
    output_layers = dict(walk_layers(output))

    baseline_shape = (baseline.width, baseline.height, baseline.depth, int(baseline.color_mode))
    output_shape = (output.width, output.height, output.depth, int(output.color_mode))
    if baseline_shape != (790, 1115, 8, 3) or output_shape != baseline_shape:
        raise ValueError(f"PSD canvas, depth, or color mode changed: {baseline_shape} -> {output_shape}")
    if set(baseline_layers) != set(output_layers):
        raise ValueError("Output PSD layer paths differ from the baseline.")

    target_results = []
    for path, (expected_id, expected_name, source_name, source_hash) in EXPECTED_TARGETS.items():
        layer = output_layers[path]
        if layer.kind != "smartobject" or layer.layer_id != expected_id:
            raise ValueError(f"Target {path} is no longer the approved smart object.")
        if str(layer.name).strip() != expected_name:
            raise ValueError(f"Target {path} has an unexpected name: {layer.name!r}")
        payload_hash = smart_payload_hash(layer)
        if payload_hash != source_hash:
            raise ValueError(f"Target {path} payload does not match {source_name}: {payload_hash}")
        if not layer.visible or not layer.clipping:
            raise ValueError(f"Target {path} lost visibility or clipping.")
        target_results.append(
            {
                "path": list(path),
                "layer_id": layer.layer_id,
                "name": layer.name,
                "source_name": source_name,
                "payload_sha256": payload_hash,
                "editable_smart_object": True,
            }
        )

    unchanged_non_target_layers = 0
    decoded_pixel_layers = 0
    for path in sorted(baseline_layers):
        before_layer = baseline_layers[path]
        after_layer = output_layers[path]
        before = layer_metadata(before_layer, include_pixels=path not in EXPECTED_TARGETS)
        after = layer_metadata(after_layer, include_pixels=path not in EXPECTED_TARGETS)
        if before["kind"] != after["kind"] or before["layer_id"] != after["layer_id"]:
            raise ValueError(f"Layer identity changed at path {path}.")
        if path in EXPECTED_TARGETS:
            continue

        keys = [
            "name",
            "visible",
            "opacity",
            "blend_mode",
            "clipping",
            "bbox",
            "text",
            "smart_payload_sha256",
            "pixel_sha256",
        ]
        changed = [key for key in keys if before[key] != after[key]]
        if changed:
            raise ValueError(f"Non-target layer changed at path {path}: {', '.join(changed)}")
        unchanged_non_target_layers += 1
        if before["pixel_sha256"] is not None:
            decoded_pixel_layers += 1

    return {
        "schema": "uxp-tv06-return-deep-verification/v1",
        "passed": True,
        "baseline_sha256": sha256_file(baseline_path),
        "output_psd_sha256": sha256_file(output_path),
        "evidence_sha256": sha256_file(evidence_path),
        "document": {
            "width": output.width,
            "height": output.height,
            "depth": output.depth,
            "color_mode": int(output.color_mode),
            "layer_count_recursive": len(output_layers),
            "type_layer_count": sum(layer.kind == "type" for layer in output_layers.values()),
        },
        "targets": target_results,
        "unchanged_non_target_layers": unchanged_non_target_layers,
        "decoded_pixel_layers_compared": decoded_pixel_layers,
        "document_reopened_by_parser": True,
        "manual_photoshop_reopen_screenshot_required": True,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--baseline", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--evidence", required=True, type=Path)
    args = parser.parse_args()
    try:
        print(json.dumps(verify(args.baseline, args.output, args.evidence), ensure_ascii=True, indent=2))
        return 0
    except Exception as error:
        print(
            json.dumps(
                {
                    "schema": "uxp-tv06-return-deep-verification/v1",
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
