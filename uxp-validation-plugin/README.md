# UXP validation probe

This is a deliberately small Photoshop UXP Manifest v5 probe. It is a validation fixture, not the product panel or a production renderer.

Version `0.4.7` retains the approved whole-product renderer and adds one tightly constrained TV-13 failure-isolation checkpoint. The TV-13 plan must stop after exactly two completed JPG pages and before the approved third page; the adapter removes every output from that failed run and keeps its structured failure evidence in memory. A subsequent normal six-page run in the same plugin session embeds that prior failure evidence in the success evidence, proving cleanup and continued execution together. The checkpoint cannot be moved or assigned an arbitrary error code by a RenderPlan.

The mutation probe accepts only the fixture-specific `single_frame_copy`, `multi_frame_copy`, and `whole_product_copy` modes. Each page preflights all target paths, smart-object kinds, visibility, clipping flags, window bases, lower replacement-stack layers, registered frames, and unique Photoshop layer IDs before its first replacement. The whole-product mode processes exactly six calibrated non-size pages and eleven frames, emits six JPG files without PSD files, and removes the whole run's outputs if any page fails. UXP exposes layer collections top-to-bottom, so the adapter converts each audited PSD index before resolving the layer. Layer names are recorded only as audit cross-check evidence.

After replacement, the adapter compares recursive before/after structure snapshots. Target smart objects may change bounds to satisfy their calibrated cover geometry; all other recorded layer identity, visibility, clipping, mask, opacity, blend-mode, hierarchy, and bounds properties must remain unchanged before output is saved.

For single-frame and four-frame validation, the plugin saves an editable PSD copy before converting the unsaved working document to `sRGB IEC61966-2.1`. Whole-product validation intentionally skips PSD output and follows the default JPG-only workflow. It reads back Photoshop's actual document profile, requests an embedded profile during JPG export, closes every input PSD copy without saving, then parses each exported JPEG. An existing complete sRGB profile is preserved. If a JPEG has no ICC profile, the adapter inserts a deterministic 588-byte `sRGB built-in` profile without recompressing image pixels, reads the file again, and only then records success evidence. A malformed or non-sRGB profile is rejected rather than overwritten.

Profile provenance and redistribution information are recorded in `THIRD_PARTY_NOTICES.md`.

## Local preflight

From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/validate_uxp_prototype.ps1
```

The script only parses the manifest and sample plan. It does not require Photoshop and writes no output.

The minimal desktop-side handshake peer uses only Windows PowerShell and binds to loopback:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/uxp_validation_bridge.ps1
```

It accepts one valid `POST /handshake` by default and then exits. It cannot execute Photoshop actions or arbitrary commands.

## Photoshop handoff

1. Copy this directory to the test machine without copying user PSDs into it.
2. Install it through the Photoshop UXP Developer Tool after recording the exact Photoshop version.
3. Do not open the fixture PSD manually. Close any already-open validation-copy PSD selected by the loaded plan.
4. Record the probe first, then test the localhost bridge only when a desktop bridge is running on `127.0.0.1:17842`. Version `0.4.7` still carries the temporary diagnostic `network.domains: "all"`; this is not the production permission policy.
5. Load the plan required by the current operation card. TV-11 uses `render-plans/whole-product-a6015-render-plan.json`.
6. Start the loaded plan. TV-11 asks for one folder containing six exact PSD copies, one folder containing the six exact approved source JPG files, and a separate empty output folder.
7. Verify the input hashes before and after the run. A panel success message is not sufficient evidence until all six JPG files, aggregate evidence JSON, visual results, and unchanged input hashes have been checked.
