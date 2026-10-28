using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ImmeubleCad.Desktop;

/// <summary>File logger: log.txt in the app-data folder, rolling at 5 MB keeping 5 files, buffered on a background queue.</summary>
public sealed class AppLogProvider : ILoggerProvider
{
    private readonly string _dir; private readonly long _maxBytes; private readonly LogLevel _min;
    private readonly BlockingCollection<string> _q = new();
    private readonly Thread _t; private readonly object _flush = new();
    private int _pending;

    public AppLogProvider(string? dir = null, long maxBytes = 5_000_000, LogLevel min = LogLevel.Information)
    {
        _dir = dir ?? DefaultDir(); _maxBytes = maxBytes; _min = min; Directory.CreateDirectory(_dir);
        Cleanup();
        _t = new Thread(Pump) { IsBackground = true, Name = "AppLog" }; _t.Start();
    }

    public static string DefaultDir() =>
        OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImmeubleCad")
        : OperatingSystem.IsMacOS() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "Application Support", "ImmeubleCad")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".local", "share", "ImmeubleCad");

    public string LogPath => Path.Combine(_dir, "log.txt");
    public ILogger CreateLogger(string category) => new L(this, category);

    private void Enqueue(string line) { Interlocked.Increment(ref _pending); _q.Add(line); }

    /// <summary>Block until everything queued has been written.</summary>
    public void Flush() { var sw = Environment.TickCount64; while (Volatile.Read(ref _pending) > 0 && Environment.TickCount64 - sw < 5000) Thread.Sleep(5); }

    private void Pump()
    {
        foreach (var line in _q.GetConsumingEnumerable())
        {
            try { Roll(); File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8); } catch { /* logging must never throw */ }
            Interlocked.Decrement(ref _pending);
        }
    }

    private void Roll()
    {
        var fi = new FileInfo(LogPath);
        if (!fi.Exists || fi.Length < _maxBytes) return;
        File.Delete(Path.Combine(_dir, "log.4.txt"));
        for (var i = 3; i >= 1; i--) { var f = Path.Combine(_dir, $"log.{i}.txt"); if (File.Exists(f)) File.Move(f, Path.Combine(_dir, $"log.{i + 1}.txt")); }
        File.Move(LogPath, Path.Combine(_dir, "log.1.txt"));
    }

    private void Cleanup()
    {
        foreach (var f in Directory.GetFiles(_dir, "log.*.txt"))
            if (int.TryParse(Path.GetFileNameWithoutExtension(f).Split('.').Last(), out var n) && n > 4) File.Delete(f);
    }

    public void Dispose() { _q.CompleteAdding(); _t.Join(2000); }

    private sealed class L(AppLogProvider p, string cat) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel l) => l >= p._min;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
        {
            if (!IsEnabled(level)) return;
            var sb = new StringBuilder($"{DateTime.UtcNow:O} {level,-11} {cat} {fmt(state, ex)}");
            if (ex != null) sb.AppendLine().Append(ex);
            p.Enqueue(sb.ToString());
            if (level >= LogLevel.Error) p.Flush();
        }
    }

    /// <summary>Route everything that escapes into the log.</summary>
    public void HookGlobalHandlers(ILogger log)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { log.LogCritical(e.ExceptionObject as Exception, "Unhandled exception"); Flush(); };
        TaskScheduler.UnobservedTaskException += (_, e) => { log.LogError(e.Exception, "Unobserved task exception"); e.SetObserved(); };
    }
}
