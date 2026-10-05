# Altium LCSC Bridge

Altium LCSC Bridge is an Altium Designer extension for searching EasyEDA/LCSC components, previewing source models, creating reusable native Altium libraries, and placing linked schematic components. Its interface runs as a non-modal WPF window inside Altium Designer.

The tested target is Altium Designer 26.9.1.10 on Windows x64. The extension uses the .NET 8 desktop runtime hosted by that Altium release. It retrieves component data from EasyEDA/LCSC services; endpoint availability and model licensing remain the provider's responsibility. GPL-3.0 source and attribution are included with each release package.

## Features

- **Online components** searches by LCSC number, manufacturer part number, or keyword, validates models, and previews the source symbol and footprint.
- **Import to library** creates `.SchLib` and `.PcbLib` files, saves and reopens them in Altium, validates the exact names, publishes an immutable revision, links the final footprint path, and installs the libraries.
- **Import and place** captures the active schematic, performs the same verified import, then calls Altium's native interactive placement. Escape cancels placement and normal undo remains available.
- **Offline library** searches persistent revisions and places them without network access.
- **Import local files** accepts a `.SchLib`/`.PcbLib` pair or `.IntLib`.
- **LCSC website** uses an isolated WebView2 profile for supplementary browsing.
- **Logs and journals** retain conversion details and durable operation state for diagnosis without replaying uncertain placement.

Unsupported source layers or geometry block import with an explicit error. 3D models are optional and outside this release gate.

## Install

1. Open the project's [GitHub Releases page](https://github.com/wojtek3/Altium_LCSC_Bridge/releases) and download the latest Windows x64 release package.
2. Extract the downloaded package to a local folder.
3. Close Altium Designer.
4. Open Windows PowerShell as Administrator, change to the extracted folder, and run:

   ```powershell
   .\Install.ps1
   ```

The installer registers the extension with Altium and backs up `ExtensionsRegistry.xml` before changing it. Restart Altium Designer after installation, then open Altium LCSC Bridge from its extension command. The command's internal Altium identifier is `EasyEDA-Loader:EasyEDARun`.

The extension requires Microsoft Edge WebView2 Evergreen Runtime for embedded browsing and previews. If WebView2 is missing, those views are unavailable; offline search and local library operations remain usable.

## Build from source

Building requires Windows, PowerShell, .NET SDK **10.0.401** (pinned in `global.json`), network access to restore packages, and Altium Designer assemblies. The adapter project defaults to `C:\Program Files\Altium\AD23`.

Run from the repository root. Pass the path to your Altium installation if it is different from the default:

```powershell
.\build\Build.ps1 -Configuration Release -AltiumInstallDir 'C:\Program Files\Altium\AD26'
```

The script restores packages, builds the solution, runs automated checks, and creates the release package under `artifacts`. The package contains the extension, x64 WebView2 and SQLite native dependencies, installer scripts, documentation, attribution, and corresponding source. Altium assemblies are referenced from the installed product and are not redistributed.

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

Select **Open logs folder** in the extension window when reporting a problem. Include the request ID shown in the error and the matching `easyeda-loader-YYYYMMDD.log`, `altium-adapter-YYYYMMDD.log`, and operation journal. Log files can contain component identifiers and local paths.

## Uninstall

Run `Uninstall.ps1` from an elevated PowerShell window while Altium is closed. It removes the extension while preserving user libraries and `%LOCALAPPDATA%\AltiumLcscBridge`.

See [validation](docs/VALIDATION.md) for release gates and recorded limitations.
