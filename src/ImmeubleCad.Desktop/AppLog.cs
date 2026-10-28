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

    public AppLogProvider(string? dir = null, long maxBytes = 5_000_000, LogLevel? min = null)
    {
        _dir = dir ?? DefaultDir(); _maxBytes = maxBytes; _min = min ?? LevelFromEnvironment(); Directory.CreateDirectory(_dir);
        Cleanup();
        _t = new Thread(Pump) { IsBackground = true, Name = "AppLog" }; _t.Start();
    }

    /// <summary>Per-OS app-data folder; IMMEUBLECAD_DATA_DIR overrides it (tests never touch the user's real folder).</summary>
    public static string DefaultDir() =>
        Environment.GetEnvironmentVariable("IMMEUBLECAD_DATA_DIR") is { Length: > 0 } over ? over :
        OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImmeubleCad")
        : OperatingSystem.IsMacOS() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "ImmeubleCad")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "ImmeubleCad");

    /// <summary>Information by default; Debug with IMMEUBLECAD_LOG=debug.</summary>
    public static LogLevel LevelFromEnvironment() => string.Equals(Environment.GetEnvironmentVariable("IMMEUBLECAD_LOG"), "debug", StringComparison.OrdinalIgnoreCase) ? LogLevel.Debug : LogLevel.Information;
    /// <summary>IMMEUBLECAD_LOG=1 (or debug) also mirrors to stderr; stdout is never used (it is the MCP protocol).</summary>
    public static bool MirrorToStderr() => Environment.GetEnvironmentVariable("IMMEUBLECAD_LOG") is "1" or "debug";

    public string LogPath => Path.Combine(_dir, "log.txt");
    public ILogger CreateLogger(string category) => new L(this, category);

    private void Enqueue(string line)
    {
        if (_q.IsAddingCompleted) return;   // logging after dispose must never throw
        Interlocked.Increment(ref _pending);
        try { _q.Add(line); } catch (InvalidOperationException) { Interlocked.Decrement(ref _pending); }
    }

    /// <summary>Block until everything queued has been written.</summary>
    public void Flush() { var sw = Environment.TickCount64; while (Volatile.Read(ref _pending) > 0 && Environment.TickCount64 - sw < 5000) Thread.Sleep(5); }

    private void Pump()
    {
        foreach (var line in _q.GetConsumingEnumerable())
        {
            try { Roll(); File.AppendAllText(LogPath, line + Environment.NewLine, new UTF8Encoding(false)); if (MirrorToStderr()) Console.Error.WriteLine(line); } catch { /* logging must never throw */ }
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
    public IDisposable HookGlobalHandlers(ILogger log)
    {
        UnhandledExceptionEventHandler a = (_, e) => { log.LogCritical(e.ExceptionObject as Exception, "Unhandled exception"); Flush(); };
        EventHandler<UnobservedTaskExceptionEventArgs> b = (_, e) => { log.LogError(e.Exception, "Unobserved task exception"); e.SetObserved(); };
        Avalonia.Threading.DispatcherUnhandledExceptionEventHandler c = (_, e) => { log.LogError(e.Exception, "Unhandled UI exception"); Flush(); e.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += a; TaskScheduler.UnobservedTaskException += b; Avalonia.Threading.Dispatcher.UIThread.UnhandledException += c;
        return new Unhook(() => { AppDomain.CurrentDomain.UnhandledException -= a; TaskScheduler.UnobservedTaskException -= b; Avalonia.Threading.Dispatcher.UIThread.UnhandledException -= c; });
    }
    private sealed class Unhook(Action a) : IDisposable { public void Dispose() => a(); }

    /// <summary>Last lines of the log, for bug reports.</summary>
    public string Tail(int lines = 200) { Flush(); try { return File.Exists(LogPath) ? string.Join(Environment.NewLine, File.ReadLines(LogPath).TakeLast(lines)) : ""; } catch { return ""; } }

    public static string SystemInfo() =>
        $"ImmeubleCad {typeof(AppLogProvider).Assembly.GetName().Version}  built {File.GetLastWriteTimeUtc(typeof(AppLogProvider).Assembly.Location):yyyy-MM-dd}\n{System.Runtime.InteropServices.RuntimeInformation.OSDescription} {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}  .NET {Environment.Version}";
}
