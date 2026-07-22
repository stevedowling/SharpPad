using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SharpPad.Runtime;

/// <summary>
/// Child-process side of the wire protocol. Frames are single JSON lines on the
/// real stdout, prefixed with a magic marker so stray direct writes to the
/// stdout stream can't be confused with frames. Console.Out/Error are
/// redirected into "out"/"err" frames.
/// </summary>
public static class Protocol
{
    public const string Magic = "SPAD";

    private static readonly object Lock = new();
    private static TextWriter? _raw;
    private static Stopwatch? _watch;
    private static bool _doneSent;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Called from a [ModuleInitializer] generated into the script assembly,
    /// i.e. before any top-level statement runs.</summary>
    public static void Init()
    {
        if (_raw is not null) return;
        _watch = Stopwatch.StartNew();
        _raw = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(new FrameTextWriter("out"));
        Console.SetError(new FrameTextWriter("err"));

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Send(new Frame { Type = "ex", Text = ex?.ToString() ?? e.ExceptionObject?.ToString() });
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Done();
    }

    public static void SendDump(string? title, DumpNode node) =>
        Send(new Frame { Type = "dump", Title = title, Node = node });

    public static void Done()
    {
        lock (Lock)
        {
            if (_doneSent) return;
            _doneSent = true;
        }
        Send(new Frame { Type = "done", ElapsedMs = _watch?.ElapsedMilliseconds });
    }

    private static void Send(Frame frame)
    {
        if (_raw is null) Init();
        var json = JsonSerializer.Serialize(frame, JsonOpts);
        lock (Lock) _raw!.WriteLine(Magic + json);
    }

    private sealed class FrameTextWriter : TextWriter
    {
        private readonly string _type;
        private readonly StringBuilder _buf = new();
        public FrameTextWriter(string type) => _type = type;
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char c)
        {
            lock (_buf)
            {
                if (c == '\n') FlushLine();
                else if (c != '\r') _buf.Append(c);
            }
        }

        public override void Write(string? s)
        {
            if (s is null) return;
            foreach (var c in s) Write(c);
        }

        public override void WriteLine(string? s)
        {
            Write(s);
            Write('\n');
        }

        public override void Flush()
        {
            lock (_buf) { if (_buf.Length > 0) FlushLine(); }
        }

        private void FlushLine()
        {
            var text = _buf.ToString();
            _buf.Clear();
            Send(new Frame { Type = _type, Text = text });
        }
    }
}
