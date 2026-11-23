using System.IO;
using System.Net.Http;
using System.Text.Json;
using IPTV101.Data;

namespace IPTV101.Channels;

public sealed record UpdateResult(string Source, bool Ok, int Count, string Message);

/// <summary>
/// Gestiona las listas del usuario: IPTV 101 se instala sin ningún canal; todo lo que aparece aquí
/// lo agregó la propia persona desde el importador, sea un archivo .m3u/.m3u8 local o una URL remota.
/// </summary>
public static class ChannelStore
{
    public static List<PlaylistSource> LoadSources()
    {
        try
        {
            if (File.Exists(AppPaths.Sources))
                return JsonSerializer.Deserialize(File.ReadAllText(AppPaths.Sources), SettingsJson.Default.ListPlaylistSource) ?? new();
        }
        catch (Exception) { }
        return new();
    }

    private static void SaveSources(List<PlaylistSource> sources)
    {
        AppPaths.Ensure();
        AppPaths.WriteAtomic(AppPaths.Sources, JsonSerializer.Serialize(sources, SettingsJson.Default.ListPlaylistSource));
    }

    /// <summary>Lee todas las listas habilitadas y numera los canales en el orden de fuentes.json.</summary>
    public static List<Channel> LoadAll()
    {
        var sources = LoadSources();
        var all = new List<Channel>(2048);
        foreach (var s in sources.Where(s => s.Enabled != 0))
        {
            var p = Path.Combine(AppPaths.Lists, s.FileName);
            if (!File.Exists(p)) continue;
            try { all.AddRange(M3uParser.Parse(File.ReadAllText(p), s.Name)); }
            catch (IOException) { }
        }
        for (int i = 0; i < all.Count; i++) all[i].Number = i + 1;
        return all;
    }

    private static int NextId(List<PlaylistSource> sources) => sources.Count == 0 ? 1 : sources.Max(s => s.Id) + 1;

    /// <summary>Nombre visible único: si ya existe, agrega " (2)", " (3)"...</summary>
    private static string UniqueName(List<PlaylistSource> sources, string baseName)
    {
        baseName = string.IsNullOrWhiteSpace(baseName) ? "Mi lista" : baseName.Trim();
        if (!sources.Any(s => s.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase))) return baseName;
        for (int i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!sources.Any(s => s.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
        }
        return baseName + " (" + Guid.NewGuid().ToString("N")[..4] + ")";
    }

    /// <summary>Importa una lista desde un archivo .m3u/.m3u8 local. Lanza <see cref="InvalidDataException"/> si no trae canales.</summary>
    public static async Task<PlaylistSource> AddFromFileAsync(string path, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var name = Path.GetFileNameWithoutExtension(path);
        var parsed = M3uParser.Parse(text, name);
        if (parsed.Count == 0) throw new InvalidDataException("El archivo no trae canales reconocibles (se esperaba formato M3U/M3U8 con #EXTINF).");

        AppPaths.Ensure();
        var sources = LoadSources();
        var source = new PlaylistSource
        {
            Id = NextId(sources),
            Name = UniqueName(sources, name),
            Url = "",
            SourcePath = path,
            Enabled = 1,
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ChannelCount = parsed.Count,
        };
        AppPaths.WriteAtomic(Path.Combine(AppPaths.Lists, source.FileName), text);
        sources.Add(source);
        SaveSources(sources);
        return source;
    }

    /// <summary>Importa una lista desde una URL http/https remota. Lanza excepción con un mensaje claro si falla.</summary>
    public static async Task<PlaylistSource> AddFromUrlAsync(string url, string? name, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new InvalidDataException("La URL no es válida: debe empezar con http:// o https://");

        using var http = NewHttpClient();
        string text;
        try
        {
            using var resp = await http.GetAsync(uri, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidDataException($"El servidor respondió {(int)resp.StatusCode} ({resp.ReasonPhrase}).");
            text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException) { throw new InvalidDataException("Se agotó el tiempo de espera al descargar la lista."); }
        catch (HttpRequestException ex) { throw new InvalidDataException("No se pudo conectar con la fuente: " + ex.Message); }

        var label = string.IsNullOrWhiteSpace(name) ? (uri.Host.Length > 0 ? uri.Host : "Mi lista") : name!;
        var parsed = M3uParser.Parse(text, label);
        if (parsed.Count == 0) throw new InvalidDataException("La lista descargada no trae canales reconocibles.");

        AppPaths.Ensure();
        var sources = LoadSources();
        var source = new PlaylistSource
        {
            Id = NextId(sources),
            Name = UniqueName(sources, label),
            Url = url,
            SourcePath = null,
            Enabled = 1,
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ChannelCount = parsed.Count,
        };
        AppPaths.WriteAtomic(Path.Combine(AppPaths.Lists, source.FileName), text);
        sources.Add(source);
        SaveSources(sources);
        return source;
    }

    /// <summary>Vuelve a descargar (URL) o releer (archivo local) una lista ya importada.</summary>
    public static async Task<UpdateResult> RefreshAsync(int id, CancellationToken ct)
    {
        var sources = LoadSources();
        var s = sources.FirstOrDefault(x => x.Id == id);
        if (s == null) return new UpdateResult("", false, 0, "La lista ya no existe.");

        string text;
        if (s.IsRemote)
        {
            using var http = NewHttpClient();
            try
            {
                using var resp = await http.GetAsync(s.Url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return new UpdateResult(s.Name, false, 0, $"el servidor respondió {(int)resp.StatusCode}");
                text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (TaskCanceledException) { return new UpdateResult(s.Name, false, 0, "tiempo de espera agotado"); }
            catch (Exception ex) { return new UpdateResult(s.Name, false, 0, "sin conexión con la fuente (" + ex.Message + ")"); }
        }
        else if (!string.IsNullOrWhiteSpace(s.SourcePath))
        {
            if (!File.Exists(s.SourcePath)) return new UpdateResult(s.Name, false, 0, "ya no se encuentra el archivo original: " + s.SourcePath);
            try { text = await File.ReadAllTextAsync(s.SourcePath, ct).ConfigureAwait(false); }
            catch (IOException ex) { return new UpdateResult(s.Name, false, 0, "no se pudo leer el archivo: " + ex.Message); }
        }
        else return new UpdateResult(s.Name, false, 0, "esta lista no tiene una fuente para actualizar");

        var parsed = M3uParser.Parse(text, s.Name);
        if (parsed.Count == 0) return new UpdateResult(s.Name, false, 0, "la nueva versión no trae canales; se mantuvo la copia anterior");

        var dest = Path.Combine(AppPaths.Lists, s.FileName);
        AppPaths.Ensure();
        AppPaths.WriteAtomic(dest, text);
        s.LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        s.ChannelCount = parsed.Count;
        SaveSources(sources);
        return new UpdateResult(s.Name, true, parsed.Count, $"{parsed.Count} canales");
    }

    /// <summary>Actualiza todas las listas remotas (botón "Actualizar listas desde sus fuentes").</summary>
    public static async Task<List<UpdateResult>> UpdateAllAsync(CancellationToken ct)
    {
        var results = new List<UpdateResult>();
        foreach (var s in LoadSources().Where(s => s.Enabled != 0 && s.CanUpdate))
            results.Add(await RefreshAsync(s.Id, ct).ConfigureAwait(false));
        return results;
    }

    /// <summary>Quita una lista importada: borra el archivo local y la entrada de fuentes.json.</summary>
    public static void RemoveSource(int id)
    {
        var sources = LoadSources();
        var s = sources.FirstOrDefault(x => x.Id == id);
        if (s == null) return;
        sources.Remove(s);
        SaveSources(sources);
        try
        {
            var p = Path.Combine(AppPaths.Lists, s.FileName);
            if (File.Exists(p)) File.Delete(p);
        }
        catch (IOException) { }
    }

    public static void RenameSource(int id, string newName)
    {
        var sources = LoadSources();
        var s = sources.FirstOrDefault(x => x.Id == id);
        if (s == null) return;
        s.Name = UniqueName(sources.Where(x => x.Id != id).ToList(), newName);
        SaveSources(sources);
    }

    private static HttpClient NewHttpClient()
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        })
        { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(Channel.DefaultUserAgent);
        return http;
    }
}
