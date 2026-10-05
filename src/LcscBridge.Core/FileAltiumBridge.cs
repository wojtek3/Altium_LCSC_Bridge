using System.Diagnostics;
using System.Text;

namespace LcscBridge.Core;

public sealed record OnlineOperationStatus(string RequestId, string State, string Stage, DateTimeOffset UpdatedAt, string Detail)
{
    public bool IsTerminal => State is "succeeded" or "failed" or "interrupted";
}

public sealed class FileAltiumBridge : IAltiumBridge
{
    public const int ProtocolVersion = 1;
    public const int OnlineProtocolVersion = 3;
    public string BridgeRoot { get; }
    public string LibraryRoot { get; }

    public FileAltiumBridge(string bridgeRoot, string libraryRoot)
    {
        BridgeRoot = Path.GetFullPath(bridgeRoot);
        LibraryRoot = Path.GetFullPath(libraryRoot);
        foreach (var name in new[] { "requests", "responses", "pending", "online-requests", "status" })
            Directory.CreateDirectory(Path.Combine(BridgeRoot, name));
        QuarantineStalePlacementRequests();
        RemoveExpiredFailedOnlineImports();
    }

    public bool IsOnlineAdapterAvailable(out string diagnostic)
    {
        var path = Path.Combine(BridgeRoot, "adapter.capabilities");
        if (!File.Exists(path))
        {
            diagnostic = "The Altium online-import adapter is not loaded. Install version 0.2.4 and restart Altium.";
            return false;
        }
        try
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromSeconds(5))
                throw new InvalidDataException("The Altium online-import adapter stopped reporting status. Restart Altium after installing version 0.2.4.");
            var values = ReadValues(path, Encoding.Unicode);
            if (values.GetValueOrDefault("protocol") != OnlineProtocolVersion.ToString() ||
                values.GetValueOrDefault("createOnlineLibraries") != "true" ||
                values.GetValueOrDefault("onlineOperationStatus") != "true")
                throw new InvalidDataException("The loaded Altium adapter is incompatible. Install the 0.2.4 protocol-3 adapter and restart Altium.");
            if (!int.TryParse(values.GetValueOrDefault("processId"), out var processId))
                throw new InvalidDataException("The adapter capability file has no process identifier.");
            using var process = Process.GetProcessById(processId);
            if (process.HasExited) throw new InvalidDataException("The Altium adapter is no longer running.");
            diagnostic = $"Altium adapter {values.GetValueOrDefault("adapterVersion", "unknown")} is available.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or InvalidDataException)
        {
            diagnostic = ex.Message;
            return false;
        }
    }

    public bool IsPlacementAdapterAvailable(out string diagnostic)
    {
        var path = Path.Combine(BridgeRoot, "script.capabilities");
        if (!File.Exists(path))
        {
            diagnostic = "The Altium placement listener is not active. In Altium, run the LCSC Browser toolbar command and keep its listener window open.";
            return false;
        }
        try
        {
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            if (age > TimeSpan.FromSeconds(4))
                throw new InvalidDataException("The Altium placement listener stopped responding. Run the LCSC Browser toolbar command again and keep its listener window open.");
            var values = ReadValues(path, Encoding.UTF8);
            if (values.GetValueOrDefault("protocol") != ProtocolVersion.ToString() || values.GetValueOrDefault("placement") != "true")
                throw new InvalidDataException("The active Altium placement listener is incompatible with this bridge version.");
            diagnostic = "Altium placement listener is active.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            diagnostic = ex.Message;
            return false;
        }
    }

    public OnlineOperationStatus? ReadOnlineOperationStatus(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Any(x => !char.IsAsciiLetterOrDigit(x))) return null;
        var path = Path.Combine(BridgeRoot, "status", requestId + ".status");
        if (!File.Exists(path)) return null;
        try
        {
            var values = ReadValues(path, Encoding.Unicode);
            if (!DateTimeOffset.TryParse(values.GetValueOrDefault("updatedUtc"), out var updated)) updated = File.GetLastWriteTimeUtc(path);
            return new OnlineOperationStatus(requestId, values.GetValueOrDefault("state", "unknown"),
                values.GetValueOrDefault("stage", "unknown"), updated, values.GetValueOrDefault("detail", string.Empty));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public OnlineOperationStatus? FindActiveOnlineOperation() => Directory.EnumerateFiles(Path.Combine(BridgeRoot, "status"), "*.status")
        .Select(x => ReadOnlineOperationStatus(Path.GetFileNameWithoutExtension(x)))
        .Where(x => x is not null && !x.IsTerminal)
        .OrderByDescending(x => x!.UpdatedAt).FirstOrDefault();

    public bool HasOnlineOperationArtifacts(string requestId) =>
        File.Exists(Path.Combine(BridgeRoot, "online-requests", requestId + ".request")) ||
        File.Exists(Path.Combine(BridgeRoot, "online-requests", requestId + ".processing")) ||
        File.Exists(Path.Combine(BridgeRoot, "responses", requestId + ".response")) ||
        File.Exists(Path.Combine(BridgeRoot, "responses", requestId + ".response.processing"));

    public Task WriteOnlineOperationStatusAsync(string requestId, string state, string stage, string detail, CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["protocol"] = OnlineProtocolVersion.ToString(), ["requestId"] = requestId, ["state"] = state,
            ["stage"] = stage, ["updatedUtc"] = DateTimeOffset.UtcNow.ToString("O"), ["detail"] = Sanitize(detail)
        };
        return AtomicFile.WriteAllTextAsync(Path.Combine(BridgeRoot, "status", requestId + ".status"),
            Serialize(values), cancellationToken, Encoding.Unicode);
    }

    public async Task<string> QueueOnlineImportAsync(SourceModelBundle bundle, bool placeAfterImport, CancellationToken cancellationToken = default)
    {
        if (!bundle.IsImportable) throw new InvalidOperationException("The source model did not pass conversion validation.");
        if (FindActiveOnlineOperation() is { } active)
            throw new InvalidOperationException($"Online import {active.RequestId} is still {active.Stage}; wait for it to finish before starting another.");
        var requestId = Guid.NewGuid().ToString("N");
        var stagingDirectory = Path.Combine(LibraryRoot, ".staging", "online-" + requestId);
        Directory.CreateDirectory(stagingDirectory);
        var payloadPath = Path.Combine(stagingDirectory, "easyeda-source.json");
        await AtomicFile.WriteAllBytesAsync(payloadPath, bundle.RawPayload, cancellationToken);

        var metadata = bundle.Component.Metadata;
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["protocol"] = OnlineProtocolVersion.ToString(), ["requestId"] = requestId, ["operation"] = "createOnlineLibraries",
            ["payloadPath"] = payloadPath, ["stagingDirectory"] = stagingDirectory, ["libraryRoot"] = LibraryRoot,
            ["placeAfterImport"] = placeAfterImport ? "true" : "false", ["providerModelId"] = bundle.ProviderModelId,
            ["providerRevision"] = bundle.ProviderRevision, ["manufacturer"] = metadata.Manufacturer,
            ["manufacturerPartNumber"] = metadata.ManufacturerPartNumber, ["supplierPartNumber"] = metadata.SupplierPartNumber,
            ["description"] = metadata.Description, ["package"] = metadata.Package, ["datasheetUrl"] = metadata.DatasheetUrl
        };
        ValidateValues(values);
        await WriteOnlineOperationStatusAsync(requestId, "queued", "queued", "Waiting for Altium.", cancellationToken);
        try
        {
            await AtomicFile.WriteAllTextAsync(Path.Combine(BridgeRoot, "online-requests", requestId + ".request"),
                Serialize(values), cancellationToken, Encoding.Unicode);
        }
        catch (Exception ex)
        {
            try { await WriteOnlineOperationStatusAsync(requestId, "failed", "queueing", ex.Message, CancellationToken.None); } catch { }
            try { Directory.Delete(stagingDirectory, true); } catch { }
            throw;
        }
        BridgeLog.Info("bridge", $"Queued online native-library creation (placeAfterImport={placeAfterImport}).", requestId);
        return requestId;
    }

    public async Task<string> QueueImportAsync(LibraryManifest manifest, bool placeAfterImport, string? targetDocument = null, CancellationToken cancellationToken = default)
    {
        var libraryDirectory = Path.GetFullPath(manifest.LibraryDirectory);
        if (!IsWithin(libraryDirectory, LibraryRoot)) throw new InvalidOperationException("Manifest is outside the configured library root.");
        if (!manifest.IsPlaceable) throw new InvalidOperationException("The cached component does not contain enough information for placement.");
        string FileFor(string role) => manifest.Files.Where(x => x.Role == role).Select(x => Path.Combine(libraryDirectory, x.FileName)).FirstOrDefault() ?? string.Empty;
        var requestId = Guid.NewGuid().ToString("N");
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["protocol"] = ProtocolVersion.ToString(), ["requestId"] = requestId,
            ["operation"] = placeAfterImport ? "installAndPlace" : "install", ["libraryRoot"] = LibraryRoot,
            ["manifestPath"] = Path.Combine(libraryDirectory, "manifest.json"), ["schLibPath"] = FileFor("schlib"),
            ["pcbLibPath"] = FileFor("pcblib"), ["intLibPath"] = FileFor("intlib"),
            ["symbolReference"] = manifest.Metadata.SymbolReference, ["footprintName"] = manifest.Metadata.FootprintName,
            ["targetDocument"] = targetDocument ?? string.Empty, ["manufacturer"] = manifest.Metadata.Manufacturer,
            ["manufacturerPartNumber"] = manifest.Metadata.ManufacturerPartNumber,
            ["supplierPartNumber"] = manifest.Metadata.SupplierPartNumber, ["description"] = manifest.Metadata.Description,
            ["package"] = manifest.Metadata.Package, ["datasheetUrl"] = manifest.Metadata.DatasheetUrl,
            ["source"] = manifest.Metadata.Source, ["sourceRevision"] = manifest.Metadata.SourceRevision
        };
        ValidateValues(values);
        await AtomicFile.WriteAllTextAsync(Path.Combine(BridgeRoot, "pending", requestId + ".pending"),
            Path.Combine(libraryDirectory, "manifest.json"), cancellationToken);
        await AtomicFile.WriteAllTextAsync(Path.Combine(BridgeRoot, "requests", requestId + ".request"), Serialize(values), cancellationToken, Encoding.Unicode);
        BridgeLog.Info("bridge", $"Queued Altium operation '{values["operation"]}' for {manifest.PartId}/{manifest.RevisionId}.", requestId);
        return requestId;
    }

    private static Dictionary<string, string> ReadValues(string path, Encoding encoding) => File.ReadAllLines(path, encoding)
        .Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1], StringComparer.OrdinalIgnoreCase);
    private static string Serialize(Dictionary<string, string> values) => string.Join(Environment.NewLine, values.Select(x => $"{x.Key}={x.Value}")) + Environment.NewLine;
    private static string Sanitize(string value) => (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
    private static void ValidateValues(Dictionary<string, string> values)
    {
        foreach (var value in values.Values) if (value.Contains('\r') || value.Contains('\n')) throw new InvalidOperationException("Bridge values cannot contain newlines.");
    }
    private static bool IsWithin(string child, string parent)
    {
        var relative = Path.GetRelativePath(parent, child);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private void QuarantineStalePlacementRequests()
    {
        var requests = Path.Combine(BridgeRoot, "requests");
        var recovery = Path.Combine(BridgeRoot, "recovery");
        foreach (var path in Directory.EnumerateFiles(requests, "*.request"))
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromMinutes(10)) continue;
            Directory.CreateDirectory(recovery);
            var destination = Path.Combine(recovery, Path.GetFileNameWithoutExtension(path) + ".stale-placement");
            File.Move(path, destination, true);
            BridgeLog.Info("recovery", $"Quarantined stale placement request at {destination}.");
        }
    }

    private void RemoveExpiredFailedOnlineImports()
    {
        foreach (var failed in Directory.EnumerateFiles(Path.Combine(BridgeRoot, "online-requests"), "*.processing.failed"))
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(failed) < TimeSpan.FromDays(14)) continue;
            try
            {
                var values = ReadValues(failed, Encoding.Unicode);
                var staging = values.GetValueOrDefault("stagingDirectory");
                var stagingRoot = Path.Combine(LibraryRoot, ".staging");
                if (!string.IsNullOrWhiteSpace(staging) && IsWithin(Path.GetFullPath(staging), stagingRoot) && Directory.Exists(staging)) Directory.Delete(staging, true);
                File.Delete(failed);
                BridgeLog.Info("maintenance", "Removed an online-import failure retained for more than 14 days.", values.GetValueOrDefault("requestId"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { BridgeLog.Error("maintenance", ex); }
        }
    }
}
