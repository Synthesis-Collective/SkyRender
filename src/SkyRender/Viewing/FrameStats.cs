using System.Globalization;
using System.Text;

namespace SkyRender.Viewing;

/// <summary>
/// One drawn frame of a live view. Times are milliseconds on the view's clock.
/// </summary>
/// <param name="Frame">The frame's number since the view started.</param>
/// <param name="StartMs">When the frame began (the camera is read here).</param>
/// <param name="PresentMs">When the host had handed the frame over: after <c>SwapBuffers</c> in a
/// native window, after the frame callback returned in a shared-texture view.</param>
/// <param name="IntervalMs">Since the previous frame's <paramref name="PresentMs"/>; 0 for the first.</param>
/// <param name="StartIntervalMs">Since the previous frame's <paramref name="StartMs"/>; 0 for the first.</param>
/// <param name="RenderMs">CPU time of the frame callback (issuing the GL calls).</param>
/// <param name="PresentCallMs">Time in the host's hand-over call (the buffer swap), or NaN where the
/// host has none to time.</param>
/// <param name="GpuMs">GPU time of a recent frame's draw (<see cref="GpuTimer"/>), or NaN.</param>
/// <param name="Resumed">True for the first frame after the view sat with nothing to draw.</param>
/// <param name="Reason">What first asked for this frame ("input", "camera", "fly", "continuous", ...).</param>
/// <param name="ReasonAgeMs">How long that request waited before the frame began.</param>
/// <param name="QueuedMs">How long the frame waited for the UI thread after the pacer posted it, or NaN.</param>
/// <param name="InputEvents">Mouse and key events handled since the previous frame.</param>
public readonly record struct FrameSample(
    long Frame, double StartMs, double PresentMs, double IntervalMs, double StartIntervalMs, double RenderMs,
    double PresentCallMs, double GpuMs, bool Resumed, string Reason, double ReasonAgeMs, double QueuedMs, int InputEvents,
    int Gc0, int Gc1, int Gc2, float Azimuth, float Elevation, float EyeX, float EyeY, float EyeZ, int Width, int Height);

/// <summary>
/// Frame intervals as a distribution: the median, the 1% low (the mean of the slowest 1% of
/// intervals, at least one), the worst, and how many were longer than 25 ms and 50 ms. An average
/// hides exactly the frames a person feels, so none is the headline.
/// </summary>
public readonly record struct IntervalSummary(int Count, double TotalMs, double MedianMs, double Low1Ms, double WorstMs, int Over25, int Over50)
{
    public double MeanFps => TotalMs > 0 ? Count * 1000.0 / TotalMs : 0;

    public string Words() => Count == 0 ? "no frames" : string.Create(CultureInfo.InvariantCulture,
        $"{Count} frames over {TotalMs / 1000.0:0.0} s ({MeanFps:0.0} fps mean); interval median {MedianMs:0.00} ms ({1000 / MedianMs:0} fps), 1% low {Low1Ms:0.00} ms ({1000 / Low1Ms:0} fps), worst {WorstMs:0.00} ms; {Over25} over 25 ms, {Over50} over 50 ms");

    public static IntervalSummary Of(IReadOnlyList<double> intervals)
    {
        if (intervals.Count == 0) return default;
        var s = intervals.OrderBy(x => x).ToArray();
        int slow = Math.Max(1, s.Length / 100);
        double low = 0;
        for (int i = s.Length - slow; i < s.Length; i++) low += s[i];
        return new IntervalSummary(s.Length, s.Sum(), s[s.Length / 2], low / slow, s[^1], s.Count(x => x > 25), s.Count(x => x > 50));
    }
}

/// <summary>What a stopped frame log held.</summary>
/// <param name="Path">The CSV file.</param>
/// <param name="Present">Intervals between frames handed over (what reaches the screen).</param>
/// <param name="Start">Intervals between frame starts (when the camera was read).</param>
/// <param name="AfterIdle">Frames that followed an idle pause, left out of both summaries.</param>
public readonly record struct FrameLogResult(string Path, IntervalSummary Present, IntervalSummary Start, int AfterIdle)
{
    public string Words() => $"frame log {Path}: " + Present.Words() +
        (AfterIdle > 0 ? $"; {AfterIdle} frame(s) after an idle pause not counted" : "");
}

/// <summary>
/// A live view's frame times: a rolling two-second window for an on-screen readout, and a
/// per-frame CSV log. The view host feeds it (<see cref="Add"/>); it does no GL and no UI work, so
/// a host of any kind can use it. Call it from one thread.
/// </summary>
public sealed class FrameStats
{
    public const string CsvHeader =
        "frame,t_ms,interval_ms,start_interval_ms,render_ms,present_ms,gpu_ms,host,resumed,reason,reason_age_ms,queued_ms,input_events,gc0,gc1,gc2,azimuth,elevation,eye_x,eye_y,eye_z,fb_w,fb_h";

    private const double WindowMs = 2000;

    private readonly Queue<FrameSample> _recent = new();
    private StreamWriter? _log;
    private string? _logPath;
    private readonly List<double> _logIntervals = new(), _logStartIntervals = new();
    private int _logResumed;
    private double _logStart;

    /// <summary>Written to the log's <c>host</c> column: which host and pacing drew the frames.</summary>
    public string HostLabel { get; set; } = "";

    public double LastRenderMs { get; private set; }
    public double LastPresentMs { get; private set; } = -1;
    public double LastStartMs { get; private set; } = -1;
    public long Frames { get; private set; }
    public bool Logging => _log is not null;
    public string? LogPath => _logPath;

    /// <summary>The first frame after the view sat with nothing to draw for more than 100 ms: its
    /// interval is the pause, not a frame time, so summaries leave it out.</summary>
    public static bool AfterIdle(FrameSample s) => s.Resumed && s.IntervalMs - s.ReasonAgeMs > 100;

    public void Add(FrameSample s)
    {
        LastRenderMs = s.RenderMs;
        LastPresentMs = s.PresentMs;
        LastStartMs = s.StartMs;
        Frames++;
        _recent.Enqueue(s);
        while (_recent.Count > 0 && _recent.Peek().PresentMs < s.PresentMs - WindowMs) _recent.Dequeue();
        if (_log is null) return;
        if (AfterIdle(s)) _logResumed++;
        else if (s.IntervalMs > 0)
        {
            _logIntervals.Add(s.IntervalMs);
            _logStartIntervals.Add(s.StartIntervalMs);
        }
        _log.WriteLine(Csv(s, s.PresentMs - _logStart, HostLabel));
    }

    public static string Csv(FrameSample s, double t, string host)
    {
        var inv = CultureInfo.InvariantCulture;
        static string Opt(double v) => double.IsNaN(v) ? "" : v.ToString("0.000", CultureInfo.InvariantCulture);
        return string.Create(inv,
            $"{s.Frame},{t:0.000},{s.IntervalMs:0.000},{s.StartIntervalMs:0.000},{s.RenderMs:0.000},{Opt(s.PresentCallMs)},{Opt(s.GpuMs)},{host},{(s.Resumed ? 1 : 0)},{s.Reason},{s.ReasonAgeMs:0.000},{Opt(s.QueuedMs)},{s.InputEvents},{s.Gc0},{s.Gc1},{s.Gc2},{s.Azimuth:0.###},{s.Elevation:0.###},{s.EyeX:0.#},{s.EyeY:0.#},{s.EyeZ:0.#},{s.Width},{s.Height}");
    }

    /// <summary>The window's intervals summed up, or null when fewer than two frames are in it.</summary>
    public IntervalSummary? Recent(double nowMs)
    {
        var iv = _recent.Where(r => !AfterIdle(r) && r.IntervalMs > 0 && r.PresentMs >= nowMs - WindowMs).Select(r => r.IntervalMs).ToList();
        return iv.Count < 2 ? null : IntervalSummary.Of(iv);
    }

    /// <summary>The readout's words over the last two seconds; "idle" when no frame was drawn in
    /// the last half second (a view that draws on demand and has nothing to draw).</summary>
    public string Readout(double nowMs)
    {
        string tail = HostLabel.Length > 0 ? "  |  " + HostLabel : "";
        if (_recent.Count == 0 || nowMs - LastPresentMs > 500) return "idle (nothing to draw)" + tail;
        if (Recent(nowMs) is not { } sum) return "drawing..." + tail;
        return string.Create(CultureInfo.InvariantCulture,
            $"{1000 / sum.MedianMs:0} fps  {sum.MedianMs:0.0} ms  1% low {sum.Low1Ms:0.0} ms  worst {sum.WorstMs:0.0} ms  render {LastRenderMs:0.0} ms") + tail;
    }

    /// <summary>Start writing one CSV row per frame to <paramref name="path"/>; a log already
    /// running is stopped first.</summary>
    public void StartLog(string path, double nowMs)
    {
        if (_log is not null) StopLog();
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _log = new StreamWriter(full, append: false, new UTF8Encoding(false), 1 << 16);
        _log.WriteLine(CsvHeader);
        _logPath = full;
        _logIntervals.Clear();
        _logStartIntervals.Clear();
        _logResumed = 0;
        _logStart = nowMs;
    }

    /// <summary>Stop the log and return what it held; the rows are on disk when this returns, and
    /// the summary line is appended to a <c>.summary.txt</c> beside the CSV. Null when no log runs.</summary>
    public FrameLogResult? StopLog()
    {
        if (_log is null) return null;
        _log.Flush();
        _log.Dispose();
        _log = null;
        var result = new FrameLogResult(_logPath!, IntervalSummary.Of(_logIntervals), IntervalSummary.Of(_logStartIntervals), _logResumed);
        File.AppendAllText(Path.ChangeExtension(_logPath!, ".summary.txt"), result.Words() + Environment.NewLine);
        return result;
    }
}
