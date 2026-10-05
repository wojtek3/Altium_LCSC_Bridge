using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LcscBridge.Core;

public sealed class LibraryStore : ILibraryStore
{
    private const int ManifestVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string RootPath { get; }
    public string IndexPath => Path.Combine(RootPath, "catalog.sqlite3");

    public LibraryStore(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) throw new ArgumentException("Library root is required.", nameof(rootPath));
        RootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(RootPath);
        EnsureSchema();
    }

    public async Task<LibraryManifest> ImportAsync(ImportSource source, ComponentMetadata metadata, CancellationToken cancellationToken = default)
    {
        source.Validate();
        ValidateMetadata(metadata);

        var inputs = new List<(string Role, string Path)>();
        if (source.SchLibPath is not null) inputs.Add(("schlib", Path.GetFullPath(source.SchLibPath)));
        if (source.PcbLibPath is not null) inputs.Add(("pcblib", Path.GetFullPath(source.PcbLibPath)));
        if (source.IntLibPath is not null) inputs.Add(("intlib", Path.GetFullPath(source.IntLibPath)));
        if (source.OriginalPayloadPath is not null) inputs.Add(("source", Path.GetFullPath(source.OriginalPayloadPath)));

        var hashes = new List<(string Role, string Path, string Hash, long Length)>();
        foreach (var input in inputs)
        {
            await using var stream = File.OpenRead(input.Path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            hashes.Add((input.Role, input.Path, hash, stream.Length));
        }

        var partId = SafeSegment(FirstNonEmpty(metadata.SupplierPartNumber, metadata.ManufacturerPartNumber));
        var revisionMaterial = string.Join("|", hashes.OrderBy(x => x.Role).Select(x => $"{x.Role}:{x.Hash}")) + "|" +
            JsonSerializer.Serialize(metadata);
        var revisionId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revisionMaterial))).ToLowerInvariant()[..16];
        var finalDirectory = Path.Combine(RootPath, "parts", partId, revisionId);
        var manifestPath = Path.Combine(finalDirectory, "manifest.json");
        if (File.Exists(manifestPath))
        {
            var existing = await ReadManifestAsync(manifestPath, cancellationToken);
            Upsert(existing);
            return existing;
        }

        var stagingDirectory = Path.Combine(RootPath, ".staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            var storedFiles = new List<StoredFile>();
            foreach (var item in hashes)
            {
                var fileName = item.Role + Path.GetExtension(item.Path).ToLowerInvariant();
                File.Copy(item.Path, Path.Combine(stagingDirectory, fileName), false);
                storedFiles.Add(new StoredFile(item.Role, fileName, item.Hash, item.Length));
            }

            var manifest = new LibraryManifest(ManifestVersion, partId, revisionId, DateTimeOffset.UtcNow,
                metadata, storedFiles, finalDirectory);
            await AtomicFile.WriteAllTextAsync(Path.Combine(stagingDirectory, "manifest.json"),
                JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken);

            Directory.CreateDirectory(Path.GetDirectoryName(finalDirectory)!);
            try { Directory.Move(stagingDirectory, finalDirectory); }
            catch (IOException) when (Directory.Exists(finalDirectory))
            {
                Directory.Delete(stagingDirectory, true);
            }
            Upsert(manifest);
            return manifest;
        }
        catch
        {
            if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, true);
            throw;
        }
    }

    public async Task<IReadOnlyList<LibraryManifest>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var results = new List<LibraryManifest>();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT manifest_path FROM components
            WHERE $query = '' OR search_text LIKE '%' || $query || '%'
            ORDER BY imported_at DESC LIMIT 250;
            """;
        command.Parameters.AddWithValue("$query", query.Trim().ToLowerInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var path = reader.GetString(0);
            if (File.Exists(path)) results.Add(await ReadManifestAsync(path, cancellationToken));
        }
        return results;
    }

    public async Task RebuildIndexAsync(CancellationToken cancellationToken = default)
    {
        await using (var connection = OpenConnection())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM components;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        var parts = Path.Combine(RootPath, "parts");
        if (!Directory.Exists(parts)) return;
        foreach (var manifestPath in Directory.EnumerateFiles(parts, "manifest.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { Upsert(await ReadManifestAsync(manifestPath, cancellationToken)); }
            catch (JsonException) { /* Leave malformed revisions out of the rebuild. */ }
        }
    }

    public async Task<LibraryManifest> ReconcileAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        var fullManifestPath = Path.GetFullPath(manifestPath);
        var relative = Path.GetRelativePath(RootPath, fullManifestPath);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidOperationException("Manifest is outside the library root.");
        var manifest = await ReadManifestAsync(fullManifestPath, cancellationToken);
        var files = new List<StoredFile>();
        foreach (var item in manifest.Files)
        {
            var path = Path.Combine(manifest.LibraryDirectory, item.FileName);
            await using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            files.Add(item with { Sha256 = hash, Length = stream.Length });
        }
        var reconciled = manifest with { Files = files };
        await AtomicFile.WriteAllTextAsync(fullManifestPath, JsonSerializer.Serialize(reconciled, JsonOptions), cancellationToken);
        Upsert(reconciled);
        return reconciled;
    }

    private void EnsureSchema()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS components (
              part_id TEXT NOT NULL,
              revision_id TEXT NOT NULL,
              manufacturer TEXT NOT NULL,
              mpn TEXT NOT NULL,
              supplier_part TEXT NOT NULL,
              description TEXT NOT NULL,
              package TEXT NOT NULL,
              imported_at TEXT NOT NULL,
              manifest_path TEXT NOT NULL UNIQUE,
              search_text TEXT NOT NULL,
              PRIMARY KEY (part_id, revision_id)
            );
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = IndexPath }.ToString());
        connection.Open();
        return connection;
    }

    private void Upsert(LibraryManifest manifest)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO components(part_id,revision_id,manufacturer,mpn,supplier_part,description,package,imported_at,manifest_path,search_text)
            VALUES($part,$revision,$manufacturer,$mpn,$supplier,$description,$package,$imported,$manifest,$search)
            ON CONFLICT(part_id,revision_id) DO UPDATE SET manufacturer=$manufacturer,mpn=$mpn,supplier_part=$supplier,
              description=$description,package=$package,imported_at=$imported,manifest_path=$manifest,search_text=$search;
            """;
        var m = manifest.Metadata;
        command.Parameters.AddWithValue("$part", manifest.PartId);
        command.Parameters.AddWithValue("$revision", manifest.RevisionId);
        command.Parameters.AddWithValue("$manufacturer", m.Manufacturer);
        command.Parameters.AddWithValue("$mpn", m.ManufacturerPartNumber);
        command.Parameters.AddWithValue("$supplier", m.SupplierPartNumber);
        command.Parameters.AddWithValue("$description", m.Description);
        command.Parameters.AddWithValue("$package", m.Package);
        command.Parameters.AddWithValue("$imported", manifest.ImportedAt.ToString("O"));
        command.Parameters.AddWithValue("$manifest", Path.Combine(manifest.LibraryDirectory, "manifest.json"));
        command.Parameters.AddWithValue("$search", string.Join(' ', m.Manufacturer, m.ManufacturerPartNumber, m.SupplierPartNumber,
            m.Description, m.Package, m.SymbolReference, m.FootprintName).ToLowerInvariant());
        command.ExecuteNonQuery();
    }

    private static async Task<LibraryManifest> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<LibraryManifest>(stream, JsonOptions, cancellationToken)
            ?? throw new JsonException($"Empty manifest: {path}");
        return manifest with { LibraryDirectory = Path.GetDirectoryName(Path.GetFullPath(path))! };
    }

    private static void ValidateMetadata(ComponentMetadata value)
    {
        if (string.IsNullOrWhiteSpace(value.ManufacturerPartNumber) && string.IsNullOrWhiteSpace(value.SupplierPartNumber))
            throw new ArgumentException("An MPN or supplier part number is required.");
        foreach (var text in new[] { value.Manufacturer, value.ManufacturerPartNumber, value.SupplierPartNumber, value.SymbolReference, value.FootprintName })
            if (text.Contains('\n') || text.Contains('\r')) throw new ArgumentException("Metadata cannot contain newlines.");
        if (!string.IsNullOrWhiteSpace(value.DatasheetUrl) &&
            (!Uri.TryCreate(value.DatasheetUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")))
            throw new ArgumentException("Datasheet URL must be HTTP or HTTPS.");
    }

    private static string FirstNonEmpty(string first, string second) => !string.IsNullOrWhiteSpace(first) ? first : second;

    private static string SafeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new string(value.Trim().Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim('.', ' ');
        return string.IsNullOrWhiteSpace(result) ? "unknown-part" : result;
    }
}
