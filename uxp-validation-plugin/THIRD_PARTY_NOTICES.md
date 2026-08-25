# Third-party notice for the canonical sRGB profile

The 588-byte ICC payload embedded by this validation adapter is generated data. It is not copied from Adobe Photoshop or an Adobe ICC file.

Generation provenance:

- Pillow `12.3.0`, `PIL.ImageCms.createProfile("sRGB")`
- LittleCMS `2.19`
- ICC creation timestamp normalized to `2026-08-23 14:41:23`
- Payload description: `sRGB built-in`
- Embedded copyright text: `No copyright, use freely`
- SHA-256: `14FB97CEB79AB10B4A72C839A16A334CAC46E7991C6A7F46DE4E56136675920D`

The exact generation and verification procedure is in `tools/generate_srgb_icc_profile.py` in the source repository. Validation packages also include a copy under `tools/`.

Pillow is distributed under the HPND License: <https://github.com/python-pillow/Pillow/blob/main/LICENSE>

LittleCMS is distributed under the MIT License: <https://github.com/mm2/Little-CMS/blob/master/COPYING>

Neither Pillow nor LittleCMS runs inside the Photoshop plugin. They are used only to produce and audit the fixed ICC data payload.
