# UXP validation probe

This is a deliberately small Photoshop UXP Manifest v5 probe. It is a validation fixture, not the product panel or a production renderer.

The panel can record a Photoshop environment probe, attempt a localhost handshake, load a renderer-neutral `render-plan/v1` JSON file, and return structured errors. The render button is fail-closed until a Photoshop 2022 copy-based layer mapping has been observed and recorded. It does not accept natural-language Photoshop commands and does not contain AI, template rules, batch state, or output cleanup logic.

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
3. Open a disposable copy of the selected sample PSD.
4. Record the probe first, then test the localhost bridge only when a desktop bridge is running on `127.0.0.1:17842`.
5. Do not enable mutation until the test report names the copy path and pre/post hashes.
