// Native conversion transaction adapted from EasyEDALoader, GPL-3.0.
using DXP;
using PCB;
using SCH;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Forms = System.Windows.Forms;

namespace EasyEDA_Loader
{
    internal static class NativeLibraryTransaction
    {
        internal static void CreateLibraries(Root source, string pcbPath, string schPath, string packageName, string symbolName,
            Dictionary<string, string> values, string requestId, Action<string, string> report)
        {
            IServerDocument pcbDocument = null, verifyPcbDocument = null, schDocument = null, verifySchDocument = null;
            var pcbProcessing = false;
            var succeeded = false;
            try
            {
                DeleteIfExists(pcbPath);
                DeleteIfExists(schPath);
                report("activating-pcb-library", "Creating and activating the staging PCB library.");
                pcbDocument = AltiumApi.GlobalVars.Client.OpenDocument("PcbLib", pcbPath)
                    ?? throw new InvalidOperationException("Altium could not create the PCB library.");
                ActivateDocument(pcbDocument, pcbPath, "PCB library", requestId);
                var pcbLibrary = AltiumApi.GlobalVars.PCBServer.GetCurrentPCBLibrary()
                    ?? throw new InvalidOperationException("Altium did not activate the PCB library.");
                var footprint = EEPCB.CreateFootprintInLib(pcbLibrary, packageName, source.Component.PackageDetail.Title)
                    ?? throw new InvalidOperationException("Altium could not create the footprint.");
                var actualFootprintName = footprint.GetState_Pattern();
                if (!string.Equals(actualFootprintName, packageName, StringComparison.Ordinal))
                    throw new InvalidOperationException("Altium changed the footprint name from '" + packageName + "' to '" + actualFootprintName + "'.");

                report("converting-footprint", "Converting footprint geometry and layers.");
                AltiumApi.GlobalVars.PCBServer.PreProcess();
                pcbProcessing = true;
                source.Component.PackageDetail.Footprint.AddToComponent(footprint, new EeFootprintContext
                {
                    Box = source.Component.PackageDetail.Footprint.BoundingBox,
                    Layers = source.Component.PackageDetail.Footprint.Layers,
                    Exception = ex => { throw ex; }
                });
                AltiumApi.GlobalVars.PCBServer.PostProcess();
                pcbProcessing = false;

                report("saving-footprint", "Saving the staging PCB library.");
                pcbDocument.DoFileSave("PcbLib");
                if (pcbLibrary.GetComponentByName(packageName) == null)
                    throw new InvalidOperationException("The generated footprint is not present in the active PCB library: " + packageName);
                CloseDocument(ref pcbDocument, false, requestId);

                report("verifying-footprint", "Reopening and verifying the exact footprint.");
                verifyPcbDocument = AltiumApi.GlobalVars.Client.OpenDocument("PcbLib", pcbPath)
                    ?? throw new InvalidOperationException("Altium could not reopen the saved PCB library.");
                ActivateDocument(verifyPcbDocument, pcbPath, "saved PCB library", requestId);
                var reopenedPcbLibrary = AltiumApi.GlobalVars.PCBServer.GetCurrentPCBLibrary()
                    ?? throw new InvalidOperationException("Altium did not activate the saved PCB library.");
                if (reopenedPcbLibrary.GetComponentByName(packageName) == null)
                    throw new InvalidOperationException("The saved PCB library does not contain footprint '" + packageName + "'.");
                CloseDocument(ref verifyPcbDocument, false, requestId);

                report("creating-symbol", "Creating the staging schematic symbol.");
                schDocument = AltiumApi.GlobalVars.Client.OpenDocument("SchLib", schPath)
                    ?? throw new InvalidOperationException("Altium could not create the schematic library.");
                ActivateDocument(schDocument, schPath, "schematic library", requestId);
                var schLibrary = EESCH.GetCurrentSchLibrary()
                    ?? throw new InvalidOperationException("Altium did not activate the schematic library.");
                var component = EESCH.CreateComponent(symbolName, Value(values, "description"), source.Component.Symbol.Head.Parameters.Pre);
                SymbolDrawing.CreateComponent(schLibrary, component, pcbPath, packageName, source.Component.Symbol, false);
                EESCH.AddParameter(component, "Manufacturer", Value(values, "manufacturer"));
                EESCH.AddParameter(component, "Manufacturer Part Number", Value(values, "manufacturerPartNumber"));
                EESCH.AddParameter(component, "LCSC Part Number", Value(values, "supplierPartNumber"));
                EESCH.AddParameter(component, "Source", "EasyEDA/LCSC");
                EESCH.AddParameter(component, "Source Revision", Value(values, "providerRevision"));

                report("saving-symbol", "Saving the staging schematic library.");
                schDocument.DoFileSave("SchLib");
                if (schLibrary.GetState_SchComponentByLibRef(symbolName) == null)
                    throw new InvalidOperationException("The generated symbol is not present in the active schematic library: " + symbolName);
                CloseDocument(ref schDocument, false, requestId);

                report("verifying-symbol", "Reopening and verifying the exact schematic symbol.");
                verifySchDocument = AltiumApi.GlobalVars.Client.OpenDocument("SchLib", schPath)
                    ?? throw new InvalidOperationException("Altium could not reopen the saved schematic library.");
                ActivateDocument(verifySchDocument, schPath, "saved schematic library", requestId);
                var reopenedSchLibrary = EESCH.GetCurrentSchLibrary()
                    ?? throw new InvalidOperationException("Altium did not activate the saved schematic library.");
                if (reopenedSchLibrary.GetState_SchComponentByLibRef(symbolName) == null)
                    throw new InvalidOperationException("The saved schematic library does not contain symbol '" + symbolName + "'.");
                CloseDocument(ref verifySchDocument, false, requestId);

                if (!File.Exists(pcbPath) || !File.Exists(schPath)) throw new IOException("Altium did not save both native library files.");
                if (new FileInfo(pcbPath).Length == 0 || new FileInfo(schPath).Length == 0) throw new IOException("Altium saved an empty native library file.");
                succeeded = true;
            }
            finally
            {
                if (pcbProcessing)
                {
                    try { AltiumApi.GlobalVars.PCBServer.PostProcess(); }
                    catch (Exception ex) { AdapterLog.Error("pcb-postprocess", ex, requestId); }
                }
                CloseDocument(ref verifySchDocument, !succeeded, requestId);
                CloseDocument(ref schDocument, !succeeded, requestId);
                CloseDocument(ref verifyPcbDocument, !succeeded, requestId);
                CloseDocument(ref pcbDocument, !succeeded, requestId);
                if (!succeeded)
                {
                    TryDelete(pcbPath, requestId);
                    TryDelete(schPath, requestId);
                }
                RestoreTarget(Value(values, "targetDocument"), requestId);
            }
        }

        internal static void CloseDocument(ref IServerDocument document, bool discard, string requestId)
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

        internal static void RestoreTarget(string targetPath, string requestId)
        {
            if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath)) return;
            try
            {
                var target = AltiumApi.GlobalVars.Client.OpenDocument("SCH", targetPath);
                if (target != null) ActivateDocument(target, targetPath, "original schematic", requestId);
            }
            catch (Exception ex) { AdapterLog.Error("restore-target", ex, requestId); }
        }

        internal static void ActivateDocument(IServerDocument document, string expectedPath, string description, string requestId)
        {
            AltiumApi.GlobalVars.Client.ShowDocument(document);
            for (var attempt = 1; attempt <= 20; attempt++)
            {
                Forms.Application.DoEvents();
                var currentPath = AltiumApi.GlobalVars.Client.GetCurrentView()?.GetOwnerDocument()?.GetFileName();
                if (!string.IsNullOrWhiteSpace(currentPath) &&
                    string.Equals(Path.GetFullPath(currentPath), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
                    return;
                Thread.Sleep(25);
            }
            throw new InvalidOperationException("Altium did not activate the expected " + description + ": " + expectedPath);
        }

        private static string Value(Dictionary<string, string> values, string key) => values.TryGetValue(key, out var value) ? value : "";
        private static void DeleteIfExists(string path) { if (File.Exists(path)) File.Delete(path); }
        private static void TryDelete(string path, string requestId)
        {
            try { DeleteIfExists(path); }
            catch (Exception ex) { AdapterLog.Error("cleanup-file", ex, requestId); }
        }
    }
}
