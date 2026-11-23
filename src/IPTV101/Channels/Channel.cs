namespace IPTV101.Channels;

/// <summary>Un canal leído desde una lista M3U.</summary>
public sealed class Channel
{
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    public required string Name { get; init; }
    public required string Url { get; init; }
    public required string Source { get; init; }
    public string Group { get; init; } = "";
    public string? LogoUrl { get; init; }
    public string? UserAgent { get; init; }
    public string? Referrer { get; init; }
    public IReadOnlyList<string> Headers { get; init; } = Array.Empty<string>();
    public int Number { get; set; }

    /// <summary>Texto normalizado (sin tildes, minúsculas) para la búsqueda.</summary>
    public string SearchKey { get; set; } = "";
}
