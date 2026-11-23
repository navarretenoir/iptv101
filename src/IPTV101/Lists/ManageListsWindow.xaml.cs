using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using IPTV101.Channels;
using IPTV101.Data;
using Microsoft.Win32;

namespace IPTV101.Lists;

/// <summary>Fila de la lista de listas: envuelve <see cref="PlaylistSource"/> con un texto de subtítulo ya armado.</summary>
public sealed class SourceRow
{
    public required PlaylistSource Source { get; init; }
    public int Id => Source.Id;
    public string Name => Source.Name;
    public bool CanUpdate => Source.CanUpdate;

    public string Subtitle
    {
        get
        {
            var kind = Source.IsRemote ? "URL" : "archivo local";
            var count = Source.ChannelCount == 1 ? "1 canal" : $"{Source.ChannelCount:N0} canales".Replace(",", ".");
            var when = Source.LastUpdated > 0
                ? DateTimeOffset.FromUnixTimeSeconds(Source.LastUpdated).ToLocalTime().ToString("d MMM yyyy, HH:mm")
                : "sin fecha";
            return $"{kind} · {count} · actualizada {when}";
        }
    }
}

/// <summary>
/// Pantalla de importación: agregar una lista desde archivo .m3u/.m3u8 local o desde una URL remota,
/// y gestionar (ver, actualizar, quitar) las que ya se importaron. IPTV 101 no trae ninguna por defecto.
/// </summary>
public partial class ManageListsWindow : Window
{
    /// <summary>En true si se agregó, quitó o actualizó algo: la ventana que abrió este diálogo debe recargar los canales.</summary>
    public bool ChangedAnything { get; private set; }

    private readonly ObservableCollection<SourceRow> _rows = new();
    private bool _busy;

    public ManageListsWindow()
    {
        InitializeComponent();
        SourcesList.ItemsSource = _rows;
        Loaded += (_, _) => RefreshRows();
    }

    private void RefreshRows()
    {
        _rows.Clear();
        foreach (var s in ChannelStore.LoadSources().OrderByDescending(s => s.LastUpdated))
            _rows.Add(new SourceRow { Source = s });
        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SourcesList.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetStatus(string text, bool error)
    {
        StatusBox.Visibility = Visibility.Visible;
        StatusText.Text = text;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(error ? "AppCriticalBrush" : "AppTextPrimaryBrush");
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UrlBox.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private async void ImportUrl_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var url = UrlBox.Text.Trim();
        if (url.Length == 0) { SetStatus("Pega primero la URL de una lista .m3u o .m3u8.", error: true); return; }

        SetBusy(true);
        SetStatus("Descargando e importando la lista…", error: false);
        try
        {
            var source = await ChannelStore.AddFromUrlAsync(url, null, CancellationToken.None);
            UrlBox.Clear();
            ChangedAnything = true;
            RefreshRows();
            SetStatus($"«{source.Name}» importada con {source.ChannelCount} canales.", error: false);
        }
        catch (InvalidDataException ex)
        {
            SetStatus("No se pudo importar la lista: " + ex.Message, error: true);
        }
        catch (Exception ex)
        {
            SetStatus("No se pudo importar la lista: " + ex.Message, error: true);
        }
        finally { SetBusy(false); }
    }

    private async void ImportFile_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dlg = new OpenFileDialog
        {
            Title = "Elegir lista M3U/M3U8",
            Filter = "Listas M3U (*.m3u;*.m3u8)|*.m3u;*.m3u8|Todos los archivos (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        SetBusy(true);
        SetStatus("Importando el archivo…", error: false);
        try
        {
            var source = await ChannelStore.AddFromFileAsync(dlg.FileName, CancellationToken.None);
            ChangedAnything = true;
            RefreshRows();
            SetStatus($"«{source.Name}» importada con {source.ChannelCount} canales.", error: false);
        }
        catch (InvalidDataException ex)
        {
            SetStatus("No se pudo importar el archivo: " + ex.Message, error: true);
        }
        catch (Exception ex)
        {
            SetStatus("No se pudo importar el archivo: " + ex.Message, error: true);
        }
        finally { SetBusy(false); }
    }

    private async void RefreshOne_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var id = (int)((Button)sender).Tag;
        SetBusy(true);
        SetStatus("Actualizando…", error: false);
        try
        {
            var result = await ChannelStore.RefreshAsync(id, CancellationToken.None);
            ChangedAnything = true;
            RefreshRows();
            SetStatus(result.Ok ? $"«{result.Source}» actualizada: {result.Message}." : $"No se pudo actualizar «{result.Source}»: {result.Message}.", error: !result.Ok);
        }
        finally { SetBusy(false); }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var id = (int)((Button)sender).Tag;
        var row = _rows.FirstOrDefault(r => r.Id == id);
        if (row == null) return;
        var r = MessageBox.Show(this, $"¿Quitar la lista «{row.Name}»? Esto no borra el archivo original si la importaste desde tu disco.",
            "Quitar lista", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        ChannelStore.RemoveSource(id);
        ChangedAnything = true;
        RefreshRows();
        SetStatus($"«{row.Name}» se quitó de IPTV 101.", error: false);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
