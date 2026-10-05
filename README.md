# EasyEDA Loader for Altium Designer

EasyEDA Loader 0.3.0 is one Altium Designer extension for searching EasyEDA/LCSC, previewing source models, creating reusable native Altium libraries, and placing linked schematic components. The UI runs as a non-modal WPF window inside Altium. It does not use the former companion executable or DelphiScript request listener.

The tested target is Altium Designer 26.9.1.10 on Windows x64. The extension targets the .NET 8 desktop runtime hosted by that Altium release. It uses EasyEDA/LCSC service responses; endpoint availability and model licensing remain the provider's responsibility. GPL-3.0 source and attribution are included with each package.

## Features

- **Online components** searches by LCSC number, manufacturer part number, or keyword, validates models, and previews the source symbol and footprint.
- **Import to library** creates `.SchLib` and `.PcbLib` files, saves and reopens them in Altium, validates the exact names, publishes an immutable revision, links the final footprint path, and installs the libraries.
- **Import and place** captures the active schematic, performs the same verified import, then calls Altium's native interactive placement. Escape cancels placement and normal undo remains available.
- **Offline library** searches persistent revisions and places them without network access.
- **Import local files** accepts a `.SchLib`/`.PcbLib` pair or `.IntLib`.
- **LCSC website** uses an isolated WebView2 profile for supplementary browsing.
- **Logs and journals** retain conversion details and durable operation state for diagnosis without replaying uncertain placement.

Unsupported source layers or geometry block import with an explicit error. 3D models are optional and outside this release gate.

## Build

Run from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\build\Build.ps1 -Configuration Release
```

The script stops on restore, compilation, test, dependency, or packaging failure. It creates:

- `artifacts\publish-0.3.0`
- `artifacts\EasyEDA-Loader-0.3.0-win-x64.zip`

The package contains one extension, its x64 WebView2 and SQLite native dependencies, installer scripts, documentation, attribution, and corresponding source. Altium assemblies are referenced from the installed product and are not redistributed.

## Install or upgrade from 0.2.4

1. Close Altium Designer and the old **Altium LCSC Bridge** companion.
2. Extract `EasyEDA-Loader-0.3.0-win-x64.zip`.
3. Open Windows PowerShell as Administrator in the extracted directory.
4. Run:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install.ps1
```

The installer backs up `ExtensionsRegistry.xml`, the existing EasyEDA Loader extension, and `Documents\AltiumScripts\LcscBridge`. It changes only extension entries whose HRID is `EasyEDA-Loader` or `Altium LCSC Bridge`, preserving VaultExplorer and unrelated extensions. After successful registration it removes the old companion shortcut/application and retires the Bridge script folder. Component libraries, settings, logs, journals, and browser profiles are preserved.

Restart Altium and use the existing **EasyEDA Loader** toolbar/menu command (`EasyEDA-Loader:EasyEDARun`). Repeated clicks activate the same window.

If the manually added **LCSC Browser** button remains, remove it once in Altium: right-click a toolbar, choose **Customize**, select or drag the obsolete **LCSC Browser** command off the toolbar, then close Customize. Do not remove the **EasyEDA Loader** command.

## Use

Open a schematic before choosing **Import and place**. In **Online components**, search, select a result, review both previews and the validation message, then select **Import to library** or **Import and place**. The window temporarily hides while Altium runs interactive placement and returns afterward.

Altium stores reusable revisions under the configured library root, which defaults to `Documents\LCSC`:

```text
Documents\LCSC\parts\<part>\<revision>\
  schlib.schlib
  pcblib.pcblib
  source.json
  manifest.json
```

The SQLite index at `Documents\LCSC\catalog.sqlite3` is rebuildable. Manifests and library files are authoritative.

Runtime state remains in `%LOCALAPPDATA%\AltiumLcscBridge`:

- `Logs` — UI and Altium adapter logs
- `Operations` — durable in-process operation journals
- `WebView2` and `PreviewWebView2` — isolated browser profiles
- `settings.ini` — selected library root

Select **Open logs folder** in EasyEDA Loader when reporting a problem. Include the request ID shown in the error and the matching `easyeda-loader-YYYYMMDD.log`, `altium-adapter-YYYYMMDD.log`, and operation journal. Log files can contain component identifiers and local paths.

WebView2 Evergreen Runtime is required for embedded browsing and previews. Missing WebView2 disables those views while offline search and local library operations remain available.

## Uninstall and rollback

Run `Uninstall.ps1` from an elevated PowerShell window while Altium is closed. It removes the extension and legacy application remnants while preserving user libraries and `%LOCALAPPDATA%\AltiumLcscBridge`.

Installer backups are stored under `%ProgramData%\AltiumLcscBridge\Backups`. To roll back, close Altium, restore the saved extension directory and matching `ExtensionsRegistry.xml`, then restart Altium. Release 0.2.4 remains available from commit `45e6311` on `main`.

See [validation](docs/VALIDATION.md) for release gates and recorded limitations.
