from __future__ import annotations

import argparse
import hashlib
import json
import math
import subprocess
from pathlib import Path
from typing import Any

from PIL import Image, ImageChops, ImageOps, ImageStat


PROTOCOL_VERSION = "psd-inspection/v1"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def inspect(worker: Path, psd_copy: Path, preview_path: Path) -> dict[str, Any]:
    request = {
        "schema_version": PROTOCOL_VERSION,
        "request_id": "tpl-s0-local-preview",
        "input_path": str(psd_copy.resolve()),
        "preview_path": str(preview_path.resolve()),
    }
    completed = subprocess.run(
        [str(worker.resolve())],
        input=json.dumps(request, ensure_ascii=False),
        text=True,
        encoding="utf-8",
        capture_output=True,
        check=False,
        timeout=60,
    )
    if completed.returncode != 0:
        raise RuntimeError(f"Worker exited with code {completed.returncode}.")
    response = json.loads(completed.stdout)
    if not response.get("ok") or response.get("inspection") is None:
        code = (response.get("error") or {}).get("code", "unknown")
        raise RuntimeError(f"Worker inspection failed: {code}.")
    return response["inspection"]


def load_plan(path: Path) -> list[dict[str, Any]]:
    payload = json.loads(path.read_text(encoding="utf-8-sig"))
    pages = payload.get("pages")
    if not isinstance(pages, list) or len(pages) != 1:
        raise ValueError("Validation plan must contain exactly one page.")
    frames = pages[0].get("frames")
    if not isinstance(frames, list) or not frames:
        raise ValueError("Validation plan contains no frames.")
    return frames


def layer_path(value: str) -> list[int]:
    return [int(part) for part in value.split("/")]


def cover_crop(
    source: Image.Image,
    frame_width: int,
    frame_height: int,
    focal_point: tuple[float, float],
    target_point: tuple[float, float],
    scale_multiplier: float,
    offset: tuple[float, float],
) -> tuple[Image.Image, dict[str, Any]]:
    source = ImageOps.exif_transpose(source).convert("RGB")
    base_scale = max(frame_width / source.width, frame_height / source.height)
    scale = base_scale * scale_multiplier
    scaled_width = max(frame_width, math.ceil(source.width * scale))
    scaled_height = max(frame_height, math.ceil(source.height * scale))
    resized = source.resize((scaled_width, scaled_height), Image.Resampling.LANCZOS)

    anchor_x = focal_point[0] * scaled_width
    anchor_y = focal_point[1] * scaled_height
    desired_x = target_point[0] * frame_width + offset[0]
    desired_y = target_point[1] * frame_height + offset[1]
    left = round(anchor_x - desired_x)
    top = round(anchor_y - desired_y)
    right = left + frame_width
    bottom = top + frame_height
    if left < 0 or top < 0 or right > scaled_width or bottom > scaled_height:
        raise ValueError("Transform exposes pixels outside the source image.")

    crop = resized.crop((left, top, right, bottom))
    mapping_error = max(
        abs((anchor_x - left) - desired_x),
        abs((anchor_y - top) - desired_y),
    )
    return crop, {
        "base_scale": round(base_scale, 8),
        "effective_scale": round(scale, 8),
        "scaled_size": [scaled_width, scaled_height],
        "crop_box": [left, top, right, bottom],
        "mapping_error_px": round(mapping_error, 6),
        "full_cover": crop.size == (frame_width, frame_height),
    }


def mean_absolute_error(left: Image.Image, right: Image.Image) -> float:
    difference = ImageChops.difference(left.convert("RGB"), right.convert("RGB"))
    means = ImageStat.Stat(difference).mean
    return round(sum(means) / (len(means) * 255), 6)


def render(
    base: Image.Image,
    reference: Image.Image,
    frames: list[dict[str, Any]],
    layers: list[dict[str, Any]],
    source_root: Path,
    scale_multiplier: float,
    offset: tuple[float, float],
) -> tuple[Image.Image, list[dict[str, Any]]]:
    output = base.convert("RGB").copy()
    layer_index = {tuple(layer["path"]): layer for layer in layers}
    evidence: list[dict[str, Any]] = []

    for frame in frames:
        target = frame["target"]
        path = layer_path(target["layer_path"])
        layer = layer_index.get(tuple(path))
        if layer is None:
            raise ValueError(f"Registered layer path is missing: {path}.")
        clipping_base = layer.get("clipping_base")
        if clipping_base is None:
            raise ValueError(f"Clipping base is unavailable for path {path}.")
        if clipping_base.get("has_mask") or clipping_base.get("has_vector_mask"):
            raise ValueError(f"Mask extraction is required for path {path}.")

        registered_frame = target["registered_frame"]
        if clipping_base.get("bbox") != registered_frame:
            raise ValueError(f"Clipping base bounds differ for path {path}.")
        left, top, right, bottom = registered_frame
        frame_width = right - left
        frame_height = bottom - top
        if frame_width <= 0 or frame_height <= 0:
            raise ValueError(f"Registered frame is empty for path {path}.")

        source = source_root / Path(frame["source"]["relative_path"]).name
        expected_hash = frame["source"]["sha256"].upper()
        actual_hash = sha256(source)
        if actual_hash != expected_hash:
            raise ValueError(f"Source hash differs for {source.name}.")

        crop_config = frame["crop"]
        with Image.open(source) as source_image:
            crop, geometry = cover_crop(
                source_image,
                frame_width,
                frame_height,
                tuple(crop_config["focal_point"]),
                tuple(frame["target_point"]),
                scale_multiplier,
                offset,
            )
        output.paste(crop, (left, top))
        reference_crop = reference.crop((left, top, right, bottom))
        evidence.append(
            {
                "frame_id": frame["frame_id"],
                "target_path": path,
                "clipping_base_path": clipping_base["path"],
                "registered_frame": registered_frame,
                "source": source.name,
                "source_sha256": actual_hash,
                "reference_mean_absolute_error": mean_absolute_error(crop, reference_crop),
                **geometry,
            }
        )

    return output, evidence


def comparison_image(local: Image.Image, reference: Image.Image) -> Image.Image:
    local = local.convert("RGB")
    reference = reference.convert("RGB")
    if local.size != reference.size:
        raise ValueError("Local and reference previews must have the same canvas size.")
    result = Image.new("RGB", (local.width * 2, local.height), "white")
    result.paste(local, (0, 0))
    result.paste(reference, (local.width, 0))
    return result


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--worker", required=True, type=Path)
    parser.add_argument("--psd-copy", required=True, type=Path)
    parser.add_argument("--plan", required=True, type=Path)
    parser.add_argument("--source-copy-root", required=True, type=Path)
    parser.add_argument("--reference-copy", required=True, type=Path)
    parser.add_argument("--output-dir", required=True, type=Path)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    args.output_dir.mkdir(parents=True, exist_ok=True)
    base_path = args.output_dir / "base-preview.png"
    psd_hash_before = sha256(args.psd_copy)
    inspection = inspect(args.worker, args.psd_copy, base_path)
    psd_hash_after = sha256(args.psd_copy)
    if psd_hash_before != psd_hash_after:
        raise RuntimeError("PSD copy changed during preview validation.")

    frames = load_plan(args.plan)
    with Image.open(base_path) as base_image, Image.open(args.reference_copy) as reference_image:
        reference = ImageOps.exif_transpose(reference_image).convert("RGB")
        local, default_evidence = render(
            base_image,
            reference,
            frames,
            inspection["layers"],
            args.source_copy_root,
            1.0,
            (0.0, 0.0),
        )
        offset_preview, offset_evidence = render(
            base_image,
            reference,
            frames,
            inspection["layers"],
            args.source_copy_root,
            1.15,
            (12.0, -12.0),
        )
        local.save(args.output_dir / "local-preview.png", format="PNG")
        offset_preview.save(args.output_dir / "offset-preview.png", format="PNG")
        comparison_image(local, reference).save(
            args.output_dir / "local-vs-photoshop-reference.png",
            format="PNG",
        )

    evidence = {
        "schema_version": "tpl-s0-local-preview/v1",
        "psd_copy_sha256_before": psd_hash_before,
        "psd_copy_sha256_after": psd_hash_after,
        "source_copy_root": args.source_copy_root.name,
        "reference_copy_sha256": sha256(args.reference_copy),
        "structure_signature": inspection["structure_signature"],
        "default_cover": default_evidence,
        "offset_probe": offset_evidence,
        "passed": all(
            item["full_cover"] and item["mapping_error_px"] <= 0.5
            for item in [*default_evidence, *offset_evidence]
        ),
    }
    evidence_path = args.output_dir / "evidence.json"
    evidence_path.write_text(
        json.dumps(evidence, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    print(json.dumps(evidence, ensure_ascii=False, separators=(",", ":")))
    return 0 if evidence["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
