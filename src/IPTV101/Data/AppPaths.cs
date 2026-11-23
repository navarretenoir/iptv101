using System.IO;

namespace IPTV101.Data;

/// <summary>Rutas de datos del usuario (%LOCALAPPDATA%\IPTV101) y del paquete instalado.</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IPTV101");
    public static string Lists => Path.Combine(Root, "listas");
    public static string Logos => Path.Combine(Root, "logos");
    public static string Settings => Path.Combine(Root, "ajustes.json");
    public static string Sources => Path.Combine(Lists, "fuentes.json");
    public static string BundledLists => Path.Combine(AppContext.BaseDirectory, "listas");

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Lists);
        Directory.CreateDirectory(Logos);
    }

    /// <summary>Escritura atómica: archivo temporal y reemplazo.</summary>
    public static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new System.Text.UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
