using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IPTV101.Channels;

/// <summary>Lector de listas M3U/M3U8 extendidas (EXTINF, EXTVLCOPT, EXTHTTP).</summary>
public static partial class M3uParser
{
    [GeneratedRegex("([A-Za-z0-9_-]+)=\"([^\"]*)\"")]
    private static partial Regex AttrRegex();

    public static List<Channel> Parse(string text, string source)
    {
        var result = new List<Channel>();
        string? name = null, group = null, logo = null, ua = null, referrer = null;
        List<string>? headers = null;
        bool pending = false;

        void Reset()
        {
            name = group = logo = ua = referrer = null;
            headers = null;
            pending = false;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                Reset();
                pending = true;
                int comma = FindTitleComma(line);
                name = comma >= 0 ? line[(comma + 1)..].Trim() : "";
                var header = comma >= 0 ? line[..comma] : line;
                foreach (Match m in AttrRegex().Matches(header))
                {
                    var key = m.Groups[1].Value.ToLowerInvariant();
                    var val = m.Groups[2].Value.Trim();
                    switch (key)
                    {
                        case "group-title": group = val; break;
                        case "tvg-logo": logo = val; break;
                        case "tvg-name": if (string.IsNullOrEmpty(name)) name = val; break;
                        case "http-user-agent": case "user-agent": ua = val; break;
                        case "http-referrer": case "http-referer": case "referer": case "referrer": referrer = val; break;
                    }
                }
                continue;
            }

            if (line.StartsWith("#EXTVLCOPT:", StringComparison.OrdinalIgnoreCase))
            {
                var opt = line["#EXTVLCOPT:".Length..];
                int eq = opt.IndexOf('=');
                if (eq > 0)
                {
                    var k = opt[..eq].Trim().ToLowerInvariant();
                    var v = opt[(eq + 1)..].Trim();
                    if (k == "http-user-agent") ua = v;
                    else if (k is "http-referrer" or "http-referer") referrer = v;
                    else if (k == "http-origin") (headers ??= new()).Add("Origin: " + v);
                }
                continue;
            }

            if (line.StartsWith("#EXTHTTP:", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line["#EXTHTTP:".Length..]);
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        var v = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString();
                        if (p.Name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) ua = v;
                        else if (p.Name.Equals("Referer", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("Referrer", StringComparison.OrdinalIgnoreCase)) referrer = v;
                        else (headers ??= new()).Add(p.Name + ": " + v);
                    }
                }
                catch (JsonException) { }
                continue;
            }

            if (line[0] == '#') continue;

            // Línea de URL.
            var url = line;
            if (!url.Contains("://", StringComparison.Ordinal)) { Reset(); continue; }

            // Algunas listas agregan opciones al estilo "url|User-Agent=...&Referer=...".
            int pipe = url.IndexOf('|');
            if (pipe > 0)
            {
                foreach (var part in url[(pipe + 1)..].Split('&'))
                {
                    int eq = part.IndexOf('=');
                    if (eq <= 0) continue;
                    var k = part[..eq]; var v = Uri.UnescapeDataString(part[(eq + 1)..]);
                    if (k.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) ua = v;
                    else if (k.Equals("Referer", StringComparison.OrdinalIgnoreCase) || k.Equals("Referrer", StringComparison.OrdinalIgnoreCase)) referrer = v;
                    else (headers ??= new()).Add(k + ": " + v);
                }
                url = url[..pipe];
            }

            var finalName = string.IsNullOrWhiteSpace(name) ? NameFromUrl(url) : name!;
            var finalGroup = string.IsNullOrWhiteSpace(group) || group.Equals("live", StringComparison.OrdinalIgnoreCase)
                || group.Equals("undefined", StringComparison.OrdinalIgnoreCase)
                ? source : group!;

            result.Add(new Channel
            {
                Name = finalName,
                Url = url,
                Source = source,
                Group = finalGroup,
                LogoUrl = string.IsNullOrWhiteSpace(logo) ? null : logo,
                UserAgent = ua,
                Referrer = referrer,
                Headers = headers?.ToArray() ?? Array.Empty<string>(),
                SearchKey = Normalize(finalName),
            });
            _ = pending;
            Reset();
        }
        return result;
    }

    /// <summary>La coma que separa atributos del título: la primera coma fuera de comillas.</summary>
    private static int FindTitleComma(string line)
    {
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') quoted = !quoted;
            else if (c == ',' && !quoted) return i;
        }
        return -1;
    }

    private static string NameFromUrl(string url)
    {
        try { return new Uri(url).Host; } catch (UriFormatException) { return url; }
    }

    public static string Normalize(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>Solo las URL de una lista (para precargar favoritos).</summary>
    public static IEnumerable<string> Urls(string text) => Parse(text, "").Select(c => c.Url);
}
