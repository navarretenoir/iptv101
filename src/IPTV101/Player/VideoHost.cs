using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace IPTV101.Player;

/// <summary>Ventana hija nativa donde el motor dibuja el video. Recibe el mouse por encima del video.</summary>
public sealed class VideoHost : HwndHost
{
    private const string ClassName = "TVVideoSurface";
    private static Native.WndProc? s_proc;
    private static bool s_registered;
    private IntPtr _hwnd;
    private bool _cursorHidden;

    public new IntPtr Handle => _hwnd;

    public event Action? SurfaceDoubleClick;
    public event Action? SurfaceMouseMove;
    public event Action? SurfaceClick;
    public event Action? SurfaceRightClick;
    public event Action<int>? SurfaceWheel;

    public bool CursorHidden
    {
        get => _cursorHidden;
        set
        {
            if (_cursorHidden == value) return;
            _cursorHidden = value;
            if (Native.GetCursorPos(out var pt) && Native.WindowFromPoint(pt) == _hwnd)
                Native.SetCursor(value ? IntPtr.Zero : Native.LoadCursor(IntPtr.Zero, Native.IDC_ARROW));
        }
    }

    private static readonly Dictionary<IntPtr, VideoHost> s_hosts = new();
    private int _lastX = int.MinValue, _lastY;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        if (!s_registered)
        {
            s_proc = StaticProc;
            var wc = new Native.WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(),
                style = Native.CS_DBLCLKS | Native.CS_HREDRAW | Native.CS_VREDRAW,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_proc),
                hInstance = Native.GetModuleHandle(null),
                hbrBackground = Native.GetStockObject(Native.BLACK_BRUSH),
                lpszClassName = ClassName,
                hCursor = IntPtr.Zero,
            };
            Native.RegisterClassEx(ref wc);
            s_registered = true;
        }
        _hwnd = Native.CreateWindowEx(0, ClassName, "",
            Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_CLIPCHILDREN | Native.WS_CLIPSIBLINGS,
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);
        s_hosts[_hwnd] = this;

        // Un solo relleno negro inicial (evita el destello del color de fondo del sistema antes de que
        // mpv tome esta ventana). A partir de aquí la ventana queda enteramente en manos de mpv: nunca
        // se vuelve a pintar manualmente, porque mpv presenta su cadena de intercambio D3D11 (flip-model)
        // directamente sobre este mismo HWND, y cualquier pintura GDI posterior (incluido un simple
        // WM_PAINT repintado) compite con esa presentación y puede taparla con negro.
        Native.GetClientRect(_hwnd, out var rc0);
        var dc0 = Native.GetWindowDC(_hwnd);
        Native.FillRect(dc0, ref rc0, Native.GetStockObject(Native.BLACK_BRUSH));
        Native.ReleaseDC(_hwnd, dc0);

        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        s_hosts.Remove(hwnd.Handle);
        Native.DestroyWindow(hwnd.Handle);
    }

    /// <summary>Deshabilita las ventanas hijas del motor para que el mouse llegue a esta superficie.</summary>
    public void PassThroughChildren()
    {
        if (_hwnd == IntPtr.Zero) return;
        Native.EnumChildWindows(_hwnd, (h, _) => { Native.EnableWindow(h, false); return true; }, IntPtr.Zero);
    }

    private static IntPtr StaticProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (s_hosts.TryGetValue(hwnd, out var host))
        {
            switch (msg)
            {
                case Native.WM_SETCURSOR:
                    Native.SetCursor(host._cursorHidden ? IntPtr.Zero : Native.LoadCursor(IntPtr.Zero, Native.IDC_ARROW));
                    return (IntPtr)1;
                case Native.WM_MOUSEMOVE:
                    {
                        int x = (short)((long)lParam & 0xFFFF), y = (short)(((long)lParam >> 16) & 0xFFFF);
                        if (x != host._lastX || y != host._lastY)
                        {
                            host._lastX = x; host._lastY = y;
                            host.SurfaceMouseMove?.Invoke();
                        }
                        return IntPtr.Zero;
                    }
                case Native.WM_LBUTTONDBLCLK:
                    host.SurfaceDoubleClick?.Invoke();
                    return IntPtr.Zero;
                case Native.WM_LBUTTONUP:
                    host.SurfaceClick?.Invoke();
                    return IntPtr.Zero;
                case Native.WM_RBUTTONUP:
                    host.SurfaceRightClick?.Invoke();
                    return IntPtr.Zero;
                case Native.WM_MOUSEWHEEL:
                    host.SurfaceWheel?.Invoke((short)(((long)wParam >> 16) & 0xFFFF));
                    return IntPtr.Zero;
                case Native.WM_ERASEBKGND:
                    // Nunca dejar que Windows borre esta ventana: eso dibujaría sobre la presentación de mpv.
                    return (IntPtr)1;
                case Native.WM_PAINT:
                    {
                        // Solo validar la región: no dibujar nada. El video (hijo que crea mpv dentro de
                        // este HWND) se presenta por su cuenta vía D3D11 y no pasa por WM_PAINT; si
                        // nosotros pintáramos aquí, taparíamos esa presentación con negro en cada repintado
                        // (p. ej. al redimensionar, restaurar la ventana o superponer otra ventana encima).
                        var ps = new Native.PAINTSTRUCT();
                        Native.BeginPaint(hwnd, ref ps);
                        Native.EndPaint(hwnd, ref ps);
                        return IntPtr.Zero;
                    }
            }
        }
        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
