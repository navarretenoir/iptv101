using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace IPTV101.Data;

/// <summary>Logos de canales: caché en disco + memoria, descarga perezosa con prioridad a lo último pedido.</summary>
public sealed class LogoCache
{
    private const int Workers = 4;
    private const int MemoryLimit = 500;
    private const int DecodeHeight = 96;
    private const long MaxBytes = 1_500_000;
    private static readonly TimeSpan FailureTtl = TimeSpan.FromDays(3);

    private readonly Dispatcher _ui;
    private readonly HttpClient _http;
    private readonly ConcurrentStack<string> _queue = new();
    private readonly ConcurrentDictionary<string, List<Action<ImageSource?>>> _waiting = new();
    private readonly Dictionary<string, ImageSource> _memory = new();
    private readonly LinkedList<string> _order = new();
    private readonly object _memLock = new();
    private readonly SemaphoreSlim _signal = new(0);

    public LogoCache(Dispatcher ui)
    {
        _ui = ui;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(6),
            MaxConnectionsPerServer = 4,
        })
        { Timeout = TimeSpan.FromSeconds(12) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(Channels.Channel.DefaultUserAgent);
        for (int i = 0; i < Workers; i++)
        {
            var t = new Thread(WorkLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "logos" };
            t.Start();
        }
    }

    public ImageSource? TryGetMemory(string url)
    {
        lock (_memLock) return _memory.TryGetValue(url, out var img) ? img : null;
    }

    /// <summary>Pide un logo; el callback se ejecuta en el hilo de interfaz (null si no hay logo).</summary>
    public void Request(string url, Action<ImageSource?> callback)
    {
        var mem = TryGetMemory(url);
        if (mem != null) { callback(mem); return; }
        bool first = false;
        var list = _waiting.GetOrAdd(url, _ => { first = true; return new List<Action<ImageSource?>>(); });
        lock (list) list.Add(callback);
        if (first)
        {
            _queue.Push(url);
            _signal.Release();
        }
    }

    private void WorkLoop()
    {
        while (true)
        {
            _signal.Wait();
            if (!_queue.TryPop(out var url)) continue;
            ImageSource? img = null;
            try { img = LoadOrDownload(url); } catch (Exception) { }
            if (img != null) Remember(url, img);
            if (_waiting.TryRemove(url, out var callbacks))
            {
                Action<ImageSource?>[] cbs;
                lock (callbacks) cbs = callbacks.ToArray();
                _ui.BeginInvoke(() => { foreach (var cb in cbs) cb(img); }, DispatcherPriority.Background);
            }
        }
    }

    private void Remember(string url, ImageSource img)
    {
        lock (_memLock)
        {
            if (_memory.ContainsKey(url)) return;
            _memory[url] = img;
            _order.AddLast(url);
            while (_order.Count > MemoryLimit)
            {
                _memory.Remove(_order.First!.Value);
                _order.RemoveFirst();
            }
        }
    }

    private static string KeyFor(string url)
    {
        var h = SHA1.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(h).ToLowerInvariant();
    }

    private ImageSource? LoadOrDownload(string url)
    {
        var key = KeyFor(url);
        var file = Path.Combine(AppPaths.Logos, key + ".img");
        var fail = Path.Combine(AppPaths.Logos, key + ".sin");

        if (File.Exists(file))
        {
            var cached = Decode(File.ReadAllBytes(file));
            if (cached != null) return cached;
            File.Delete(file);
        }
        if (File.Exists(fail) && DateTime.UtcNow - File.GetLastWriteTimeUtc(fail) < FailureTtl) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            MarkFailed(fail);
            return null;
        }

        byte[]? data = null;
        try
        {
            using var resp = _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            if (resp.IsSuccessStatusCode && (resp.Content.Headers.ContentLength ?? 0) <= MaxBytes)
            {
                data = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                if (data.Length > MaxBytes) data = null;
            }
        }
        catch (Exception) { data = null; }

        var img = data != null ? Decode(data) : null;
        if (img == null) { MarkFailed(fail); return null; }
        try { File.WriteAllBytes(file, data!); } catch (IOException) { }
        return img;
    }

    private static void MarkFailed(string path)
    {
        try { File.WriteAllBytes(path, Array.Empty<byte>()); File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
        catch (IOException) { }
    }

    private static ImageSource? Decode(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelHeight = DecodeHeight;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception) { return null; }
    }
}
