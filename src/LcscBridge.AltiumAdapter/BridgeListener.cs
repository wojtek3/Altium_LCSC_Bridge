// Adapted for Altium LCSC Bridge from EasyEDALoader, GPL-3.0.
using Newtonsoft.Json;
using PCB;
using SCH;
using DXP;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace EasyEDA_Loader
{
    internal sealed class BridgeListener
    {
        private const string Protocol = "3";
        private readonly Timer timer;
        private bool busy;

        public BridgeListener()
        {
            try { WriteCapabilities(); AdapterLog.Info("startup", "Online-import adapter 0.2.4 started in Altium " + Application.ProductVersion + "."); }
            catch (Exception ex) { AdapterLog.Error("startup", ex); }
            timer = new Timer { Interval = 750 };
            timer.Tick += Tick;
            timer.Start();
        }

        private static string BridgeRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AltiumLcscBridge", "Bridge");

        private static void WriteCapabilities()
        {
            Directory.CreateDirectory(BridgeRoot);
            var final = Path.Combine(BridgeRoot, "adapter.capabilities");
            var temp = final + ".tmp";
            File.WriteAllLines(temp, new[]
            {
                "protocol=" + Protocol, "createOnlineLibraries=true", "onlineOperationStatus=true",
                "adapterVersion=0.2.4", "processId=" + System.Diagnostics.Process.GetCurrentProcess().Id,
                "altiumVersion=" + Application.ProductVersion
            }, Encoding.Unicode);
            File.Move(temp, final, true);
        }

        private void Tick(object sender, EventArgs args)
        {
            if (busy) return;
            try { WriteCapabilities(); } catch (Exception ex) { AdapterLog.Error("heartbeat", ex); }
            var requests = Path.Combine(BridgeRoot, "online-requests");
            if (!Directory.Exists(requests)) return;
            var request = Directory.GetFiles(requests, "*.request").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (request == null) return;
            busy = true;
            try { Process(request, BridgeRoot); }
            finally { busy = false; }
        }

        private static void Process(string requestPath, string bridgeRoot)
        {
            var claimed = Path.ChangeExtension(requestPath, ".processing");
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var requestId = Path.GetFileNameWithoutExtension(requestPath);
            var failedStage = "claiming";
            try
            {
                File.Move(requestPath, claimed);
                values = ReadValues(claimed);
                requestId = Required(values, "requestId");
                Require(values, "protocol", Protocol);
                Require(values, "operation", "createOnlineLibraries");
                WriteStatus(bridgeRoot, requestId, "running", "validating-source", "Validating staged EasyEDA data.");
                AdapterLog.Info("request", "Claimed online library request.", requestId);

                var activeDocument = AltiumApi.GlobalVars.Client.GetCurrentView()?.GetOwnerDocument();
                var activePath = activeDocument?.GetFileName() ?? "";
                values["targetDocument"] = string.Equals(Path.GetExtension(activePath), ".SchDoc", StringComparison.OrdinalIgnoreCase) ? activePath : "";
                AdapterLog.Info("target", string.IsNullOrWhiteSpace(values["targetDocument"])
                    ? "No active schematic was captured for placement." : "Captured placement target: " + values["targetDocument"], requestId);

                var libraryRoot = Path.GetFullPath(Required(values, "libraryRoot"));
                var staging = Path.GetFullPath(Required(values, "stagingDirectory"));
                var payload = Path.GetFullPath(Required(values, "payloadPath"));
                if (!IsInside(staging, libraryRoot) || !IsInside(payload, staging)) throw new InvalidOperationException("Online import path escaped the configured library root.");
                Directory.CreateDirectory(staging);

                var source = JsonConvert.DeserializeObject<Root>(File.ReadAllText(payload, Encoding.UTF8));
                if (source == null || !source.Success || source.Component == null) throw new InvalidDataException("The staged EasyEDA payload is invalid.");
                var footprint = source.Component.PackageDetail?.Footprint ?? throw new InvalidDataException("Footprint data is missing.");
                var symbol = source.Component.Symbol ?? throw new InvalidDataException("Symbol data is missing.");
                if (footprint.Shapes == null || footprint.Shapes.Any(x => x == null)) throw new InvalidDataException("The footprint contains an unsupported primitive.");
                if (symbol.Shapes == null || symbol.Shapes.Any(x => x == null)) throw new InvalidDataException("The symbol contains an unsupported primitive.");
                var packageName = footprint.Head.Parameters.Package;
                var symbolName = symbol.Head.Parameters.Name;
                if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(symbolName)) throw new InvalidDataException("The source model has an empty symbol or footprint name.");

                var pcbPath = Path.Combine(staging, "online.PcbLib");
                var schPath = Path.Combine(staging, "online.SchLib");
                CreateLibraries(source, pcbPath, schPath, packageName, symbolName, values, requestId,
                    (stage, detail) => { failedStage = stage; WriteStatus(bridgeRoot, requestId, "running", stage, detail); });
                failedStage = "adapter-complete";
                WriteResponse(bridgeRoot, values, "ok", "Native Altium libraries created and verified by the adapter.", schPath, pcbPath, symbolName, packageName, "", "", staging);
                WriteStatus(bridgeRoot, requestId, "running", "adapter-complete", "Native libraries verified; waiting for companion publication.");
                AdapterLog.Info("complete", "Native libraries created, saved, reopened, and verified.", requestId);
                File.Delete(claimed);
            }
            catch (Exception ex)
            {
                AdapterLog.Error(failedStage, ex, requestId);
                var errorCode = ErrorCode(ex);
                var staging = Value(values, "stagingDirectory");
                if (string.IsNullOrWhiteSpace(Value(values, "requestId"))) values["requestId"] = requestId;
                TryWriteResponse(bridgeRoot, values, "error", ex.Message, "", "", "", "", errorCode, failedStage, staging);
                TryWriteStatus(bridgeRoot, requestId, "failed", failedStage, errorCode + ": " + ex.Message);
                var failed = claimed + ".failed";
                if (File.Exists(claimed)) File.Move(claimed, failed, true);
            }
        }

        private static void CreateLibraries(Root source, string pcbPath, string schPath, string packageName, string symbolName,
            Dictionary<string, string> values, string requestId, Action<string, string> report)
        {
            IServerDocument pcbDocument = null, verifyPcbDocument = null, schDocument = null, verifySchDocument = null;
            var pcbProcessing = false;
            var succeeded = false;
            try
            {
                DeleteIfExists(pcbPath); DeleteIfExists(schPath);
                report("activating-pcb-library", "Creating and activating the staging PCB library.");
                pcbDocument = AltiumApi.GlobalVars.Client.OpenDocument("PcbLib", pcbPath) ?? throw new InvalidOperationException("Altium could not create the PCB library.");
                ActivateDocument(pcbDocument, pcbPath, "PCB library", requestId);
                var pcbLibrary = AltiumApi.GlobalVars.PCBServer.GetCurrentPCBLibrary() ?? throw new InvalidOperationException("Altium did not activate the PCB library.");
                var footprint = EEPCB.CreateFootprintInLib(pcbLibrary, packageName, source.Component.PackageDetail.Title) ?? throw new InvalidOperationException("Altium could not create the footprint.");
                var actualFootprintName = footprint.GetState_Pattern();
                if (!string.Equals(actualFootprintName, packageName, StringComparison.Ordinal))
                    throw new InvalidOperationException("Altium changed the footprint name from '" + packageName + "' to '" + actualFootprintName + "'. The library was not published.");

                report("converting-footprint", "Converting footprint geometry and layers.");
                AltiumApi.GlobalVars.PCBServer.PreProcess(); pcbProcessing = true;
                source.Component.PackageDetail.Footprint.AddToComponent(footprint, new EeFootprintContext
                {
                    Box = source.Component.PackageDetail.Footprint.BoundingBox,
                    Layers = source.Component.PackageDetail.Footprint.Layers,
                    Exception = ex => { throw ex; }
                });
                AltiumApi.GlobalVars.PCBServer.PostProcess(); pcbProcessing = false;

                report("saving-footprint", "Saving the staging PCB library.");
                pcbDocument.DoFileSave("PcbLib");
                if (pcbLibrary.GetComponentByName(packageName) == null) throw new InvalidOperationException("The generated footprint is not present in the active PCB library: " + packageName);
                AdapterLog.Info("pcb-save", "Saved footprint '" + packageName + "' to " + pcbPath + ".", requestId);
                CloseDocument(ref pcbDocument, false, requestId);

                report("verifying-footprint", "Reopening and verifying the exact footprint.");
                verifyPcbDocument = AltiumApi.GlobalVars.Client.OpenDocument("PcbLib", pcbPath) ?? throw new InvalidOperationException("Altium could not reopen the saved PCB library.");
                ActivateDocument(verifyPcbDocument, pcbPath, "saved PCB library", requestId);
                var reopenedPcbLibrary = AltiumApi.GlobalVars.PCBServer.GetCurrentPCBLibrary() ?? throw new InvalidOperationException("Altium did not activate the saved PCB library.");
                if (reopenedPcbLibrary.GetComponentByName(packageName) == null) throw new InvalidOperationException("The saved PCB library does not contain footprint '" + packageName + "'.");
                CloseDocument(ref verifyPcbDocument, false, requestId);

                report("creating-symbol", "Creating the staging schematic symbol.");
                schDocument = AltiumApi.GlobalVars.Client.OpenDocument("SchLib", schPath) ?? throw new InvalidOperationException("Altium could not create the schematic library.");
                ActivateDocument(schDocument, schPath, "schematic library", requestId);
                var schLibrary = EESCH.GetCurrentSchLibrary() ?? throw new InvalidOperationException("Altium did not activate the schematic library.");
                var component = EESCH.CreateComponent(symbolName, Value(values, "description"), source.Component.Symbol.Head.Parameters.Pre);
                SymbolDrawing.CreateComponent(schLibrary, component, pcbPath, packageName, source.Component.Symbol, false);
                EESCH.AddParameter(component, "Manufacturer", Value(values, "manufacturer"));
                EESCH.AddParameter(component, "Manufacturer Part Number", Value(values, "manufacturerPartNumber"));
                EESCH.AddParameter(component, "LCSC Part Number", Value(values, "supplierPartNumber"));
                EESCH.AddParameter(component, "Source", "EasyEDA/LCSC");
                EESCH.AddParameter(component, "Source Revision", Value(values, "providerRevision"));

                report("saving-symbol", "Saving the staging schematic library.");
                schDocument.DoFileSave("SchLib");
                if (schLibrary.GetState_SchComponentByLibRef(symbolName) == null) throw new InvalidOperationException("The generated symbol is not present in the active schematic library: " + symbolName);
                CloseDocument(ref schDocument, false, requestId);

                report("verifying-symbol", "Reopening and verifying the exact schematic symbol.");
                verifySchDocument = AltiumApi.GlobalVars.Client.OpenDocument("SchLib", schPath) ?? throw new InvalidOperationException("Altium could not reopen the saved schematic library.");
                ActivateDocument(verifySchDocument, schPath, "saved schematic library", requestId);
                var reopenedSchLibrary = EESCH.GetCurrentSchLibrary() ?? throw new InvalidOperationException("Altium did not activate the saved schematic library.");
                if (reopenedSchLibrary.GetState_SchComponentByLibRef(symbolName) == null) throw new InvalidOperationException("The saved schematic library does not contain symbol '" + symbolName + "'.");
                CloseDocument(ref verifySchDocument, false, requestId);

                if (!File.Exists(pcbPath) || !File.Exists(schPath)) throw new IOException("Altium did not save both native library files.");
                if (new FileInfo(pcbPath).Length == 0 || new FileInfo(schPath).Length == 0) throw new IOException("Altium saved an empty native library file.");
                succeeded = true;
            }
            finally
            {
                if (pcbProcessing) { try { AltiumApi.GlobalVars.PCBServer.PostProcess(); } catch (Exception ex) { AdapterLog.Error("pcb-postprocess", ex, requestId); } }
                CloseDocument(ref verifySchDocument, !succeeded, requestId);
                CloseDocument(ref schDocument, !succeeded, requestId);
                CloseDocument(ref verifyPcbDocument, !succeeded, requestId);
                CloseDocument(ref pcbDocument, !succeeded, requestId);
                if (!succeeded) { TryDelete(pcbPath, requestId); TryDelete(schPath, requestId); }
                RestoreTarget(Value(values, "targetDocument"), requestId);
            }
        }

        private static void CloseDocument(ref IServerDocument document, bool discard, string requestId)
        {
            if (document == null) return;
            try
            {
                if (discard) document.SetModified(false);
                AltiumApi.GlobalVars.Client.CloseDocument(document);
            }
            catch (Exception ex) { AdapterLog.Error("close-document", ex, requestId); }
            finally { document = null; }
        }

        private static void RestoreTarget(string targetPath, string requestId)
        {
            if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath)) return;
            try
            {
                var target = AltiumApi.GlobalVars.Client.OpenDocument("SCH", targetPath);
                if (target != null) ActivateDocument(target, targetPath, "original schematic", requestId);
            }
            catch (Exception ex) { AdapterLog.Error("restore-target", ex, requestId); }
        }

        private static void ActivateDocument(IServerDocument document, string expectedPath, string description, string requestId)
        {
            AltiumApi.GlobalVars.Client.ShowDocument(document);
            for (var attempt = 1; attempt <= 20; attempt++)
            {
                Application.DoEvents();
                var currentPath = AltiumApi.GlobalVars.Client.GetCurrentView()?.GetOwnerDocument()?.GetFileName();
                if (!string.IsNullOrWhiteSpace(currentPath) && string.Equals(Path.GetFullPath(currentPath), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
                {
                    AdapterLog.Info("activate", "Activated " + description + " after " + attempt + " attempt(s): " + expectedPath, requestId);
                    return;
                }
                System.Threading.Thread.Sleep(25);
            }
            throw new InvalidOperationException("Altium did not activate the expected " + description + ": " + expectedPath);
        }

        private static string ErrorCode(Exception ex)
        {
            if (ex is EEPCB.LayerMapException) return "UNSUPPORTED_LAYER";
            if (ex is InvalidDataException || ex is JsonException) return "INVALID_SOURCE";
            if (ex is IOException || ex is UnauthorizedAccessException) return "IO_ERROR";
            return "ALTIUM_CONVERSION_ERROR";
        }

        private static void WriteStatus(string bridgeRoot, string requestId, string state, string stage, string detail)
        {
            var directory = Path.Combine(bridgeRoot, "status"); Directory.CreateDirectory(directory);
            var final = Path.Combine(directory, requestId + ".status"); var temp = final + ".tmp";
            File.WriteAllLines(temp, new[] { "protocol=" + Protocol, "requestId=" + requestId, "state=" + state,
                "stage=" + stage, "updatedUtc=" + DateTimeOffset.UtcNow.ToString("O"), "detail=" + Clean(detail) }, Encoding.Unicode);
            File.Move(temp, final, true);
        }

        private static void TryWriteStatus(string root, string id, string state, string stage, string detail)
        { try { WriteStatus(root, id, state, stage, detail); } catch (Exception ex) { AdapterLog.Error("status", ex, id); } }

        private static Dictionary<string, string> ReadValues(string path) => File.ReadAllLines(path, Encoding.Unicode)
            .Select(x => x.Split(new[] { '=' }, 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1], StringComparer.OrdinalIgnoreCase);
        private static string Required(Dictionary<string, string> values, string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidDataException("Missing request field: " + key);
        private static string Value(Dictionary<string, string> values, string key) => values.TryGetValue(key, out var value) ? value : "";
        private static void Require(Dictionary<string, string> values, string key, string expected) { if (!string.Equals(Value(values, key), expected, StringComparison.Ordinal)) throw new InvalidDataException("Unsupported " + key + "."); }
        private static string Clean(string value) => (value ?? "").Replace("\r", " ").Replace("\n", " ");
        private static bool IsInside(string child, string parent) { var relative = Path.GetRelativePath(parent, child); return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative); }
        private static void DeleteIfExists(string path) { if (File.Exists(path)) File.Delete(path); }
        private static void TryDelete(string path, string requestId) { try { DeleteIfExists(path); } catch (Exception ex) { AdapterLog.Error("cleanup-file", ex, requestId); } }

        private static void TryWriteResponse(string root, Dictionary<string, string> request, string status, string message, string schPath, string pcbPath,
            string symbolName, string footprintName, string errorCode, string failedStage, string staging)
        { try { WriteResponse(root, request, status, message, schPath, pcbPath, symbolName, footprintName, errorCode, failedStage, staging); } catch (Exception ex) { AdapterLog.Error("response", ex, Value(request, "requestId")); } }

        private static void WriteResponse(string bridgeRoot, Dictionary<string, string> request, string status, string message,
            string schPath, string pcbPath, string symbolName, string footprintName, string errorCode, string failedStage, string staging)
        {
            var directory = Path.Combine(bridgeRoot, "responses"); Directory.CreateDirectory(directory);
            var requestId = Value(request, "requestId"); if (string.IsNullOrWhiteSpace(requestId)) requestId = Guid.NewGuid().ToString("N");
            var fields = new Dictionary<string, string>
            {
                ["protocol"] = Protocol, ["operation"] = "onlineCreate", ["requestId"] = requestId, ["status"] = status,
                ["message"] = Clean(message), ["errorCode"] = errorCode, ["failedStage"] = failedStage, ["stagingDirectory"] = staging,
                ["schLibPath"] = schPath, ["pcbLibPath"] = pcbPath, ["symbolReference"] = symbolName, ["footprintName"] = footprintName,
                ["placeAfterImport"] = Value(request, "placeAfterImport"), ["manufacturer"] = Value(request, "manufacturer"),
                ["manufacturerPartNumber"] = Value(request, "manufacturerPartNumber"), ["supplierPartNumber"] = Value(request, "supplierPartNumber"),
                ["description"] = Value(request, "description"), ["package"] = Value(request, "package"), ["datasheetUrl"] = Value(request, "datasheetUrl"),
                ["providerModelId"] = Value(request, "providerModelId"), ["providerRevision"] = Value(request, "providerRevision"), ["targetDocument"] = Value(request, "targetDocument")
            };
            var temp = Path.Combine(directory, requestId + ".response.tmp"); var final = Path.Combine(directory, requestId + ".response");
            File.WriteAllLines(temp, fields.Select(x => x.Key + "=" + x.Value), Encoding.Unicode); File.Move(temp, final, true);
        }
    }
}
