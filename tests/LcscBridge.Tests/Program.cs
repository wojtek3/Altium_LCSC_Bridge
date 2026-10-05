using LcscBridge.Core;
using Microsoft.Data.Sqlite;
using System.Net;
using System.Net.Http;
using System.Text;

Environment.SetEnvironmentVariable("LCSC_BRIDGE_LOG_DIRECTORY", Path.Combine(Path.GetTempPath(), "LcscBridgeTests", "logs-" + Guid.NewGuid().ToString("N")));

var failures = new List<string>();
await Run("online search URLs are encoded", () =>
{
    Equal("https://www.lcsc.com/", OnlineCatalogNavigation.CreateSearchUri("  ").AbsoluteUri);
    Equal("https://www.lcsc.com/search?q=C681024", OnlineCatalogNavigation.CreateSearchUri(" C681024 ").AbsoluteUri);
    Equal("https://www.lcsc.com/search?q=STM32%20F4", OnlineCatalogNavigation.CreateSearchUri("STM32 F4").AbsoluteUri);
    Equal("https://www.lcsc.com/search?q=TPS7A49%2F1", OnlineCatalogNavigation.CreateSearchUri("TPS7A49/1").AbsoluteUri);
    Equal("https://www.lcsc.com/search?q=10%C2%B5F", OnlineCatalogNavigation.CreateSearchUri("10µF").AbsoluteUri);
    return Task.CompletedTask;
});

await Run("online navigation permits only HTTP and HTTPS", () =>
{
    True(OnlineCatalogNavigation.IsAllowedWebUri("https://www.lcsc.com/"));
    True(OnlineCatalogNavigation.IsAllowedWebUri("http://example.test/path"));
    True(!OnlineCatalogNavigation.IsAllowedWebUri("file:///C:/Windows/win.ini"));
    True(!OnlineCatalogNavigation.IsAllowedWebUri("javascript:alert(1)"));
    True(!OnlineCatalogNavigation.IsAllowedWebUri("mailto:test@example.test"));
    True(!OnlineCatalogNavigation.IsAllowedWebUri("not a URI"));
    return Task.CompletedTask;
});

await Run("paired import, duplicate reuse, search and bridge", async () =>
{
    using var fixture = new Fixture();
    var sch = fixture.File("źródło/Test.SchLib", "schematic-v1");
    var pcb = fixture.File("źródło/Test.PcbLib", "footprint-v1");
    var metadata = fixture.Metadata("C12345");
    var first = await fixture.Store.ImportAsync(new ImportSource(sch, pcb, null), metadata);
    var second = await fixture.Store.ImportAsync(new ImportSource(sch, pcb, null), metadata);
    Equal(first.RevisionId, second.RevisionId);
    True(File.Exists(Path.Combine(first.LibraryDirectory, "manifest.json")));
    Equal(1, (await fixture.Store.SearchAsync("c12345")).Count);
    var request = await fixture.Bridge.QueueImportAsync(first, true);
    var requestPath = Path.Combine(fixture.Bridge.BridgeRoot, "requests", request + ".request");
    True(File.Exists(requestPath));
    True((await File.ReadAllTextAsync(requestPath)).Contains("operation=installAndPlace"));
    True(File.Exists(Path.Combine(fixture.Bridge.BridgeRoot, "pending", request + ".pending")));
});

await Run("changed input creates a new revision", async () =>
{
    using var fixture = new Fixture();
    var sch = fixture.File("Test.SchLib", "v1");
    var pcb = fixture.File("Test.PcbLib", "pcb");
    var one = await fixture.Store.ImportAsync(new ImportSource(sch, pcb, null), fixture.Metadata("C9"));
    await File.WriteAllTextAsync(sch, "v2");
    var two = await fixture.Store.ImportAsync(new ImportSource(sch, pcb, null), fixture.Metadata("C9"));
    True(one.RevisionId != two.RevisionId);
});

await Run("metadata change creates a new revision", async () =>
{
    using var fixture = new Fixture();
    var intLib = fixture.File("Part.IntLib", "same-library");
    var one = await fixture.Store.ImportAsync(new ImportSource(null, null, intLib), fixture.Metadata("C10"));
    var two = await fixture.Store.ImportAsync(new ImportSource(null, null, intLib), fixture.Metadata("C11"));
    True(one.RevisionId != two.RevisionId);
});

await Run("invalid extension is rejected", async () =>
{
    using var fixture = new Fixture();
    var wrong = fixture.File("Part.bin", "not-a-library");
    await ThrowsAsync<ArgumentException>(() => fixture.Store.ImportAsync(new ImportSource(null, null, wrong), fixture.Metadata("C12")));
});

await Run("index rebuild uses manifests", async () =>
{
    using var fixture = new Fixture();
    var intLib = fixture.File("Part.IntLib", "library");
    await fixture.Store.ImportAsync(new ImportSource(null, null, intLib), fixture.Metadata("C77"));
    SqliteConnection.ClearAllPools();
    File.Delete(fixture.Store.IndexPath);
    var rebuilt = new LibraryStore(fixture.LibraryRoot);
    await rebuilt.RebuildIndexAsync();
    Equal(1, (await rebuilt.SearchAsync("maker")).Count);
});

await Run("bridge rejects directories outside library root", async () =>
{
    using var fixture = new Fixture();
    var fake = new LibraryManifest(1, "x", "y", DateTimeOffset.UtcNow, fixture.Metadata("C1"),
        [new StoredFile("intlib", "a.IntLib", "00", 1)], fixture.Root);
    await ThrowsAsync<InvalidOperationException>(() => fixture.Bridge.QueueImportAsync(fake, false));
});

await Run("placement adapter heartbeat is required and expires", () =>
{
    using var fixture = new Fixture();
    True(!fixture.Bridge.IsPlacementAdapterAvailable(out _));
    var capability = Path.Combine(fixture.Bridge.BridgeRoot, "script.capabilities");
    File.WriteAllText(capability, "protocol=1\nplacement=true\nadapterVersion=test\n");
    True(fixture.Bridge.IsPlacementAdapterAvailable(out _));
    File.SetLastWriteTimeUtc(capability, DateTime.UtcNow - TimeSpan.FromSeconds(10));
    True(!fixture.Bridge.IsPlacementAdapterAvailable(out _));
    return Task.CompletedTask;
});

await Run("online adapter requires a live protocol 3 capability handshake", () =>
{
    using var fixture = new Fixture();
    var capability = Path.Combine(fixture.Bridge.BridgeRoot, "adapter.capabilities");
    File.WriteAllText(capability, $"protocol=2\r\ncreateOnlineLibraries=true\r\nadapterVersion=0.2.3\r\nprocessId={Environment.ProcessId}\r\n", Encoding.Unicode);
    True(!fixture.Bridge.IsOnlineAdapterAvailable(out var oldDiagnostic));
    True(oldDiagnostic.Contains("0.2.4") || oldDiagnostic.Contains("protocol-3"));
    File.WriteAllText(capability, $"protocol=3\r\ncreateOnlineLibraries=true\r\nonlineOperationStatus=true\r\nadapterVersion=0.2.4\r\nprocessId={Environment.ProcessId}\r\n", Encoding.Unicode);
    True(fixture.Bridge.IsOnlineAdapterAvailable(out _));
    File.SetLastWriteTimeUtc(capability, DateTime.UtcNow - TimeSpan.FromSeconds(10));
    True(!fixture.Bridge.IsOnlineAdapterAvailable(out _));
    return Task.CompletedTask;
});

await Run("stale placement requests are quarantined", () =>
{
    using var fixture = new Fixture();
    var request = Path.Combine(fixture.Bridge.BridgeRoot, "requests", "old.request");
    File.WriteAllText(request, "operation=installAndPlace");
    File.SetLastWriteTimeUtc(request, DateTime.UtcNow - TimeSpan.FromMinutes(11));
    _ = new FileAltiumBridge(fixture.Bridge.BridgeRoot, fixture.LibraryRoot);
    True(!File.Exists(request));
    True(File.Exists(Path.Combine(fixture.Bridge.BridgeRoot, "recovery", "old.stale-placement")));
    return Task.CompletedTask;
});

await Run("EasyEDA exact search validates the returned LCSC number", async () =>
{
    using var client = new HttpClient(new FixtureHttpHandler(request => Json("""
        {"success":true,"result":{"lcsc":{"number":"C2040"},"uuid":"dev-2040","title":"RC0603","description":"10k resistor","updated_at":"r1",
          "dataStr":{"head":{"c_para":{"name":"R","package":"0603","Manufacturer":"Maker","Manufacturer Part":"RC0603"}},"shape":["P~pin"]},
          "packageDetail":{"title":"0603","dataStr":{"shape":["PAD~pad"]}}}}
        """)));
    using var provider = new EasyEdaProvider(client);
    var result = await provider.SearchAsync("C2040", 1, CancellationToken.None);
    Equal(1, result.Count);
    Equal("C2040", result[0].Metadata.SupplierPartNumber);
});

await Run("EasyEDA wrong-part search response is rejected", async () =>
{
    using var client = new HttpClient(new FixtureHttpHandler(_ => Json("""
        {"success":true,"result":{"lcsc":{"number":"C999"},"uuid":"wrong","dataStr":{"head":{"c_para":{}},"shape":[]},"packageDetail":{"dataStr":{"shape":[]}}}}
        """)));
    using var provider = new EasyEdaProvider(client);
    await ThrowsAsync<InvalidDataException>(() => provider.SearchAsync("C2040", 1, CancellationToken.None));
});

await Run("EasyEDA unsupported footprint geometry blocks import", async () =>
{
    using var client = new HttpClient(new FixtureHttpHandler(request =>
        request.RequestUri!.AbsolutePath.EndsWith("/svgs", StringComparison.Ordinal)
            ? Json("{\"success\":true,\"result\":[{\"svg\":\"<svg></svg>\"},{\"svg\":\"<svg></svg>\"}]}")
            : Json("""
                {"success":true,"result":{"lcsc":{"number":"C2040"},"uuid":"dev","updated_at":"r2",
                 "dataStr":{"shape":["P~pin"]},"packageDetail":{"dataStr":{"layers":["1~TopLayer~#ff0000"],"shape":["SOLIDREGION~1~~M 0 0 C 1 1 2 2 3 3 Z~solid~id"]}}}}
                """)));
    using var provider = new EasyEdaProvider(client);
    var component = new CatalogComponent("dev", new ComponentMetadata("Maker","RC","C2040","R","0603","","EasyEDA/LCSC","r1","R","0603"),
        ModelAvailability.Available, ModelAvailability.Available, DateTimeOffset.UtcNow);
    var bundle = await provider.DownloadAsync(component, CancellationToken.None);
    True(!bundle.IsImportable);
    True(bundle.Diagnostics.Any(x => x.Code == "unsupported-solidregion-path"));
});

await Run("ComponentPolarityLayer is accepted for referenced footprint geometry", async () =>
{
    using var client = new HttpClient(new FixtureHttpHandler(request =>
        request.RequestUri!.AbsolutePath.EndsWith("/svgs", StringComparison.Ordinal)
            ? Json("{\"success\":true,\"result\":[{\"svg\":\"<svg></svg>\"},{\"svg\":\"<svg></svg>\"}]}")
            : Json("""
                {"success":true,"result":{"lcsc":{"number":"C2040"},"uuid":"dev","updated_at":"r2",
                 "dataStr":{"shape":["P~pin"]},"packageDetail":{"dataStr":{"layers":["101~ComponentPolarityLayer~#ffffff"],"shape":["CIRCLE~0~0~1~0.2~101~circle-id"]}}}}
                """)));
    using var provider = new EasyEdaProvider(client);
    var component = new CatalogComponent("dev", new ComponentMetadata("Maker","RC","C2040","R","0603","","EasyEDA/LCSC","r1","R","0603"),
        ModelAvailability.Available, ModelAvailability.Available, DateTimeOffset.UtcNow);
    var bundle = await provider.DownloadAsync(component, CancellationToken.None);
    True(bundle.IsImportable);
    True(!bundle.Diagnostics.Any(x => x.Code == "unsupported-footprint-layer"));
});

await Run("unknown referenced footprint layer blocks import with identity", async () =>
{
    using var client = new HttpClient(new FixtureHttpHandler(request =>
        request.RequestUri!.AbsolutePath.EndsWith("/svgs", StringComparison.Ordinal)
            ? Json("{\"success\":true,\"result\":[{\"svg\":\"<svg></svg>\"},{\"svg\":\"<svg></svg>\"}]}")
            : Json("""
                {"success":true,"result":{"lcsc":{"number":"C2040"},"uuid":"dev","updated_at":"r2",
                 "dataStr":{"shape":["P~pin"]},"packageDetail":{"dataStr":{"layers":["777~MysteryCopper~#ffffff","778~UnusedMystery~#ffffff"],"shape":["TRACK~0.2~777~0 0 1 1~track-id"]}}}}
                """)));
    using var provider = new EasyEdaProvider(client);
    var component = new CatalogComponent("dev", new ComponentMetadata("Maker","RC","C2040","R","0603","","EasyEDA/LCSC","r1","R","0603"),
        ModelAvailability.Available, ModelAvailability.Available, DateTimeOffset.UtcNow);
    var bundle = await provider.DownloadAsync(component, CancellationToken.None);
    True(!bundle.IsImportable);
    True(bundle.Diagnostics.Any(x => x.Code == "unsupported-footprint-layer" && x.Message.Contains("TRACK") && x.Message.Contains("777") && x.Message.Contains("MysteryCopper")));
    True(!bundle.Diagnostics.Any(x => x.Message.Contains("UnusedMystery")));
});

await Run("online bridge stages protocol 3 source payload and status", async () =>
{
    using var fixture = new Fixture();
    var component = new CatalogComponent("dev", fixture.Metadata("C2040"), ModelAvailability.Available, ModelAvailability.Available, DateTimeOffset.UtcNow);
    var bundle = new SourceModelBundle(component, "dev", "r1", Encoding.UTF8.GetBytes("{\"source\":true}"), ["<svg></svg>", "<svg></svg>"], 1, 1, []);
    var id = await fixture.Bridge.QueueOnlineImportAsync(bundle, true);
    var request = Path.Combine(fixture.Bridge.BridgeRoot, "online-requests", id + ".request");
    True(File.Exists(request));
    var content = await File.ReadAllTextAsync(request);
    True(content.Contains("protocol=3"));
    True(content.Contains("operation=createOnlineLibraries"));
    var status = fixture.Bridge.ReadOnlineOperationStatus(id);
    Equal("queued", status!.State);
    Equal("queued", status.Stage);
    var payload = Directory.EnumerateFiles(Path.Combine(fixture.LibraryRoot, ".staging"), "easyeda-source.json", SearchOption.AllDirectories).Single();
    Equal("{\"source\":true}", await File.ReadAllTextAsync(payload));
});

await Run("online bridge suppresses duplicate active requests", async () =>
{
    using var fixture = new Fixture();
    var component = new CatalogComponent("dev", fixture.Metadata("C2040"), ModelAvailability.Available, ModelAvailability.Available, DateTimeOffset.UtcNow);
    var bundle = new SourceModelBundle(component, "dev", "r1", Encoding.UTF8.GetBytes("{\"source\":true}"), ["<svg></svg>", "<svg></svg>"], 1, 1, []);
    _ = await fixture.Bridge.QueueOnlineImportAsync(bundle, true);
    await ThrowsAsync<InvalidOperationException>(() => fixture.Bridge.QueueOnlineImportAsync(bundle, true));
});

await Run("failed online staging is retained for 14 days then removed", () =>
{
    using var fixture = new Fixture();
    var id = Guid.NewGuid().ToString("N");
    var staging = Path.Combine(fixture.LibraryRoot, ".staging", "online-" + id);
    Directory.CreateDirectory(staging);
    File.WriteAllText(Path.Combine(staging, "easyeda-source.json"), "diagnostic");
    var failed = Path.Combine(fixture.Bridge.BridgeRoot, "online-requests", id + ".processing.failed");
    File.WriteAllText(failed, $"protocol=3\r\nrequestId={id}\r\nstagingDirectory={staging}\r\n", Encoding.Unicode);
    File.SetLastWriteTimeUtc(failed, DateTime.UtcNow - TimeSpan.FromDays(15));
    _ = new FileAltiumBridge(fixture.Bridge.BridgeRoot, fixture.LibraryRoot);
    True(!Directory.Exists(staging));
    True(!File.Exists(failed));
    return Task.CompletedTask;
});

if (args.Contains("--live", StringComparer.OrdinalIgnoreCase))
{
    await Run("live EasyEDA exact component and model contract", async () =>
    {
        using var provider = new EasyEdaProvider();
        var result = await provider.SearchAsync("C25804", 1, CancellationToken.None);
        Equal("C25804", result.Single().Metadata.SupplierPartNumber);
        var bundle = await provider.DownloadAsync(result.Single(), CancellationToken.None);
        True(bundle.SymbolPrimitiveCount > 0);
        True(bundle.FootprintPrimitiveCount > 0);
        True(bundle.IsImportable);
        var keywordResults = await provider.SearchAsync("RC0603", 1, CancellationToken.None);
        True(keywordResults.Count > 0);
        True(keywordResults.Any(x => x.Metadata.SupplierPartNumber.StartsWith("C", StringComparison.OrdinalIgnoreCase)));
        foreach (var number in new[] { "C7434411", "C7373267", "C6240910", "C6829929", "C26082150", "C22808547", "C1526233" })
        {
            var retainedPart = await provider.SearchAsync(number, 1, CancellationToken.None);
            var retainedBundle = await provider.DownloadAsync(retainedPart.Single(), CancellationToken.None);
            True(retainedBundle.IsImportable);
            True(!retainedBundle.Diagnostics.Any(x => x.Code == "unsupported-footprint-layer"));
        }
    });
}

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}
Console.WriteLine("All integration tests passed.");
return 0;

async Task Run(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add("FAIL " + name + ": " + ex.Message); }
}
void True(bool condition) { if (!condition) throw new Exception("Expected true."); }
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
{
    Content = new StringContent(body, Encoding.UTF8, "application/json")
};

sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "LcscBridgeTests", Guid.NewGuid().ToString("N"));
    public string LibraryRoot => Path.Combine(Root, "library");
    public LibraryStore Store { get; }
    public FileAltiumBridge Bridge { get; }
    public Fixture()
    {
        Store = new LibraryStore(LibraryRoot);
        Bridge = new FileAltiumBridge(Path.Combine(Root, "bridge"), LibraryRoot);
    }
    public string File(string relative, string content)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }
    public ComponentMetadata Metadata(string supplier) => new("Maker", "MPN-1", supplier, "Test component", "SOT-23", "https://example.test/data.pdf", "test", "1", "SYM", "FP");
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}

sealed class FixtureHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(response(request));
}
