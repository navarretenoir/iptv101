using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IPTV101.Data;

public sealed class Settings
{
    public double Volume { get; set; } = 80;
    public bool Muted { get; set; }
    public string? LastUrl { get; set; }
    public string? PreviousUrl { get; set; }
    public List<string> Favorites { get; set; } = new();
    public List<string> Recents { get; set; } = new();
    public bool FavoritesSeeded { get; set; }
    public string ListFilter { get; set; } = "Todos";
    public string? GroupFilter { get; set; }
    public string Aspect { get; set; } = "Auto";
    public string Theme { get; set; } = "Sistema";
    public string Skin { get; set; } = "Win1011";
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1120;
    public double WindowHeight { get; set; } = 680;
    public bool WindowMaximized { get; set; }
    public bool AutoPlayLast { get; set; } = true;

    public const int MaxRecents = 40;

    public static Settings Load()
    {
        try
        {
            if (File.Exists(AppPaths.Settings))
                return JsonSerializer.Deserialize(File.ReadAllText(AppPaths.Settings), SettingsJson.Default.Settings) ?? new Settings();
        }
        catch (Exception) { }
        return new Settings();
    }

    public void Save()
    {
        try { AppPaths.WriteAtomic(AppPaths.Settings, JsonSerializer.Serialize(this, SettingsJson.Default.Settings)); }
        catch (Exception) { }
    }

    public void PushRecent(string url)
    {
        Recents.Remove(url);
        Recents.Insert(0, url);
        if (Recents.Count > MaxRecents) Recents.RemoveRange(MaxRecents, Recents.Count - MaxRecents);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(List<PlaylistSource>))]
internal partial class SettingsJson : JsonSerializerContext { }

/// <summary>Una lista importada por el usuario: desde un archivo local (<see cref="SourcePath"/>) o desde una URL remota (<see cref="Url"/>).</summary>
public sealed class PlaylistSource
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("source_path")] public string? SourcePath { get; set; }
    [JsonPropertyName("enabled")] public int Enabled { get; set; } = 1;
    [JsonPropertyName("user_agent")] public string? UserAgent { get; set; }
    [JsonPropertyName("last_updated")] public long LastUpdated { get; set; }
    [JsonPropertyName("channel_count")] public int ChannelCount { get; set; }

    /// <summary>Nombre de archivo estable en el disco del usuario: no depende del nombre visible (que el usuario puede cambiar o repetir).</summary>
    [JsonIgnore] public string FileName => $"lista_{Id}.m3u";

    [JsonIgnore] public bool IsRemote => !string.IsNullOrWhiteSpace(Url);
    [JsonIgnore] public bool CanUpdate => IsRemote || !string.IsNullOrWhiteSpace(SourcePath);
}
