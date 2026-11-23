using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace IPTV101;

public partial class AboutWindow : Window
{
    private static readonly (string keys, string action)[] Shortcuts =
    {
        ("▲ ▼", "Moverse por la lista · sin lista a la vista: cambiar canal"),
        ("Enter / OK", "Ver el canal elegido · en pantalla completa: abrir la lista"),
        ("RePág / AvPág · CH+ / CH−", "Canal anterior / siguiente"),
        ("0 – 9", "Ir directo al número de canal"),
        ("◀ ▶", "En la lista: cambiar de lista · sobre el video: volumen"),
        ("Atrás / Retroceso", "Cerrar, salir de pantalla completa o volver al canal anterior"),
        ("Inicio · Menú", "Ir a la lista de canales"),
        ("F11 · F · doble clic", "Pantalla completa (Esc para salir)"),
        ("Espacio · ⏯", "Pausa / reanudar · ⏹ detener · ⏭ ⏮ cambiar canal"),
        ("+ / − · M", "Volumen · silenciar"),
        ("A · I · *", "Relación de aspecto · información técnica · favorito"),
        ("Ctrl + F · /", "Buscar"),
    };

    public AboutWindow()
    {
        InitializeComponent();
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Versión {v?.Major}.{v?.Minor}.{v?.Build}";
        int row = 0;
        foreach (var (k, a) in Shortcuts)
        {
            Keys.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var cap = new Border { Style = (Style)FindResource("KeyCap"), Child = new TextBlock { Text = k, FontSize = 12, Style = (Style)FindResource("Row") } };
            Grid.SetRow(cap, row);
            var t = new TextBlock { Text = a, Style = (Style)FindResource("Row"), TextWrapping = TextWrapping.Wrap };
            t.Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush");
            Grid.SetRow(t, row); Grid.SetColumn(t, 1);
            Keys.Children.Add(cap); Keys.Children.Add(t);
            row++;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
