using DXP;
using EDP;
using LcscBridge.Core;
using Newtonsoft.Json;
using SCH;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace EasyEDA_Loader
{
    internal sealed class InProcessAltiumIntegration : IAltiumIntegration
    {
        private readonly Dispatcher dispatcher;
        private readonly SemaphoreSlim operationLock = new SemaphoreSlim(1, 1);
        private readonly string journalDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AltiumLcscBridge", "Operations");

        public InProcessAltiumIntegration()
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            Directory.CreateDirectory(journalDirectory);
            RecoverInterruptedOperations();
        }

        public string Version => "0.3.0";

        public bool IsAvailable(out string diagnostic)
        {
            try
            {
                if (AltiumApi.GlobalVars.Client == null || AltiumApi.GlobalVars.SCHServer == null || AltiumApi.GlobalVars.PCBServer == null)
                    throw new InvalidOperationException("Altium schematic or PCB services are unavailable.");
                if (EDP.Utils.LoadIntegratedLibraryManager() == null)
                    throw new InvalidOperationException("Altium's integrated library manager is unavailable.");
                diagnostic = "EasyEDA Loader 0.3.0 in-process Altium integration is ready.";
                return true;
            }
            catch (Exception ex)
            {
                diagnostic = "EasyEDA Loader could not initialize its Altium integration: " + ex.Message;
                return false;
            }
        }

        public string CaptureActiveSchematic()
        {
            return dispatcher.Invoke(() =>
            {
                var document = AltiumApi.GlobalVars.Client.GetCurrentView()?.GetOwnerDocument();
                var path = document?.GetFileName();
                return !string.IsNullOrWhiteSpace(path) && string.Equals(Path.GetExtension(path), ".SchDoc", StringComparison.OrdinalIgnoreCase)
                    ? path : null;
            });
        }

        public async Task<NativeLibraryCreationResult> CreateNativeLibrariesAsync(SourceModelBundle bundle, string libraryRoot,
            string requestId, string targetDocument, IProgress<AltiumOperationProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (bundle == null || !bundle.IsImportable) throw new InvalidDataException("The selected source model did not pass conversion validation.");
            if (string.IsNullOrWhiteSpace(requestId)) throw new ArgumentException("A request ID is required.", nameof(requestId));
            var root = Path.GetFullPath(libraryRoot);
            var staging = Path.Combine(root, ".staging", "online-" + requestId);
            var payload = Path.Combine(staging, "easyeda-source.json");
            Directory.CreateDirectory(staging);
            await File.WriteAllBytesAsync(payload, bundle.RawPayload, cancellationToken);
            Report(progress, requestId, "validating-source", "Validating the staged EasyEDA model bundle.");
            WriteJournal(requestId, "running", "validating-source", "Source payload staged.", false);

            Root source;
            try
            {
                source = JsonConvert.DeserializeObject<Root>(Encoding.UTF8.GetString(bundle.RawPayload));
                if (source == null || !source.Success || source.Component == null) throw new InvalidDataException("The EasyEDA source payload is invalid.");
            }
            catch (Exception ex)
            {
                WriteJournal(requestId, "failed", "validating-source", ex.Message, false);
                throw;
            }
            var footprint = source.Component.PackageDetail?.Footprint ?? throw new InvalidDataException("Footprint data is missing.");
            var symbol = source.Component.Symbol ?? throw new InvalidDataException("Symbol data is missing.");
            var footprintName = footprint.Head?.Parameters?.Package;
            var symbolName = symbol.Head?.Parameters?.Name;
            if (string.IsNullOrWhiteSpace(footprintName) || string.IsNullOrWhiteSpace(symbolName))
                throw new InvalidDataException("The source model has an empty symbol or footprint name.");

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["targetDocument"] = targetDocument ?? "",
                ["manufacturer"] = bundle.Component.Metadata.Manufacturer ?? "",
                ["manufacturerPartNumber"] = bundle.Component.Metadata.ManufacturerPartNumber ?? "",
                ["supplierPartNumber"] = bundle.Component.Metadata.SupplierPartNumber ?? "",
                ["description"] = bundle.Component.Metadata.Description ?? "",
                ["package"] = bundle.Component.Metadata.Package ?? "",
                ["datasheetUrl"] = bundle.Component.Metadata.DatasheetUrl ?? "",
                ["providerRevision"] = bundle.ProviderRevision ?? ""
            };
            var pcbPath = Path.Combine(staging, "online.PcbLib");
            var schPath = Path.Combine(staging, "online.SchLib");

            await operationLock.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await dispatcher.InvokeAsync(() => NativeLibraryTransaction.CreateLibraries(source, pcbPath, schPath, footprintName, symbolName,
                    values, requestId, (stage, detail) =>
                    {
                        Report(progress, requestId, stage, detail);
                        WriteJournal(requestId, "running", stage, detail, false);
                    }));
                WriteJournal(requestId, "native-created", "verifying-symbol", "Native libraries were saved and reopened successfully.", false);
                return new NativeLibraryCreationResult(requestId, staging, schPath, pcbPath, symbolName, footprintName, targetDocument);
            }
            catch (Exception ex)
            {
                AdapterLog.Error("native-create", ex, requestId);
                WriteJournal(requestId, "failed", "native-creation", ex.Message, false);
                throw;
            }
            finally { operationLock.Release(); }
        }

        public async Task InstallAndPlaceAsync(LibraryManifest manifest, bool placeAfterInstall, string targetDocument,
            string requestId, IProgress<AltiumOperationProgress> progress = null, CancellationToken cancellationToken = default)
        {
            if (manifest == null || !manifest.IsPlaceable) throw new InvalidDataException("The cached component is not placeable.");
            await operationLock.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await dispatcher.InvokeAsync(() => InstallAndPlaceNative(manifest, placeAfterInstall, targetDocument, requestId, progress));
            }
            catch (Exception ex)
            {
                await dispatcher.InvokeAsync(() => NativeLibraryTransaction.RestoreTarget(targetDocument, requestId));
                AdapterLog.Error("install-place", ex, requestId);
                WriteJournal(requestId, "failed", placeAfterInstall ? "placing" : "installing", ex.Message, false);
                throw;
            }
            finally { operationLock.Release(); }
        }

        private void InstallAndPlaceNative(LibraryManifest manifest, bool place, string targetDocument, string requestId,
            IProgress<AltiumOperationProgress> progress)
        {
            var manager = EDP.Utils.LoadIntegratedLibraryManager() ?? throw new InvalidOperationException("Altium's integrated library manager is unavailable.");
            var schPath = FileFor(manifest, "schlib");
            var pcbPath = FileFor(manifest, "pcblib");
            var intPath = FileFor(manifest, "intlib");
            string placementLibrary;

            if (!string.IsNullOrWhiteSpace(intPath))
            {
                Report(progress, requestId, "installing", "Installing the cached integrated library.");
                manager.InstallLibrary(intPath);
                placementLibrary = intPath;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(schPath) || string.IsNullOrWhiteSpace(pcbPath))
                    throw new FileNotFoundException("The cached SchLib/PcbLib pair is incomplete.");
                Report(progress, requestId, "installing-footprint", "Installing and resolving the final PCB library.");
                manager.InstallLibrary(pcbPath);
                var foundIn = "";
                if (string.IsNullOrWhiteSpace(manager.FindDatafileInStandardLibs(manifest.Metadata.FootprintName, "PCBLIB", pcbPath, false, ref foundIn)))
                    throw new InvalidOperationException("Footprint was not found in the selected PcbLib: " + manifest.Metadata.FootprintName);

                Report(progress, requestId, "linking", "Assigning the final-path footprint to the schematic component.");
                EnsureFootprintLink(manifest, schPath, pcbPath, requestId);
                manager.InstallLibrary(schPath);
                placementLibrary = schPath;
            }

            if (!place)
            {
                NativeLibraryTransaction.RestoreTarget(targetDocument, requestId);
                WriteJournal(requestId, "succeeded", "installed", "Library installation completed.", false);
                Report(progress, requestId, "installed", "Library installation completed.");
                return;
            }

            if (string.IsNullOrWhiteSpace(targetDocument) || !File.Exists(targetDocument) ||
                !string.Equals(Path.GetExtension(targetDocument), ".SchDoc", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The schematic selected when Import and place was clicked is no longer available.");
            Report(progress, requestId, "placing", "Activating the captured schematic and starting interactive placement.");
            var target = AltiumApi.GlobalVars.Client.OpenDocument("SCH", targetDocument)
                ?? throw new InvalidOperationException("Altium could not reopen the target schematic.");
            NativeLibraryTransaction.ActivateDocument(target, targetDocument, "target schematic", requestId);
            var current = AltiumApi.GlobalVars.SCHServer.GetCurrentSchDocument();
            if (current == null || current.GetState_ObjectId() == TObjectId.eSchLib)
                throw new InvalidOperationException("The target is not an editable schematic sheet.");
            WriteJournal(requestId, "placement-starting", "placing", "Interactive placement is starting and will not be replayed.", true);
            if (!manager.PlaceLibraryComponent(manifest.Metadata.SymbolReference, placementLibrary, ""))
                throw new InvalidOperationException("Altium rejected interactive component placement. Check the symbol reference and installed library.");
            current.GraphicallyInvalidate();
            WriteJournal(requestId, "succeeded", "placement-started", "Altium accepted interactive placement.", true);
            Report(progress, requestId, "placement-started", "Altium accepted interactive placement. Escape cancels; normal undo is available.");
        }

        private static void EnsureFootprintLink(LibraryManifest manifest, string schPath, string pcbPath, string requestId)
        {
            var marker = schPath + ".easyeda-link-v3";
            if (File.Exists(marker)) return;
            IServerDocument document = null;
            IServerDocument verifyDocument = null;
            var succeeded = false;
            try
            {
                document = AltiumApi.GlobalVars.Client.OpenDocument("SchLib", schPath)
                    ?? throw new InvalidOperationException("Altium could not open the schematic library for footprint assignment.");
                NativeLibraryTransaction.ActivateDocument(document, schPath, "schematic library", requestId);
                var library = EESCH.GetCurrentSchLibrary() ?? throw new InvalidOperationException("Altium did not activate the schematic library.");
                var component = library.GetState_SchComponentByLibRef(manifest.Metadata.SymbolReference)
                    ?? throw new InvalidOperationException("Symbol reference was not found in the SchLib: " + manifest.Metadata.SymbolReference);
                EESCH.AssignFootprint(component, pcbPath, manifest.Metadata.FootprintName, "");
                EESCH.AddParameter(component, "Manufacturer", manifest.Metadata.Manufacturer);
                EESCH.AddParameter(component, "Manufacturer Part Number", manifest.Metadata.ManufacturerPartNumber);
                EESCH.AddParameter(component, "LCSC/JLCPCB Part Number", manifest.Metadata.SupplierPartNumber);
                EESCH.AddParameter(component, "Description", manifest.Metadata.Description);
                EESCH.AddParameter(component, "Package", manifest.Metadata.Package);
                EESCH.AddParameter(component, "Datasheet", manifest.Metadata.DatasheetUrl);
                EESCH.AddParameter(component, "Source", manifest.Metadata.Source);
                EESCH.AddParameter(component, "Source Revision", manifest.Metadata.SourceRevision);
                document.DoFileSave("SchLib");
                NativeLibraryTransaction.CloseDocument(ref document, false, requestId);

                verifyDocument = AltiumApi.GlobalVars.Client.OpenDocument("SchLib", schPath)
                    ?? throw new InvalidOperationException("Altium could not reopen the linked schematic library.");
                NativeLibraryTransaction.ActivateDocument(verifyDocument, schPath, "linked schematic library", requestId);
                var reopened = EESCH.GetCurrentSchLibrary() ?? throw new InvalidOperationException("Altium did not activate the linked schematic library.");
                if (reopened.GetState_SchComponentByLibRef(manifest.Metadata.SymbolReference) == null)
                    throw new InvalidOperationException("The linked library no longer contains symbol '" + manifest.Metadata.SymbolReference + "'.");
                NativeLibraryTransaction.CloseDocument(ref verifyDocument, false, requestId);
                File.WriteAllText(marker, "footprint=" + manifest.Metadata.FootprintName + Environment.NewLine + "pcblib=" + pcbPath, Encoding.UTF8);
                succeeded = true;
            }
            finally
            {
                NativeLibraryTransaction.CloseDocument(ref verifyDocument, !succeeded, requestId);
                NativeLibraryTransaction.CloseDocument(ref document, !succeeded, requestId);
            }
        }

        private static string FileFor(LibraryManifest manifest, string role)
        {
            var file = manifest.Files.FirstOrDefault(x => string.Equals(x.Role, role, StringComparison.OrdinalIgnoreCase));
            if (file == null) return null;
            var path = Path.GetFullPath(Path.Combine(manifest.LibraryDirectory, file.FileName));
            var relative = Path.GetRelativePath(Path.GetFullPath(manifest.LibraryDirectory), path);
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
                throw new InvalidDataException("A manifest library path escaped its revision directory.");
            if (!File.Exists(path)) throw new FileNotFoundException("A cached library file is missing.", path);
            return path;
        }

        private static void Report(IProgress<AltiumOperationProgress> progress, string id, string stage, string detail)
        {
            progress?.Report(new AltiumOperationProgress(id, stage, detail));
            AdapterLog.Info(stage, detail, id);
        }

        private void WriteJournal(string requestId, string state, string stage, string detail, bool placementUncertain)
        {
            try
            {
                var final = Path.Combine(journalDirectory, requestId + ".json");
                var temp = final + ".tmp";
                var value = new
                {
                    schemaVersion = 1,
                    requestId,
                    state,
                    stage,
                    updatedUtc = DateTimeOffset.UtcNow,
                    placementUncertain,
                    detail = (detail ?? "").Replace('\r', ' ').Replace('\n', ' ')
                };
                File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
                File.Move(temp, final, true);
            }
            catch (Exception ex) { AdapterLog.Error("journal", ex, requestId); }
        }

        private void RecoverInterruptedOperations()
        {
            foreach (var path in Directory.EnumerateFiles(journalDirectory, "*.json"))
            {
                try
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(path));
                    var root = json.RootElement;
                    var state = root.TryGetProperty("state", out var stateValue) ? stateValue.GetString() : "";
                    if (state == "succeeded" || state == "failed" || state == "interrupted") continue;
                    var id = root.TryGetProperty("requestId", out var idValue) ? idValue.GetString() : Path.GetFileNameWithoutExtension(path);
                    var uncertain = state == "placement-starting" ||
                        (root.TryGetProperty("placementUncertain", out var uncertainty) && uncertainty.GetBoolean());
                    WriteJournal(id, "interrupted", "startup-recovery",
                        uncertain ? "Altium stopped after placement initiation; placement will not be replayed."
                                  : "Altium stopped before the operation reached a terminal state; the operation will not be replayed.", uncertain);
                }
                catch (Exception ex) { AdapterLog.Error("journal-recovery", ex, Path.GetFileNameWithoutExtension(path)); }
            }
        }
    }
}
