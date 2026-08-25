"""Reproduce the canonical 588-byte sRGB ICC payload used by the UXP probe."""

import base64
import hashlib
import struct

from PIL import ImageCms, __version__ as pillow_version, features


EXPECTED_PILLOW = "12.3.0"
EXPECTED_LCMS = "2.19"
EXPECTED_BYTES = 588
EXPECTED_SHA256 = "14FB97CEB79AB10B4A72C839A16A334CAC46E7991C6A7F46DE4E56136675920D"
FIXED_CREATION_TIME = (2026, 8, 23, 14, 41, 23)


def main() -> None:
    lcms_version = str(features.version_module("littlecms2"))
    if pillow_version != EXPECTED_PILLOW or lcms_version != EXPECTED_LCMS:
        raise RuntimeError(
            f"Expected Pillow {EXPECTED_PILLOW} with LittleCMS {EXPECTED_LCMS}; "
            f"found Pillow {pillow_version} with LittleCMS {lcms_version}."
        )

    profile = bytearray(ImageCms.ImageCmsProfile(ImageCms.createProfile("sRGB")).tobytes())
    profile[24:36] = struct.pack(">6H", *FIXED_CREATION_TIME)
    digest = hashlib.sha256(profile).hexdigest().upper()
    if len(profile) != EXPECTED_BYTES or digest != EXPECTED_SHA256:
        raise RuntimeError(f"Unexpected generated profile: bytes={len(profile)}, sha256={digest}")

    print(f"bytes={len(profile)}")
    print(f"sha256={digest}")
    print(base64.b64encode(profile).decode("ascii"))


if __name__ == "__main__":
    main()
