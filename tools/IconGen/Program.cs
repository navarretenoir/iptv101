using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

// Genera el ícono propio de IPTV 101 (SVG + ICO multirresolución + PNG) a partir de una sola
// definición geométrica en un lienzo de 256 unidades. Diseño original: un televisor de tubo con
// antena de "orejas de conejo" (la época analógica) y, en la base, una franja de espectro que va
// de gris a color (el guiño a "ver televisión a través de las épocas"). No reutiliza el ícono de TV.

internal static class Program
{
    private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };

    [STAThread]
    private static int Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outDir);
        var pngs = new List<(int size, byte[] data)>();
        foreach (var s in Sizes)
        {
            var data = Render(s);
            pngs.Add((s, data));
            if (s is 16 or 32 or 48 or 256 or 512) File.WriteAllBytes(Path.Combine(outDir, $"iptv101-{s}.png"), data);
        }
        File.WriteAllBytes(Path.Combine(outDir, "iptv101-512.png"), Render(512));
        WriteIco(Path.Combine(outDir, "iptv101.ico"), pngs);
        File.WriteAllText(Path.Combine(outDir, "iptv101.svg"), Svg());
        Console.WriteLine("Ícono generado en " + outDir);
        return 0;
    }

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static LinearGradientBrush Lin(string a, string b, double x1 = 0, double y1 = 0, double x2 = 1, double y2 = 1)
    {
        var g = new LinearGradientBrush(C(a), C(b), new Point(x1, y1), new Point(x2, y2));
        g.Freeze();
        return g;
    }

    private static byte[] Render(int size)
    {
        bool small = size <= 28;
        bool tiny = size <= 20;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            double k = size / 256.0;
            dc.PushTransform(new ScaleTransform(k, k));

            // Antena de "orejas de conejo": dos varillas en V. Se omite en tamaños diminutos.
            if (!tiny)
            {
                var antenna = new Pen(Lin("#AEB8C8", "#7C8699", 0, 0, 0, 1), small ? 7 : 6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawLine(antenna, new Point(118, 70), new Point(84, 10));
                dc.DrawLine(antenna, new Point(138, 70), new Point(172, 10));
                var hub = new SolidColorBrush(C("#7C8699"));
                dc.DrawEllipse(hub, null, new Point(128, 72), small ? 7 : 6, small ? 7 : 6);
            }

            // Base / pie.
            var foot = Lin("#5E6E92", "#36425F", 0, 0, 0, 1);
            if (!small)
            {
                dc.DrawRoundedRectangle(foot, null, new Rect(112, 204, 32, 16), 5, 5);
                dc.DrawRoundedRectangle(foot, null, new Rect(76, 218, 104, 14), 7, 7);
            }
            else
            {
                dc.DrawRoundedRectangle(foot, null, new Rect(70, 210, 116, 20), 10, 10);
            }

            // Cuerpo del televisor: carcasa redondeada, cálida (evoca baquelita/madera de los
            // aparatos de tubo), con un leve brillo superior.
            var bodyRect = tiny ? new Rect(8, 60, 240, 160) : small ? new Rect(10, 66, 236, 150) : new Rect(18, 76, 220, 136);
            double br = tiny ? 46 : small ? 38 : 28;
            dc.DrawRoundedRectangle(tiny ? new SolidColorBrush(C("#C97B3D")) : Lin("#D98C4A", "#9C5A2A"), null, bodyRect, br, br);
            if (!small)
            {
                var hi = new Pen(Lin("#55FFFFFF", "#00FFFFFF", 0, 0, 0, 1), 2);
                dc.DrawRoundedRectangle(null, hi, new Rect(bodyRect.X + 1, bodyRect.Y + 1, bodyRect.Width - 2, bodyRect.Height - 2), br - 1, br - 1);
            }

            // Pantalla.
            double inset = tiny ? 30 : small ? 24 : 18;
            var scr = new Rect(bodyRect.X + inset, bodyRect.Y + inset, bodyRect.Width - inset * 2, bodyRect.Height - inset * 2 - (small ? 0 : 8));
            double sr = tiny ? 26 : small ? 20 : 16;
            dc.DrawRoundedRectangle(tiny ? new SolidColorBrush(C("#0E1B2E")) : Lin("#16263F", "#0A1420"), null, scr, sr, sr);

            // Triángulo de "reproducir" al centro de la pantalla: sigue siendo un reproductor.
            double cx = scr.X + scr.Width / 2, cy = scr.Y + scr.Height / 2;
            double ts = tiny ? 34 : small ? 30 : 24;
            var tri = new StreamGeometry();
            using (var g = tri.Open())
            {
                g.BeginFigure(new Point(cx - ts * 0.42, cy - ts * 0.58), true, true);
                g.LineTo(new Point(cx - ts * 0.42, cy + ts * 0.58), true, false);
                g.LineTo(new Point(cx + ts * 0.62, cy), true, false);
            }
            tri.Freeze();
            dc.DrawGeometry(tiny ? new SolidColorBrush(Colors.White) : Lin("#7FF3FF", "#FFFFFF"), null, tri);

            // Franja de espectro en la base de la carcasa: de gris (blanco y negro) a color vivo,
            // el guiño a "todas las épocas de la televisión". Se omite en tamaños diminutos.
            if (!small)
            {
                double bandY = bodyRect.Bottom - (tiny ? 0 : 14);
                double bandH = 7;
                double bx0 = bodyRect.X + inset, bx1 = bodyRect.Right - inset;
                var band = new LinearGradientBrush(new GradientStopCollection
                {
                    new GradientStop(C("#9AA0A6"), 0.0),
                    new GradientStop(C("#9AA0A6"), 0.18),
                    new GradientStop(C("#E8536B"), 0.45),
                    new GradientStop(C("#F2A93C"), 0.62),
                    new GradientStop(C("#3DD68C"), 0.8),
                    new GradientStop(C("#4C8DFF"), 1.0),
                }, new Point(0, 0), new Point(1, 0));
                band.Freeze();
                dc.DrawRoundedRectangle(band, null, new Rect(bx0, bandY, bx1 - bx0, bandH), bandH / 2, bandH / 2);
            }

            dc.Pop();
        }

        var root = new ContainerVisual();
        if (size >= 32)
        {
            var shadow = new DrawingVisual();
            using (var dc = shadow.RenderOpen())
            {
                double k = size / 256.0;
                dc.PushTransform(new ScaleTransform(k, k));
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(70, 20, 15, 10)), null, new Rect(20, 90, 216, 136), 30, 30);
                dc.Pop();
            }
            shadow.Effect = new BlurEffect { Radius = Math.Max(1.5, size / 40.0), KernelType = KernelType.Gaussian };
            root.Children.Add(shadow);
        }
        root.Children.Add(dv);

        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static void WriteIco(string path, List<(int size, byte[] data)> images)
    {
        // El formato .ico clásico no admite más de 256px por entrada: se usan solo hasta 256 aquí.
        var entries = images.Where(i => i.size <= 256).ToList();
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)entries.Count);
        int offset = 6 + 16 * entries.Count;
        foreach (var (size, data) in entries)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32);
            w.Write(data.Length); w.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in entries) w.Write(data);
    }

    private static string Svg()
    {
        return """
<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  <title>IPTV 101</title>
  <defs>
    <linearGradient id="body" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#D98C4A"/><stop offset="1" stop-color="#9C5A2A"/></linearGradient>
    <linearGradient id="screen" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#16263F"/><stop offset="1" stop-color="#0A1420"/></linearGradient>
    <linearGradient id="foot" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#5E6E92"/><stop offset="1" stop-color="#36425F"/></linearGradient>
    <linearGradient id="antenna" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#AEB8C8"/><stop offset="1" stop-color="#7C8699"/></linearGradient>
    <linearGradient id="wave" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#7FF3FF"/><stop offset="1" stop-color="#FFFFFF"/></linearGradient>
    <linearGradient id="hi" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#FFFFFF" stop-opacity="0.33"/><stop offset="1" stop-color="#FFFFFF" stop-opacity="0"/></linearGradient>
    <linearGradient id="band" x1="0" y1="0" x2="1" y2="0">
      <stop offset="0" stop-color="#9AA0A6"/><stop offset="0.18" stop-color="#9AA0A6"/>
      <stop offset="0.45" stop-color="#E8536B"/><stop offset="0.62" stop-color="#F2A93C"/>
      <stop offset="0.8" stop-color="#3DD68C"/><stop offset="1" stop-color="#4C8DFF"/>
    </linearGradient>
    <filter id="shadow" x="-10%" y="-10%" width="120%" height="130%"><feGaussianBlur stdDeviation="5"/></filter>
  </defs>
  <rect x="20" y="90" width="216" height="136" rx="30" fill="#140F0A" fill-opacity="0.27" filter="url(#shadow)"/>
  <g stroke="url(#antenna)" stroke-width="6" stroke-linecap="round" fill="none">
    <line x1="118" y1="70" x2="84" y2="10"/>
    <line x1="138" y1="70" x2="172" y2="10"/>
  </g>
  <circle cx="128" cy="72" r="6" fill="#7C8699"/>
  <rect x="112" y="204" width="32" height="16" rx="5" fill="url(#foot)"/>
  <rect x="76" y="218" width="104" height="14" rx="7" fill="url(#foot)"/>
  <rect x="18" y="76" width="220" height="136" rx="28" fill="url(#body)"/>
  <rect x="19" y="77" width="218" height="134" rx="27" fill="none" stroke="url(#hi)" stroke-width="2"/>
  <rect x="36" y="94" width="184" height="94" rx="16" fill="url(#screen)"/>
  <path d="M118 148 L118 172 L148 160 Z" fill="url(#wave)"/>
  <rect x="36" y="198" width="184" height="7" rx="3.5" fill="url(#band)"/>
</svg>
""";
    }
}
