using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;

namespace SkyRender.CellDemo;

/// <summary>
/// The cell demo's log: every line goes to the in-window log pane, to a file (flushed per line, so the
/// trail survives a frozen UI or a crash) and to the debugger output. Safe to call from any thread.
/// </summary>
internal sealed class CellLog : IRenderLogger
{
    private readonly object _fileLock = new();
    private readonly StreamWriter? _file;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Dispatcher? _dispatcher;
    private Action<string>? _sink;

    public CellLog()
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "SkyRender.CellDemo");
            Directory.CreateDirectory(dir);
            FilePath = Path.Combine(dir, "celldemo.log");
            _file = new StreamWriter(FilePath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch
        {
            // No file log; the pane and the debugger output still work.
        }
    }

    public static CellLog Instance { get; } = new();

    /// <summary>The log file (under %TEMP%\SkyRender.CellDemo), or null if it could not be created.</summary>
    public string? FilePath { get; }

    /// <summary>Connects the log pane. Lines logged before this are kept in the file only.</summary>
    public void Attach(Dispatcher dispatcher, Action<string> sink)
    {
        _dispatcher = dispatcher;
        _sink = sink;
    }

    public void Info(string message) => Write("INFO ", message);
    public void LogMessage(string message) => Write("     ", message);
    public void LogError(string message) => Write("ERROR", message);
    public void LogError(string message, Exception ex) => Write("ERROR", message + ": " + ex);

    private void Write(string level, string message)
    {
        string line = $"{_clock.Elapsed.TotalSeconds,8:0.000} [{Environment.CurrentManagedThreadId,2}] {level} {message}";
        Debug.WriteLine(line);
        lock (_fileLock)
        {
            try { _file?.WriteLine(line); } catch { }
        }
        if (_sink is { } sink && _dispatcher is { } dispatcher)
            dispatcher.BeginInvoke(sink, DispatcherPriority.Normal, line);
    }
}
