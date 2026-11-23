using System.Windows;

namespace IPTV101.Skins;

/// <summary>
/// Cambia el "skin" visual de IPTV 101 en caliente: inserta el ResourceDictionary de la época elegida
/// como el primer diccionario fusionado de la aplicación (máxima prioridad de búsqueda de recursos),
/// reemplazando al anterior. Todo lo que en la interfaz usa DynamicResource se redibuja solo.
/// </summary>
public static class SkinManager
{
    public static readonly string[] Ids = { "Win95", "Win98", "WinMe", "Win2000", "WinXP", "Win7", "Win8", "Win1011" };

    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        ["Win95"] = "Windows 95",
        ["Win98"] = "Windows 98",
        ["WinMe"] = "Windows Me",
        ["Win2000"] = "Windows 2000",
        ["WinXP"] = "Windows XP",
        ["Win7"] = "Windows 7",
        ["Win8"] = "Windows 8",
        ["Win1011"] = "Windows 10 / 11",
    };

    public static string Current { get; private set; } = "Win1011";

    public static void Apply(string id)
    {
        if (!Ids.Contains(id)) id = "Win1011";
        Current = id;
        var dict = new ResourceDictionary { Source = new Uri($"Skins/{id}.xaml", UriKind.Relative) };

        var merged = Application.Current.Resources.MergedDictionaries;
        // El skin activo vive siempre en la posición 0 (la de mayor prioridad de búsqueda); si ya había
        // uno puesto por un cambio de skin anterior, se reemplaza.
        if (merged.Count > 0 && merged[0] is ResourceDictionary rd && rd.Contains("__SkinMarker"))
            merged.RemoveAt(0);
        merged.Insert(0, dict);
    }
}
