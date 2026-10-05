# Altium 26.9 validation checklist

Run these checks on a disposable project before using imported components in production.

## Companion and online catalog

1. Install the packaged build on Windows x64 without a separately installed .NET runtime and confirm it starts.
2. Open **LCSC website** and search for an LCSC number, a manufacturer part number containing `/`, a phrase containing spaces, and a Unicode term.
3. Verify Back, Forward, Home, Reload/Stop, current address, and **Open in browser**.
4. Follow a link that requests a new window and confirm it opens in the default browser.
5. Restart the companion and confirm the isolated WebView2 profile retains normal website state.
6. Disconnect the network and confirm the Online tab shows a recoverable error while Offline library and Import local files still work.
7. On a machine without WebView2 Runtime, confirm the Online tab offers the official runtime download and external-browser actions.

The embedded site is for live browsing. Validate structured retrieval and model import separately through **Online components**.

## Altium integration

1. Run `DiagnoseBridge` and record the installed Altium build.
2. Import a known-good, self-created SchLib/PcbLib pair with matching pin and pad designators.
3. Confirm the adapter reports success and `manifest.json` checksums are reconciled after the SchLib is saved.
4. Close and reopen Altium; confirm both libraries remain installed and the cached part remains searchable offline.
5. Place the symbol, cancel a second placement, and verify Undo removes only the placed component.
6. Inspect component parameters and its PCB model path/name.
7. Run **Design → Update PCB Document**, execute the ECO, and verify designator, footprint, pads, orientation, and nets.
8. Repeat with a passive, diode, transistor, multi-unit IC, exposed-pad IC, through-hole connector, and a component with repeated pad numbers.
9. Compare geometry and pin/pad mapping to the manufacturer datasheet and trusted source library.
10. Test a locked library, Unicode path, missing footprint, malformed request, interrupted request, library relocation, repair install, and uninstall.

Do not mark M1/M5 complete until these interactive checks pass on Altium 26.9.1.10.
# Validation status

## Automated checks

The release build runs the following checks and stops on any failure:

- URL encoding and HTTP/HTTPS navigation restrictions.
- Persistent import, duplicate reuse, changed-revision handling, index rebuild, and bridge path confinement.
- Exact LCSC match selection and rejection of wrong-part provider responses.
- Rejection of unsupported EasyEDA footprint geometry.
- Protocol 3 online-source staging, atomic operation status, duplicate suppression, and preservation of failed payloads.
- Acceptance of referenced `ComponentPolarityLayer` geometry and rejection of unknown referenced layers with primitive, layer ID, and layer name diagnostics.
- Fourteen-day cleanup of retained failed staging data.

Supported source symbol prefixes are `P`, `R`, `E`, `C`, `A`, `PL`, `PG`, and `PT`. Supported footprint prefixes are `PAD`, `TRACK`, `HOLE`, `VIA`, `CIRCLE`, `ARC`, `RECT`, `TEXT`, `SVGNODE`, and straight polygonal `SOLIDREGION`. Unknown prefixes and solid regions with curved or relative path commands block import.

EasyEDA `ComponentMarkingLayer` and `ComponentPolarityLayer` both map to Altium Mechanical 11. Layer references in `PAD`, `TRACK`, `CIRCLE`, `ARC`, `RECT`, `TEXT`, and `SOLIDREGION` are validated before a request is queued. Unknown layers in the provider layer table are ignored only when no converted primitive references them.

## Required interactive qualification

These gates cannot be established by the standalone test runner and remain required before calling this a production release:

- Compiled extension loading on Altium Designer 26.9.1.10.
- Native SchLib/PcbLib creation, save, close, reopen, and footprint resolution.
- Geometry and electrical comparison against independent fixtures, including multi-unit parts.
- Interactive placement, Escape cancellation, undo, compilation, and PCB ECO.
- Restart/offline reuse, upgrade, repair, relocation, and uninstall on a clean machine.

Until those checks are recorded, the package is an installable beta and not a qualified “flawless” release.
