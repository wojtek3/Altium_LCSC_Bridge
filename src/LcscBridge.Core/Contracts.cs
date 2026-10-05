using System.Text.Json.Serialization;

namespace LcscBridge.Core;

public enum ModelAvailability { Unknown, Missing, Available }

public sealed record ComponentMetadata(
    string Manufacturer,
    string ManufacturerPartNumber,
    string SupplierPartNumber,
    string Description,
    string Package,
    string DatasheetUrl,
    string Source,
    string SourceRevision,
    string SymbolReference,
    string FootprintName);

public sealed record CatalogComponent(
    string Id,
    ComponentMetadata Metadata,
    ModelAvailability SymbolAvailability,
    ModelAvailability FootprintAvailability,
    DateTimeOffset RetrievedAt);

public sealed record ConversionDiagnostic(string Code, string Message, bool IsError);

public sealed record SourceModelBundle(
    CatalogComponent Component,
    string ProviderModelId,
    string ProviderRevision,
    byte[] RawPayload,
    IReadOnlyList<string> PreviewSvgs,
    int SymbolPrimitiveCount,
    int FootprintPrimitiveCount,
    IReadOnlyList<ConversionDiagnostic> Diagnostics)
{
    public bool IsImportable => SymbolPrimitiveCount > 0 && FootprintPrimitiveCount > 0 && Diagnostics.All(x => !x.IsError);
}

public sealed record ImportSource(string? SchLibPath, string? PcbLibPath, string? IntLibPath, string? OriginalPayloadPath = null)
{
    public void Validate()
    {
        var integrated = !string.IsNullOrWhiteSpace(IntLibPath);
        var pair = !string.IsNullOrWhiteSpace(SchLibPath) && !string.IsNullOrWhiteSpace(PcbLibPath);
        if (integrated == pair)
            throw new ArgumentException("Select either one IntLib or one SchLib/PcbLib pair.");

        foreach (var path in new[] { SchLibPath, PcbLibPath, IntLibPath, OriginalPayloadPath }.Where(p => p is not null))
            if (!File.Exists(path)) throw new FileNotFoundException("Import source does not exist.", path);
        if (SchLibPath is not null && !Path.GetExtension(SchLibPath).Equals(".SchLib", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Schematic source must use the .SchLib extension.");
        if (PcbLibPath is not null && !Path.GetExtension(PcbLibPath).Equals(".PcbLib", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("PCB source must use the .PcbLib extension.");
        if (IntLibPath is not null && !Path.GetExtension(IntLibPath).Equals(".IntLib", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Integrated source must use the .IntLib extension.");
    }
}

public sealed record StoredFile(string Role, string FileName, string Sha256, long Length);

public sealed record LibraryManifest(
    int SchemaVersion,
    string PartId,
    string RevisionId,
    DateTimeOffset ImportedAt,
    ComponentMetadata Metadata,
    IReadOnlyList<StoredFile> Files,
    string LibraryDirectory)
{
    [JsonIgnore]
    public bool IsIntegrated => Files.Any(x => x.Role == "intlib");

    [JsonIgnore]
    public bool IsPlaceable => !string.IsNullOrWhiteSpace(Metadata.SymbolReference) &&
        (IsIntegrated || (Files.Any(x => x.Role == "schlib") && Files.Any(x => x.Role == "pcblib") &&
                          !string.IsNullOrWhiteSpace(Metadata.FootprintName)));
}

public interface ICatalogProvider
{
    string Name { get; }
    Task<IReadOnlyList<CatalogComponent>> SearchAsync(string query, int page, CancellationToken cancellationToken);
}

public interface IModelProvider
{
    string Name { get; }
    Task<SourceModelBundle> DownloadAsync(CatalogComponent component, CancellationToken cancellationToken);
}

public interface ILibraryStore
{
    string RootPath { get; }
    Task<LibraryManifest> ImportAsync(ImportSource source, ComponentMetadata metadata, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LibraryManifest>> SearchAsync(string query, CancellationToken cancellationToken = default);
    Task RebuildIndexAsync(CancellationToken cancellationToken = default);
    Task<LibraryManifest> ReconcileAsync(string manifestPath, CancellationToken cancellationToken = default);
}

public interface IAltiumBridge
{
    Task<string> QueueImportAsync(LibraryManifest manifest, bool placeAfterImport, string? targetDocument = null, CancellationToken cancellationToken = default);
}
