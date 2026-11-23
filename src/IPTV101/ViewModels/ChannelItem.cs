using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using IPTV101.Channels;
using IPTV101.Data;

namespace IPTV101.ViewModels;

/// <summary>Elemento de la lista de canales. El logo se pide recién cuando la fila se muestra.</summary>
public sealed class ChannelItem : INotifyPropertyChanged
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x2E, 0x6B, 0xE6), Color.FromRgb(0x7A, 0x4C, 0xE0), Color.FromRgb(0x0E, 0x8F, 0x8A),
        Color.FromRgb(0xC2, 0x4E, 0x2C), Color.FromRgb(0xB0, 0x3A, 0x7A), Color.FromRgb(0x3C, 0x8C, 0x3F),
        Color.FromRgb(0x9A, 0x6A, 0x12), Color.FromRgb(0x2B, 0x7E, 0xB8),
    };
    private static readonly Dictionary<Color, SolidColorBrush> BrushCache = new();

    public static LogoCache? Logos { get; set; }

    private ImageSource? _logo;
    private bool _logoRequested;
    private bool _isFavorite;
    private bool _isPlaying;

    public Channel Channel { get; }
    public ChannelItem(Channel c) { Channel = c; }

    public string Name => Channel.Name;
    public string Group => Channel.Group;
    public string Source => Channel.Source;
    public int Number => Channel.Number;
    public string NumberText => Channel.Number.ToString();
    public string Url => Channel.Url;

    public string Initials
    {
        get
        {
            var parts = Name.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "TV";
            if (parts.Length == 1) return parts[0].Length >= 2 ? parts[0][..2].ToUpperInvariant() : parts[0].ToUpperInvariant();
            return (char.ToUpperInvariant(parts[0][0]).ToString() + char.ToUpperInvariant(parts[1][0])).Trim();
        }
    }

    public Brush BadgeBrush
    {
        get
        {
            uint h = 2166136261;
            foreach (var ch in Name) h = (h ^ ch) * 16777619;
            var c = Palette[(int)(h % (uint)Palette.Length)];
            if (!BrushCache.TryGetValue(c, out var b)) { b = new SolidColorBrush(c); b.Freeze(); BrushCache[c] = b; }
            return b;
        }
    }

    public ImageSource? Logo
    {
        get
        {
            if (_logo == null && !_logoRequested && Channel.LogoUrl != null && Logos != null)
            {
                _logoRequested = true;
                var mem = Logos.TryGetMemory(Channel.LogoUrl);
                if (mem != null) _logo = mem;
                else Logos.Request(Channel.LogoUrl, img => { if (img != null) { _logo = img; OnChanged(); OnChanged(nameof(HasLogo)); } });
            }
            return _logo;
        }
    }

    public bool HasLogo => _logo != null;

    public bool IsFavorite
    {
        get => _isFavorite;
        set { if (_isFavorite != value) { _isFavorite = value; OnChanged(); } }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set { if (_isPlaying != value) { _isPlaying = value; OnChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
