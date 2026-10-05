namespace LcscBridge.Core;

public static class OnlineCatalogNavigation
{
    public static readonly Uri HomeUri = new("https://www.lcsc.com/", UriKind.Absolute);

    public static Uri CreateSearchUri(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return HomeUri;

        var encoded = Uri.EscapeDataString(query.Trim());
        return new Uri($"https://www.lcsc.com/search?q={encoded}", UriKind.Absolute);
    }

    public static bool IsAllowedWebUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
               uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
    }
}
