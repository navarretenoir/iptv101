using System.Windows;
using System.Windows.Interop;

namespace IPTV101;

/// <summary>Capa transparente sobre el video: canal actual, avisos y controles en pantalla completa.</summary>
public partial class OsdWindow : Window
{
    private IntPtr _hwnd;
    private Native.RECT _rect;

    public OsdWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
            ex |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, (IntPtr)ex);
            Apply();
        };
    }

    /// <summary>Ubica la capa exactamente sobre la superficie de video (coordenadas físicas de pantalla).</summary>
    public void PlaceOver(Native.RECT r)
    {
        _rect = r;
        Apply();
    }

    private void Apply()
    {
        if (_hwnd == IntPtr.Zero || _rect.Right <= _rect.Left) return;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, _rect.Left, _rect.Top, _rect.Right - _rect.Left, _rect.Bottom - _rect.Top,
            Native.SWP_NOACTIVATE | Native.SWP_NOZORDER | Native.SWP_NOOWNERZORDER);
    }

    private bool _suspended;

    /// <summary>Oculta la capa mientras la ventana principal está minimizada.</summary>
    public bool Suspended
    {
        get => _suspended;
        set { _suspended = value; Refresh(); }
    }

    /// <summary>Muestra la ventana solo si hay algo visible; así no se compone una capa vacía sobre el video.</summary>
    public void Refresh()
    {
        bool any = !_suspended && (Banner.Visibility == Visibility.Visible || Chip.Visibility == Visibility.Visible
                   || Center.Visibility == Visibility.Visible || Stats.Visibility == Visibility.Visible
                   || FsBar.Visibility == Visibility.Visible);
        if (any && !IsVisible) { Show(); Apply(); }
        else if (!any && IsVisible) Hide();
    }
}
