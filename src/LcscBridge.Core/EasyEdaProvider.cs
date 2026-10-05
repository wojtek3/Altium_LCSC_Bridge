using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using LcscBridge.Shared;

namespace LcscBridge.Core;

public sealed partial class EasyEdaProvider : ICatalogProvider, IModelProvider, IDisposable
{
    private const int MaximumResponseBytes = 20 * 1024 * 1024;
    private const string ModelApiVersion = "6.4.19.5";
    private static readonly HashSet<string> SupportedSymbolPrimitives = new(StringComparer.Ordinal)
        { "P", "R", "E", "C", "A", "PL", "PG", "PT" };
    private static readonly HashSet<string> SupportedFootprintPrimitives = new(StringComparer.Ordinal)
        { "PAD", "TRACK", "HOLE", "VIA", "CIRCLE", "ARC", "RECT", "TEXT", "SVGNODE", "SOLIDREGION" };
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public string Name => "EasyEDA/LCSC";

    public EasyEdaProvider(HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        });
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/142.0.0.0 Safari/537.36 AltiumLcscBridge/0.2");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<IReadOnlyList<CatalogComponent>> SearchAsync(string query, int page, CancellationToken cancellationToken)
    {
        query = query.Trim();
        if (query.Length < 2) return [];
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));
        if (LcscNumber().IsMatch(query)) return [await LookupExactAsync(query, cancellationToken)];

        var requestUri = $"https://pro.easyeda.com/api/v2/eda/product/search?keyword={Uri.EscapeDataString(query)}&type=3&page={page}&pageSize=30";
        using var request = CreateProviderRequest(HttpMethod.Get, requestUri);
        var payload = await SendBoundedAsync(request, cancellationToken);
        using var document = ParseProviderResponse(payload, "search");
        var result = RequiredObject(document.RootElement, "result", "search");
        var products = RequiredArray(result, "productList", "search");

        var components = new List<CatalogComponent>();
        foreach (var product in products.EnumerateArray())
        {
            var number = String(product, "number");
            if (!LcscNumber().IsMatch(number)) continue;
            var deviceId = String(product, "hasDevice");
            var device = ObjectOrDefault(product, "device_info");
            var hasSymbol = device.ValueKind == JsonValueKind.Object && device.TryGetProperty("symbol_info", out var symbol) && symbol.ValueKind == JsonValueKind.Object;
            var hasFootprint = device.ValueKind == JsonValueKind.Object && device.TryGetProperty("footprint_info", out var footprint) && footprint.ValueKind == JsonValueKind.Object;
            var description = String(device, "Description");
            if (string.IsNullOrWhiteSpace(description)) description = String(product, "description");
            var revision = device.ValueKind == JsonValueKind.Object ? String(device, "updated_at") : string.Empty;
            var attributes = ObjectOrDefault(device, "attributes");
            var metadata = new ComponentMetadata(
                String(product, "manufacturer"), String(product, "mpn"), number, description,
                String(product, "package"), String(attributes, "Datasheet"), Name, revision,
                String(ObjectOrDefault(device, "symbol_info"), "title"),
                String(ObjectOrDefault(device, "footprint_info"), "title"));
            components.Add(new CatalogComponent(
                string.IsNullOrWhiteSpace(deviceId) ? number : deviceId, metadata,
                hasSymbol ? ModelAvailability.Available : ModelAvailability.Missing,
                hasFootprint ? ModelAvailability.Available : ModelAvailability.Missing,
                DateTimeOffset.UtcNow));
        }

        return components;
    }

    private async Task<CatalogComponent> LookupExactAsync(string requestedNumber, CancellationToken cancellationToken)
    {
        using var request = CreateProviderRequest(HttpMethod.Get,
            $"https://easyeda.com/api/products/{Uri.EscapeDataString(requestedNumber)}/components?version={ModelApiVersion}");
        var payload = await SendBoundedAsync(request, cancellationToken);
        using var document = ParseProviderResponse(payload, "exact component lookup");
        var result = RequiredObject(document.RootElement, "result", "exact component lookup");
        var returnedNumber = String(RequiredObject(result, "lcsc", "exact component lookup"), "number");
        if (!returnedNumber.Equals(requestedNumber, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Provider returned {returnedNumber} when {requestedNumber} was requested.");

        var symbolData = RequiredObject(result, "dataStr", "exact component lookup");
        var package = RequiredObject(result, "packageDetail", "exact component lookup");
        var footprintData = RequiredObject(package, "dataStr", "exact component lookup");
        var parameters = RequiredObject(RequiredObject(symbolData, "head", "exact component lookup"), "c_para", "exact component lookup");
        var metadata = new ComponentMetadata(
            String(parameters, "Manufacturer"),
            FirstNonEmpty(String(parameters, "Manufacturer Part"), String(result, "title")),
            returnedNumber,
            FirstNonEmpty(String(result, "description"), String(result, "title")),
            FirstNonEmpty(String(parameters, "package"), String(package, "title")),
            String(parameters, "link"),
            Name,
            FirstNonEmpty(String(result, "updated_at"), String(result, "updateTime")),
            FirstNonEmpty(String(parameters, "name"), String(result, "title")),
            FirstNonEmpty(String(parameters, "package"), String(package, "title")));
        var hasSymbol = symbolData.TryGetProperty("shape", out var symbolShapes) && symbolShapes.ValueKind == JsonValueKind.Array && symbolShapes.GetArrayLength() > 0;
        var hasFootprint = footprintData.TryGetProperty("shape", out var footprintShapes) && footprintShapes.ValueKind == JsonValueKind.Array && footprintShapes.GetArrayLength() > 0;
        return new CatalogComponent(FirstNonEmpty(String(result, "uuid"), returnedNumber), metadata,
            hasSymbol ? ModelAvailability.Available : ModelAvailability.Missing,
            hasFootprint ? ModelAvailability.Available : ModelAvailability.Missing,
            DateTimeOffset.UtcNow);
    }

    public async Task<SourceModelBundle> DownloadAsync(CatalogComponent component, CancellationToken cancellationToken)
    {
        var requestedNumber = component.Metadata.SupplierPartNumber.Trim();
        if (!LcscNumber().IsMatch(requestedNumber)) throw new ArgumentException("A valid LCSC part number is required.", nameof(component));

        using var modelRequest = CreateProviderRequest(HttpMethod.Get,
            $"https://easyeda.com/api/products/{Uri.EscapeDataString(requestedNumber)}/components?version={ModelApiVersion}");
        var payload = await SendBoundedAsync(modelRequest, cancellationToken);
        using var document = ParseProviderResponse(payload, "component model");
        var result = RequiredObject(document.RootElement, "result", "component model");
        var returnedNumber = String(RequiredObject(result, "lcsc", "component model"), "number");
        if (!returnedNumber.Equals(requestedNumber, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Provider returned {returnedNumber} when {requestedNumber} was requested.");

        var symbolData = RequiredObject(result, "dataStr", "component model");
        var package = RequiredObject(result, "packageDetail", "component model");
        var footprintData = RequiredObject(package, "dataStr", "component model");
        var symbolShapes = RequiredArray(symbolData, "shape", "symbol model");
        var footprintShapes = RequiredArray(footprintData, "shape", "footprint model");
        var diagnostics = ValidatePrimitives(symbolShapes, footprintShapes, footprintData);
        var previews = await DownloadPreviewsAsync(requestedNumber, cancellationToken);

        return new SourceModelBundle(component, String(result, "uuid"),
            FirstNonEmpty(String(result, "updated_at"), String(result, "updateTime")), payload, previews,
            symbolShapes.GetArrayLength(), footprintShapes.GetArrayLength(), diagnostics);
    }

    private async Task<IReadOnlyList<string>> DownloadPreviewsAsync(string number, CancellationToken cancellationToken)
    {
        using var request = CreateProviderRequest(HttpMethod.Get,
            $"https://easyeda.com/api/products/{Uri.EscapeDataString(number)}/svgs");
        var payload = await SendBoundedAsync(request, cancellationToken);
        using var document = ParseProviderResponse(payload, "preview");
        var result = RequiredArray(document.RootElement, "result", "preview");
        return result.EnumerateArray().Select(x => String(x, "svg"))
            .Where(x => x.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
    }

    private static IReadOnlyList<ConversionDiagnostic> ValidatePrimitives(JsonElement symbols, JsonElement footprints, JsonElement footprintData)
    {
        var diagnostics = new List<ConversionDiagnostic>();
        Validate(symbols, SupportedSymbolPrimitives, "symbol", diagnostics);
        Validate(footprints, SupportedFootprintPrimitives, "footprint", diagnostics);
        ValidateFootprintLayers(footprints, footprintData, diagnostics);
        foreach (var shape in footprints.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                     .Select(x => x.GetString() ?? string.Empty).Where(x => x.StartsWith("SOLIDREGION~", StringComparison.Ordinal)))
        {
            var parts = shape.Split('~');
            var path = parts.Length > 3 ? parts[3] : string.Empty;
            if (Regex.Matches(path, "[A-Za-z]").Select(x => x.Value).Any(x => x is not ("M" or "L" or "Z")))
                diagnostics.Add(new ConversionDiagnostic("unsupported-solidregion-path", "A solid region contains curved or relative path commands that cannot be represented safely.", true));
        }
        return diagnostics;

        static void Validate(JsonElement shapes, HashSet<string> supported, string kind, List<ConversionDiagnostic> target)
        {
            var unknown = shapes.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "")
                .Select(x => x.Split('~', 2)[0]).Where(x => !supported.Contains(x)).Distinct(StringComparer.Ordinal).ToList();
            foreach (var primitive in unknown)
                target.Add(new ConversionDiagnostic("unsupported-" + kind + "-primitive",
                    $"Unsupported {kind} primitive: {(string.IsNullOrWhiteSpace(primitive) ? "<non-string>" : primitive)}", true));
        }

        static void ValidateFootprintLayers(JsonElement shapes, JsonElement data, List<ConversionDiagnostic> target)
        {
            var layers = new Dictionary<string, string>(StringComparer.Ordinal);
            if (data.TryGetProperty("layers", out var layerTable) && layerTable.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in layerTable.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.String) continue;
                    var fields = (entry.GetString() ?? string.Empty).Split('~');
                    if (fields.Length >= 2 && !string.IsNullOrWhiteSpace(fields[0])) layers[fields[0]] = fields[1];
                }
            }

            var emitted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in shapes.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? string.Empty))
            {
                var fields = raw.Split('~');
                var primitive = fields.Length == 0 ? string.Empty : fields[0];
                if (!EasyEdaLayerPolicy.TryGetLayerFieldIndex(primitive, out var layerIndex)) continue;
                if (fields.Length <= layerIndex || string.IsNullOrWhiteSpace(fields[layerIndex]))
                {
                    Add("missing-footprint-layer-reference", $"Footprint primitive {primitive} has no layer ID.");
                    continue;
                }
                var layerId = fields[layerIndex];
                if (!layers.TryGetValue(layerId, out var layerName))
                {
                    Add("missing-footprint-layer", $"Footprint primitive {primitive} references missing layer ID {layerId}.");
                    continue;
                }
                if (!EasyEdaLayerPolicy.IsSupportedLayer(layerName))
                    Add("unsupported-footprint-layer", $"Footprint primitive {primitive} references unsupported layer ID {layerId} ({layerName}).");
            }

            void Add(string code, string message)
            {
                if (emitted.Add(code + "\n" + message)) target.Add(new ConversionDiagnostic(code, message, true));
            }
        }
    }

    private HttpRequestMessage CreateProviderRequest(HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Referrer = new Uri("https://pro.easyeda.com/editor");
        request.Headers.TryAddWithoutValidation("Origin", "https://pro.easyeda.com");
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        return request;
    }

    private async Task<byte[]> SendBoundedAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            throw new InvalidDataException("Provider response exceeded the 20 MB safety limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (memory.Length + read > MaximumResponseBytes) throw new InvalidDataException("Provider response exceeded the 20 MB safety limit.");
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return memory.ToArray();
    }

    private static JsonDocument ParseProviderResponse(byte[] payload, string operation)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(payload); }
        catch (JsonException ex) { throw new InvalidDataException($"Provider returned malformed JSON for {operation}.", ex); }
        var succeeded = document.RootElement.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;
        if (!succeeded && document.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number)
            succeeded = code.TryGetInt32(out var codeValue) && codeValue == 0;
        if (!succeeded)
        {
            document.Dispose();
            throw new InvalidDataException($"Provider reported an unsuccessful {operation} response.");
        }
        return document;
    }

    private static JsonElement RequiredObject(JsonElement parent, string name, string operation)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Provider {operation} response is missing object '{name}'.");
        return value;
    }

    private static JsonElement RequiredArray(JsonElement parent, string name, string operation)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Provider {operation} response is missing array '{name}'.");
        return value;
    }

    private static JsonElement ObjectOrDefault(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : default;

    private static string String(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value)) return string.Empty;
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
    }

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
    public void Dispose() { if (_ownsClient) _httpClient.Dispose(); }

    [GeneratedRegex("^C[0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LcscNumber();
}
