using System.Runtime.InteropServices;
using System.Text;

namespace IPTV101.Player;

/// <summary>Enlace mínimo a libmpv-2.dll (API cliente C).</summary>
internal static unsafe partial class MpvNative
{
    private const string Lib = "libmpv-2.dll";

    public const int FORMAT_NONE = 0, FORMAT_STRING = 1, FORMAT_FLAG = 3, FORMAT_INT64 = 4, FORMAT_DOUBLE = 5;

    public const int EVENT_NONE = 0, EVENT_SHUTDOWN = 1, EVENT_LOG_MESSAGE = 2, EVENT_COMMAND_REPLY = 5,
        EVENT_START_FILE = 6, EVENT_END_FILE = 7, EVENT_FILE_LOADED = 8, EVENT_IDLE = 11,
        EVENT_VIDEO_RECONFIG = 17, EVENT_AUDIO_RECONFIG = 18, EVENT_PLAYBACK_RESTART = 21, EVENT_PROPERTY_CHANGE = 22;

    public const int END_EOF = 0, END_STOP = 2, END_QUIT = 3, END_ERROR = 4, END_REDIRECT = 5;

    [StructLayout(LayoutKind.Sequential)]
    public struct Event
    {
        public int EventId;
        public int Error;
        public ulong ReplyUserdata;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventProperty
    {
        public IntPtr Name;
        public int Format;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventEndFile
    {
        public int Reason;
        public int Error;
        public long PlaylistEntryId;
        public long PlaylistInsertId;
        public int PlaylistInsertNumEntries;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventLogMessage
    {
        public IntPtr Prefix;
        public IntPtr Level;
        public IntPtr Text;
        public int LogLevel;
    }

    [LibraryImport(Lib, EntryPoint = "mpv_create")]
    public static partial IntPtr Create();

    [LibraryImport(Lib, EntryPoint = "mpv_initialize")]
    public static partial int Initialize(IntPtr ctx);

    [LibraryImport(Lib, EntryPoint = "mpv_terminate_destroy")]
    public static partial void TerminateDestroy(IntPtr ctx);

    [LibraryImport(Lib, EntryPoint = "mpv_wakeup")]
    public static partial void Wakeup(IntPtr ctx);

    [LibraryImport(Lib, EntryPoint = "mpv_wait_event")]
    public static partial Event* WaitEvent(IntPtr ctx, double timeout);

    [LibraryImport(Lib, EntryPoint = "mpv_free")]
    public static partial void Free(IntPtr data);

    [LibraryImport(Lib, EntryPoint = "mpv_error_string")]
    public static partial IntPtr ErrorString(int error);

    [LibraryImport(Lib, EntryPoint = "mpv_set_option_string")]
    private static partial int SetOptionStringRaw(IntPtr ctx, byte* name, byte* data);

    [LibraryImport(Lib, EntryPoint = "mpv_command")]
    private static partial int CommandRaw(IntPtr ctx, IntPtr* args);

    [LibraryImport(Lib, EntryPoint = "mpv_command_async")]
    private static partial int CommandAsyncRaw(IntPtr ctx, ulong userdata, IntPtr* args);

    [LibraryImport(Lib, EntryPoint = "mpv_get_property")]
    private static partial int GetPropertyRaw(IntPtr ctx, byte* name, int format, void* data);

    [LibraryImport(Lib, EntryPoint = "mpv_observe_property")]
    private static partial int ObservePropertyRaw(IntPtr ctx, ulong userdata, byte* name, int format);

    [LibraryImport(Lib, EntryPoint = "mpv_request_log_messages")]
    private static partial int RequestLogMessagesRaw(IntPtr ctx, byte* level);

    private static IntPtr Utf8(string s)
    {
        var bytes = Encoding.UTF8.GetByteCount(s);
        var p = (byte*)NativeMemory.Alloc((nuint)bytes + 1);
        fixed (char* c = s) Encoding.UTF8.GetBytes(c, s.Length, p, bytes);
        p[bytes] = 0;
        return (IntPtr)p;
    }

    public static string? FromUtf8(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

    public static int SetOption(IntPtr ctx, string name, string value)
    {
        var n = Utf8(name); var v = Utf8(value);
        try { return SetOptionStringRaw(ctx, (byte*)n, (byte*)v); }
        finally { NativeMemory.Free((void*)n); NativeMemory.Free((void*)v); }
    }

    private delegate int ArgsCall(IntPtr* args);

    private static int WithArgs(string[] args, ArgsCall call)
    {
        var ptrs = stackalloc IntPtr[args.Length + 1];
        for (int i = 0; i < args.Length; i++) ptrs[i] = Utf8(args[i]);
        ptrs[args.Length] = IntPtr.Zero;
        try { return call(ptrs); }
        finally { for (int i = 0; i < args.Length; i++) NativeMemory.Free((void*)ptrs[i]); }
    }

    public static int Command(IntPtr ctx, params string[] args) => WithArgs(args, p => CommandRaw(ctx, p));

    public static int CommandAsync(IntPtr ctx, ulong userdata, params string[] args) => WithArgs(args, p => CommandAsyncRaw(ctx, userdata, p));

    public static bool TryGetDouble(IntPtr ctx, string name, out double value)
    {
        var n = Utf8(name);
        try
        {
            double d = 0;
            int r = GetPropertyRaw(ctx, (byte*)n, FORMAT_DOUBLE, &d);
            value = d;
            return r >= 0;
        }
        finally { NativeMemory.Free((void*)n); }
    }

    public static bool TryGetLong(IntPtr ctx, string name, out long value)
    {
        var n = Utf8(name);
        try
        {
            long d = 0;
            int r = GetPropertyRaw(ctx, (byte*)n, FORMAT_INT64, &d);
            value = d;
            return r >= 0;
        }
        finally { NativeMemory.Free((void*)n); }
    }

    public static string? GetString(IntPtr ctx, string name)
    {
        var n = Utf8(name);
        try
        {
            IntPtr s = IntPtr.Zero;
            if (GetPropertyRaw(ctx, (byte*)n, FORMAT_STRING, &s) < 0 || s == IntPtr.Zero) return null;
            var r = FromUtf8(s);
            Free(s);
            return r;
        }
        finally { NativeMemory.Free((void*)n); }
    }

    public static int Observe(IntPtr ctx, ulong id, string name, int format)
    {
        var n = Utf8(name);
        try { return ObservePropertyRaw(ctx, id, (byte*)n, format); }
        finally { NativeMemory.Free((void*)n); }
    }

    public static int RequestLog(IntPtr ctx, string level)
    {
        var n = Utf8(level);
        try { return RequestLogMessagesRaw(ctx, (byte*)n); }
        finally { NativeMemory.Free((void*)n); }
    }
}
