# EasyEDA Loader 0.3.0 validation

Release qualification targets Altium Designer 26.9.1.10 on Windows x64. Automated checks do not replace the interactive Altium gates below.

## Automated gates

- Restore, build, and tests complete without errors.
- Core and UI target .NET 8; the extension references installed Altium assemblies without redistributing them.
- The release contains `EasyEDA.Loader.UI.dll`, `LcscBridge.Core.dll`, WebView2 managed assemblies, and x64 `WebView2Loader.dll`.
- The release contains Microsoft.Data.Sqlite, SQLitePCLRaw, and x64 `e_sqlite3.dll`.
- Online query encoding, schema validation, exact LCSC identity matching, layer preflight, unsupported-layer rejection, storage deduplication, atomic manifests, and interrupted status behavior pass.
- The package contains GPL source, license, attribution, installer, and uninstaller.

## Altium host gate

1. Upgrade from 0.2.4 with Altium and the old companion closed.
2. Confirm `ExtensionsRegistry.xml` still contains VaultExplorer and every unrelated extension.
3. Start Altium and verify there is no VaultExplorer startup error.
4. Run the existing **EasyEDA Loader** command. Confirm one non-modal **EasyEDA Loader** window opens.
5. Click the command repeatedly, close, minimize, and reopen the window. Confirm the existing window is activated and Altium remains editable.
6. Open **LCSC website** and both component previews. Confirm WebView2 loads with profiles under `%LOCALAPPDATA%\AltiumLcscBridge`.
7. Search the offline index. This verifies SQLite and the converter/UI dependencies coexist in Altium's .NET 8 host.

Any startup, assembly-loading, WPF, WebView2, or SQLite conflict blocks release.

## Native import and placement gate

For an original test component and each supported regression part:

1. Open a schematic and select **Import and place**.
2. Confirm the extension creates a staging PcbLib and SchLib, saves them, closes them, reopens both, and verifies exact footprint/symbol names.
3. Confirm publication creates a revision under `Documents\LCSC\parts` and the installed SchLib references the final revision PcbLib path.
4. Confirm Altium starts native interactive placement on the captured schematic.
5. Place once, undo, repeat, and press Escape. Confirm no component is added at `(0,0)` and cancellation does not mark the library import failed.
6. Run Update PCB/ECO and verify footprint resolution, pin/pad mapping, and nets.
7. Restart Altium, disconnect the network, and place the cached revision again.

Required regression parts include `C7434411`, TSSOP, LQFP, UFQFPN, QFN, exposed-pad, through-hole, slotted-hole, repeated-pad, and copper-region cases. Compare geometry and electrical identities with independent references.

## Failure and recovery gate

- Inject failure during footprint conversion, save, reopen, linking, and placement setup. All staging tabs must close, PCB pre/post processing must balance, and the captured schematic must regain focus.
- Confirm failed source payloads remain diagnostic data and are never published or replayed.
- Confirm duplicate clicks cannot run concurrent native transactions.
- Close the UI during a native transaction. The window must hide while the transaction completes or rolls back and reconnect when reopened.
- Interrupt Altium after publication but before placement. The operation journal must mark placement as uncertain and restart must not replay it.
- Verify locked files, Unicode paths, insufficient space, malformed/oversized provider responses, endpoint schema changes, timeout, and network loss produce actionable request IDs.
- Verify missing WebView2 leaves offline search and local imports usable.

## Upgrade, repair, and removal gate

- Upgrade from 0.2.4 and confirm the old companion shortcut/application and Bridge script folder are retired only after successful registration.
- Confirm the prior script folder, extension, and registry are backed up under `%ProgramData%\AltiumLcscBridge\Backups`.
- Confirm repair preserves library root, manifests, SQLite index, logs, journals, and browser profiles.
- Confirm uninstall preserves user libraries and unrelated Altium extension entries.
- Remove any manually added **LCSC Browser** toolbar button through Altium Customize; preference files are not edited directly.

Record the Altium build, WebView2 runtime version, fixture IDs, pass/fail evidence, and any unavailable interactive gate. An unavailable or failed Altium gate blocks declaring 0.3.0 release-qualified.
