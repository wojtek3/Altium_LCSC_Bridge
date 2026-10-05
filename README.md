# Altium LCSC Bridge

Altium LCSC Bridge helps you find, validate, cache, and use electronic components in Altium Designer. It consists of a Windows companion app, a compiled Altium extension for creating native libraries from EasyEDA/LCSC models, and a DelphiScript adapter for installing cached libraries and placing components.

The application has three separate workflows:

- **Online components** retrieves structured component data and CAD models for conversion into native Altium libraries.
- **LCSC website** is a WebView browser for the live LCSC website. Browsing the website does not download or import CAD models.
- **Offline library** searches revisions already saved on your computer and can ask Altium to install or place them.

## Requirements

### To install and use a release

- Windows x64.
- Altium Designer. The integration targets Altium Designer **26.9.1.10**; other versions have not been qualified.
- Administrator access for installation, upgrade, and uninstall. Altium Designer must be closed during these operations.
- Internet access for online catalog retrieval, live LCSC browsing, and the initial installation if you need to download the package. Cached components remain available without internet access.
- Microsoft Edge WebView2 Evergreen Runtime for the embedded **LCSC website** tab. It is normally present on supported Windows installations. If it is missing, the app offers a runtime download button; you can also open LCSC in your default browser.

The published Windows x64 application is self-contained and does not require a separately installed .NET runtime or Altium Developer SDK on the target computer.

### To build from source

- Windows x64 and PowerShell.
- .NET SDK **10.0.401**, as pinned in `global.json` (a newer patch of .NET 10 is accepted).
- Network access to restore NuGet packages.
- Altium Designer assemblies for building the compiled extension. The adapter project defaults to `C:\Program Files\Altium\AD23`; set the `AltiumInstallDir` environment variable before running the build if Altium is installed elsewhere.

## Install

You can install a packaged release, or build the release package from source first.

### Option A: Install a release package

1. Download the Windows x64 release ZIP from the project's GitHub **Releases** page, when a release is published.
2. Extract the ZIP to a local folder. Keep the extracted files together; the installer uses files beside `Install.ps1`.
3. Close Altium Designer.
4. Open **Windows PowerShell as Administrator**, change to the extracted folder, and run:

   ```powershell
   .\Install.ps1
   ```

The installer copies the companion app to `%LOCALAPPDATA%\Programs\AltiumLcscBridge`, the Altium script and toolbar icon to `Documents\AltiumScripts\LcscBridge`, and the compiled extension into the installed Altium extension directory. It backs up the Altium `ExtensionsRegistry.xml` before updating it, creates a Start menu shortcut, and starts the companion app. Restart Altium after installation so it loads the extension.

### Option B: Build and install from this repository

1. Clone or download this repository.
2. Install the required .NET SDK and make sure Altium is installed. If needed, set the adapter's Altium directory for this PowerShell session:

   ```powershell
   $env:AltiumInstallDir = 'C:\Program Files\Altium\AD26'
   ```

   Replace that example with the directory containing your Altium `System` folder.
3. From the repository root, run:

   ```powershell
   .\build\Build.ps1
   ```

   The script restores packages, builds the solution, runs the automated checks, and creates the self-contained application and ZIP package. Build output is written to `artifacts\publish-0.2.4` and `artifacts\AltiumLcscBridge-win-x64.zip`.
4. Close Altium Designer. Open **Windows PowerShell as Administrator**, change to `artifacts\publish-0.2.4`, and run:

   ```powershell
   .\Install.ps1
   ```

   You can also extract `artifacts\AltiumLcscBridge-win-x64.zip` and run the `Install.ps1` included in the extracted folder.
5. Restart Altium after the installer completes.

## Configure Altium

The installer copies the script project to `Documents\AltiumScripts\LcscBridge`. Add it to Altium and create a toolbar button so the bridge can install cached libraries and place components:

1. In Altium, open **Preferences → Scripting System → Global Projects** and add `Documents\AltiumScripts\LcscBridge\LcscBridge.PrjScr`.
2. Open a schematic document. Open **View → Toolbars → Customize**.
3. Under **Scripts**, find `BridgeAdapter.pas > RunLcscBrowser` and drag it onto a toolbar.
4. Set the button caption to **LCSC Browser**. Set its icon to `Documents\AltiumScripts\LcscBridge\lcsc-bridge.bmp` if desired.
5. Run `BridgeAdapter.pas > DiagnoseBridge` from Altium's script commands. Confirm that both `SchServer` and `IntegratedLibraryManager` are available.

Clicking **LCSC Browser** opens a small, non-modal placement listener and launches the companion app. Keep the listener open while you use **Import and place**, **Place cached component**, or any other workflow that places a component. Open a schematic sheet before placing. You can close the listener after placement work is finished.

The compiled extension and the DelphiScript listener serve different purposes: the compiled extension creates native libraries from online source models, while the listener handles cached-library installation and interactive placement.

## Configure the library folder

On first launch, the persistent library defaults to the Windows Documents folder's `LCSC` subfolder. To choose another location:

1. In the companion app, select **Library settings**.
2. Choose a folder where your Windows account can create and modify files. A local drive is recommended for reliable Altium access.
3. Use that same folder for all bridge operations.

The library root is remembered in `%LOCALAPPDATA%\AltiumLcscBridge\settings.ini`. Changing the root changes which library the app uses; it does not move or delete revisions already stored in the previous folder. To move an existing library, close the app, copy the complete library folder to the new location, then select the copied folder in **Library settings**. Keep the `parts` directory and `catalog.sqlite3` together; if the index is missing or out of date, use **Rebuild index**.

Each imported source revision is stored under `parts\<part>\<revision>`. Its `manifest.json` and native library files are authoritative; `catalog.sqlite3` is a rebuildable search index. Revisions are immutable, so importing changed source data creates a separate revision rather than silently replacing an existing one.

## Use the online component catalog

Use **Online components** when you want the bridge to retrieve and convert a component model:

1. Start Altium and make sure the compiled extension is loaded. If you plan to place the component as part of the import, open a schematic and click **LCSC Browser** to start the placement listener.
2. In the companion app, select **Online components**.
3. Search by LCSC part number, manufacturer part number, or keyword. For exact LCSC-number searches, the app checks that the returned part number matches the requested number.
4. Select a result. Review its metadata, source symbol and footprint previews, and conversion diagnostics.
5. Select **Import to library** to create and cache the native `.SchLib` and `.PcbLib` files. Select **Import and place** to create them and request placement in the active schematic. Keep Altium open and, for placement, leave the listener running.
6. Wait for the status in the companion app to report success or an error. After a successful import, find the revision in **Offline library** and use it again without retrieving the source model.

Import controls are enabled only when the compiled adapter reports the required capability and both source models pass validation. Unsupported or malformed geometry blocks import; it is not silently omitted. Only one online library-creation operation can run at a time. If an operation is still running, wait for it to finish before starting another.

The converter supports the source primitives listed in [validation notes](docs/VALIDATION.md), including straight polygonal solid regions. It rejects curved or relative solid-region paths and unknown geometry because silently omitting copper could produce an unsafe footprint.

## Use the offline library

Use **Offline library** to find components already cached in the configured library folder. Search covers manufacturer, MPN, LCSC/JLCPCB number, description, package, symbol, and footprint.

1. Select a cached revision and review its metadata and symbol/footprint names.
2. Select **Install library only** to ask Altium to install its native library, or **Place cached component** to install it and start placement in the active schematic.
3. Keep Altium open and the **LCSC Browser** listener running for either operation. For placement, keep the target schematic open and follow Altium's normal placement interaction; press Escape to cancel placement.
4. Select **Open datasheet** to open the saved datasheet link, when one is available.

The cache and its search index work offline. Installing libraries into Altium and placing symbols still require Altium to be running.

## Import existing Altium libraries

Use **Import local files** to add libraries you already have to the offline cache:

1. Choose either one `.IntLib`, or one `.SchLib` together with its matching `.PcbLib`. Do not fill in both alternatives.
2. Enter the manufacturer, MPN, supplier part number, description, package, and datasheet URL as available.
3. For a `.SchLib`/`.PcbLib` pair, enter the exact symbol reference and footprint name to use. Altium library files are binary, so the bridge needs these names to identify the component.
4. Select **Import to library** to copy the files into the persistent cache and index them. This step does not require Altium to be open.
5. To install or place the cached files in Altium, select the new revision under **Offline library** and use the corresponding action. Altium must be open; placement also requires the listener and an open schematic.

Imported revisions are retained in the configured library folder. The bridge confines its Altium operations to that folder and does not overwrite an existing revision.

## Browse the LCSC website

The **LCSC website** tab is a separate live browser. Enter a keyword, MPN, or LCSC number and select **Search LCSC**. Use **Back**, **Forward**, **Home**, **Reload**, and **Open in browser** to navigate. Links that request a new window open in the default browser; non-HTTP navigation is blocked.

Website login state is kept in an isolated WebView2 profile at `%LOCALAPPDATA%\AltiumLcscBridge\WebView2`. The bridge does not read or store LCSC credentials itself. Website browsing does not select or import a model; use **Online components** for structured model retrieval.

## Logs and recovery

Select **Open logs folder** in the companion app to open `%LOCALAPPDATA%\AltiumLcscBridge\Logs`. The logs include:

- `companion-YYYYMMDD.log` — search, queue, publish, response, and UI errors.
- `altium-adapter-YYYYMMDD.log` — extension activation, native-library creation, save/reopen verification, and conversion errors.
- `altium-script.log` — cached library operations and interactive placement.

Bridge operations use a request ID that is shared by their related log entries. If you report an issue, include the relevant companion, adapter, and script logs. They can contain local library paths and component identifiers, but do not contain website cookies or credentials.

If a cached item is not visible after copying or restoring a library, select **Rebuild index**. This recreates the SQLite index from component manifests; it does not recreate missing library files. Stale placement requests older than ten minutes are moved to `Bridge\recovery` instead of being replayed. Failed online requests and their source payloads are retained for diagnosis and removed during startup maintenance after 14 days.

## Upgrade and uninstall

To upgrade, close Altium and run the new release's `Install.ps1` from an elevated PowerShell window. The installer backs up the extension registry before updating it. Restart Altium after the upgrade.

To uninstall, close Altium, open PowerShell as Administrator, and run:

```powershell
& "$env:LOCALAPPDATA\Programs\AltiumLcscBridge\Uninstall.ps1"
```

The uninstaller removes the companion app, toolbar script, and registered Altium extension. It preserves your component library under `Documents\LCSC` (or the custom library folder you selected). It also leaves `%LOCALAPPDATA%\AltiumLcscBridge` in place, including settings, logs, bridge recovery data, and the WebView2 browsing profile. Keep a separate backup of the component library if you need to preserve its contents.

## Safety, compatibility, and known limits

- Review every imported symbol and footprint against the manufacturer's datasheet before production use, especially repeated pad numbers, exposed pads, multi-unit symbols, and pin-to-pad mapping.
- Source previews come from EasyEDA SVG responses. The compiled adaptation inherits EasyEDALoader symbol drawing behavior, which may rearrange pins.
- Compiled extension loading, native-library save/reopen, interactive placement, undo, and the PCB ECO flow still require interactive qualification on Altium 26.9.1.10. A successful build or HTTP response alone does not qualify a release. See the [Altium validation checklist](docs/VALIDATION.md).
- Structured retrieval uses EasyEDA endpoints that are not a documented stability guarantee. Schema changes fail explicitly. Confirm that your use and offline retention of each model comply with the provider's terms.
- The embedded website may use WebView2's normal download flow when you explicitly download a file. Downloaded files are not executed or automatically imported. Datasheets in the offline library are stored as links.

## For developers

The core exposes `ICatalogProvider`, `IModelProvider`, `ILibraryStore`, and `IAltiumBridge` interfaces to separate provider retrieval, conversion, storage, and Altium integration. The automated checks run by `build\Build.ps1` cover URL handling, offline storage, provider validation, geometry rejection, and bridge protocol behavior. They do not replace the interactive Altium qualification checklist in `docs\VALIDATION.md`.

The compiled adapter is a GPL-3.0 adaptation of EasyEDALoader. See [third-party notices](THIRD-PARTY-NOTICES.md); the corresponding source and GPL license are included in every packaged build. The license for the adapter does not grant rights to component models downloaded from a provider.
