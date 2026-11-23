using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using IPTV101.Data;
using IPTV101.Skins;

namespace IPTV101;

public partial class App : Application
{
    /// <summary>Registro de diagnóstico opcional (--diag &lt;archivo&gt;): estadísticas locales, nunca se envían.</summary>
    public static string? DiagFile { get; private set; }
    public static string? EngineLogFile { get; private set; }
    public static int? StartChannel { get; private set; }
    public static uint ActivateMessage { get; private set; }

    private Mutex? _single;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ActivateMessage = RegisterWindowMessage("IPTV101.Activar.9c2f");

        _single = new Mutex(true, @"Local\IPTV101.Instancia.9c2f", out bool created);
        if (!created)
        {
            PostMessage((IntPtr)0xFFFF, ActivateMessage, IntPtr.Zero, IntPtr.Zero);
            Shutdown();
            return;
        }

        string? theme = null;
        string? skin = null;
        for (int i = 0; i < e.Args.Length; i++)
        {
            var a = e.Args[i];
            string? Next() => i + 1 < e.Args.Length ? e.Args[++i] : null;
            switch (a.ToLowerInvariant())
            {
                case "--diag": DiagFile = Next(); break;
                case "--registro-motor": EngineLogFile = Next(); break;
                case "--canal": if (int.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out var n)) StartChannel = n; break;
                case "--tema": theme = Next(); break;
                case "--skin": skin = Next(); break;
            }
        }

        AppPaths.Ensure();
        var settings = Settings.Load();
        if (theme != null) settings.Theme = theme.ToLowerInvariant() switch { "claro" => "Claro", "oscuro" => "Oscuro", _ => "Sistema" };
        if (skin != null && SkinManager.Ids.Contains(skin)) settings.Skin = skin;
        ApplyTheme(settings.Theme);
        SkinManager.Apply(settings.Skin);

        var w = new MainWindow(settings);
        MainWindow = w;
        w.Show();
    }

    public static void ApplyTheme(string theme)
    {
#pragma warning disable WPF0001
        Current.ThemeMode = theme switch
        {
            "Claro" => ThemeMode.Light,
            "Oscuro" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
#pragma warning restore WPF0001
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _single?.ReleaseMutex(); } catch (ApplicationException) { }
        _single?.Dispose();
        base.OnExit(e);
    }
}
