using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SkyRender.Viewing;

namespace SkyRender.Wpf;

/// <summary>How a <see cref="SkyRenderView"/> puts its frames on screen.</summary>
public enum ViewHostMode
{
    /// <summary>A native child window inside the WPF window, presented with <c>SwapBuffers</c> and
    /// paced to the desktop compositor. WPF content cannot overlap it.</summary>
    NativeWindow,
    /// <summary>A texture shared with WPF (<c>OpenTK.GLWpfControl</c>, <c>WGL_NV_DX_interop</c>),
    /// drawn on WPF's frame tick. WPF content can overlap it.</summary>
    WpfTexture,
}

/// <summary>When a <see cref="SkyRenderView"/> draws.</summary>
public enum ViewRenderMode
{
    /// <summary>Only while something changed: input, the camera, a held fly key, the view's size, or
    /// <see cref="SkyRenderView.Invalidate"/>. A still view costs nothing.</summary>
    OnDemand,
    /// <summary>At every display refresh.</summary>
    Continuous,
}

/// <summary>One frame to draw: the framebuffer's size in pixels, the time since the previous
/// frame began, and the frame's number.</summary>
public readonly record struct ViewFrame(int Width, int Height, double DeltaSeconds, long Number);

/// <summary>What kind of input a <see cref="ViewInput"/> is.</summary>
public enum ViewInputKind { PointerDown, PointerMove, PointerUp, Wheel, KeyDown, KeyUp, FocusLost }

/// <summary>An input event of the view, the same from either host. Positions are in WPF units
/// from the view's top-left corner.</summary>
public readonly record struct ViewInput(ViewInputKind Kind, double X, double Y, ViewButton Button, Key Key, bool Repeat, int WheelDelta);

/// <summary>
/// A live SkyRender view for a WPF window. Put it in the window, draw in <see cref="RenderFrame"/>
/// with <see cref="Camera"/>, and it paces the frames, moves the camera from the mouse and the
/// fly keys by the clock, and measures itself (<see cref="Stats"/>, <see cref="ShowReadout"/>,
/// <see cref="StartFrameLog"/>).
///
/// <code>
/// var view = new SkyRenderView();                       // the default host
/// GlRenderer? renderer = null;
/// view.GlReady += () => { renderer = new GlRenderer(); renderer.Initialize(ModuleResourceLocator.ShaderDirectory); };
/// view.RenderFrame += f => renderer!.Render(view.Camera, f.Width, f.Height);
/// view.GlClosing += () => renderer?.Dispose();
/// </code>
///
/// <para>All three events run on the UI thread with the view's GL context current. Create the
/// view on the application's main thread (its GL context comes from GLFW, which ties itself to
/// the first thread that uses it).</para>
/// </summary>
public class SkyRenderView : Decorator, IDisposable
{
    /// <summary>The host a view uses when none is named: the one that measured better (CHANGELOG 4.0.0).</summary>
    public static ViewHostMode DefaultMode => ViewHostMode.NativeWindow;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    /// <summary>The clock every view's frame times are on, in milliseconds.</summary>
    public static double NowMs => Clock.Elapsed.TotalMilliseconds;

    private readonly IViewBackend _backend;
    private readonly TextBlock _readout;
    private readonly GpuTimer _gpu = new();
    private DispatcherTimer? _readoutTimer;
    private OrbitCamera _camera;
    private ViewRenderMode _renderMode = ViewRenderMode.OnDemand;
    private bool _dirty = true, _parked, _resumed, _drewOnce, _ready, _disposed;
    private string? _reason;
    private double _reasonAt, _lastStart = -1;
    private int _inputs;
    private long _frame;

    public SkyRenderView() : this(DefaultMode) { }

    /// <param name="mode">The host. <see cref="DefaultMode"/> unless WPF content must overlap the
    /// view (then <see cref="ViewHostMode.WpfTexture"/>).</param>
    /// <param name="samples">Multisample anti-aliasing samples of the view's framebuffer; 0 for none.</param>
    public SkyRenderView(ViewHostMode mode, int samples = 0)
        : this(mode, view => mode == ViewHostMode.NativeWindow
            ? new NativeViewBackend(view, samples)
            : new WpfTextureBackend(view, samples, continuous: false))
    { }

    /// <summary>
    /// A view that draws with the host's own GL context instead of making one, so a scene the host
    /// built on that context before the view existed shows in it. Always the
    /// <see cref="ViewHostMode.NativeWindow"/> host; multisampling is whatever the context's pixel
    /// format has. The view never disposes the context and leaves it current on its hidden window
    /// when the view's window goes.
    ///
    /// <para><see cref="GlReady"/> and <see cref="GlClosing"/> are still raised with the context
    /// current; a host whose GL objects outlive the view leaves them alone in
    /// <see cref="GlClosing"/>.</para>
    /// </summary>
    public SkyRenderView(ViewGlContext context)
        : this(ViewHostMode.NativeWindow, view => new NativeViewBackend(view, context ?? throw new ArgumentNullException(nameof(context))))
    {
        HostContext = context;
    }

    private SkyRenderView(ViewHostMode mode, Func<SkyRenderView, IViewBackend> makeBackend)
    {
        Mode = mode;
        _camera = new OrbitCamera();
        _camera.ViewChanged += OnCameraChanged;
        Controls = new ViewCameraControls(_camera, () => NowMs / 1000.0);
        _backend = makeBackend(this);
        UpdateHostLabel();

        _readout = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"), FontSize = 12, Padding = new Thickness(6, 2, 6, 2),
            Foreground = Brushes.Gainsboro, Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)),
            TextTrimming = TextTrimming.CharacterEllipsis, Visibility = Visibility.Collapsed,
            ToolTip = "The view's frame rate over the last two seconds: frames a second, the median frame time, the mean of the slowest 1%, the worst, and the last frame's render call.",
        };
        // The readout is a strip above the view, not over it: nothing WPF draws can sit on top of
        // a native child window.
        var panel = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_readout, Dock.Top);
        panel.Children.Add(_readout);
        panel.Children.Add(_backend.Element);
        Child = panel;
        Unloaded += (_, _) => _readoutTimer?.Stop();
        Loaded += (_, _) => { if (ShowReadout) _readoutTimer?.Start(); };
    }

    /// <summary>Which host this view uses.</summary>
    public ViewHostMode Mode { get; }

    /// <summary>The host's context the view draws with; null when the view made its own.</summary>
    public ViewGlContext? HostContext { get; }

    /// <summary>The camera the built-in controls move. Replace it to bring your own.</summary>
    public OrbitCamera Camera
    {
        get => _camera;
        set
        {
            _camera.ViewChanged -= OnCameraChanged;
            _camera = value;
            _camera.ViewChanged += OnCameraChanged;
            Controls.Camera = value;
            Invalidate("camera");
        }
    }

    /// <summary>The size of the 3D view itself, without the readout strip, in WPF units.</summary>
    public Size ViewSize => new(_backend.Element.ActualWidth, _backend.Element.ActualHeight);

    /// <summary>Mouse and fly controls: speeds, and the fly keys' state.</summary>
    public ViewCameraControls Controls { get; }

    /// <summary>False to leave the camera alone and handle <see cref="Input"/> yourself.</summary>
    public bool ControlsEnabled { get; set; } = true;

    /// <summary>The view's frame times.</summary>
    public FrameStats Stats { get; } = new();

    /// <summary>Draw only on change (the default), or at every refresh.</summary>
    public ViewRenderMode RenderMode
    {
        get => _renderMode;
        set
        {
            _renderMode = value;
            _backendLabel = null;
            UpdateHostLabel();
            _backend.SetContinuous(value == ViewRenderMode.Continuous);
            Invalidate("mode");
        }
    }

    /// <summary>Asked after each frame: return true while the scene itself still needs frames (an
    /// upload in progress, an animation), so an on-demand view keeps drawing.</summary>
    public Func<bool>? KeepDrawing { get; set; }

    /// <summary>Raised before the first frame: create the renderer and load the scene's GL objects here.</summary>
    public event Action? GlReady;

    /// <summary>Raised for each frame. The view's framebuffer is bound and the viewport is set.</summary>
    public event Action<ViewFrame>? RenderFrame;

    /// <summary>Raised when the view's window goes away, while its GL context is still usable: free GL objects here.</summary>
    public event Action? GlClosing;

    /// <summary>Raised after a frame has been handed over and counted in <see cref="Stats"/>.</summary>
    public event Action? FrameDrawn;

    /// <summary>Every mouse and key event of the view, after the built-in controls have seen it.</summary>
    public event Action<ViewInput>? Input;

    /// <summary>Shows a one-line frame-rate readout above the view. It is refreshed four times a
    /// second from a timer and never asks for a frame.</summary>
    public bool ShowReadout
    {
        get => _readout.Visibility == Visibility.Visible;
        set
        {
            _readout.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (!value) { _readoutTimer?.Stop(); return; }
            _readoutTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal,
                (_, _) => _readout.Text = ReadoutText, Dispatcher);
            _readoutTimer.Start();
        }
    }

    /// <summary>The readout's words, for a host that shows them somewhere of its own.</summary>
    public string ReadoutText => Stats.Readout(NowMs) + (_backend.Note is { } note ? "  |  " + note : "");

    /// <summary>Something on screen changed: draw a frame. <paramref name="reason"/> names the
    /// change in the frame log.</summary>
    public void Invalidate(string reason = "other")
    {
        _dirty = true;
        if (_reason is null)
        {
            _reason = reason;
            _reasonAt = NowMs;
        }
        if (_parked)
        {
            _parked = false;
            _resumed = true;
        }
        _backend.Request();
    }

    /// <summary>Makes the view's GL context current, for GL work outside a frame. False before the
    /// view has a window.</summary>
    public bool MakeCurrent() => _backend.MakeCurrent();

    /// <summary>Gives the view the keyboard focus (the fly keys go to the focused view).</summary>
    public void FocusView() => _backend.FocusView();

    /// <summary>Start a per-frame CSV log (<see cref="FrameStats.CsvHeader"/> names the columns).</summary>
    public void StartFrameLog(string path) => Stats.StartLog(path, NowMs);

    /// <summary>Stop the log; the result carries the summary. Null when none was running.</summary>
    public FrameLogResult? StopFrameLog() => Stats.StopLog();

    /// <summary>
    /// Plays a scripted motion through the view's real input path: the events are posted as window
    /// messages on their timetable from a background thread. For measuring a view with no hand on
    /// the mouse; the window should be in the foreground, and the mouse pointer is moved onto the
    /// view. <paramref name="done"/> runs on the UI
    /// thread with a line saying how the posting went.
    /// </summary>
    public void PlayMotion(ViewMotion motion, Action<string>? done = null)
    {
        var (hwnd, cx, cy) = _backend.InputOrigin();
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("The view has no window yet.");
        _backend.FocusView();
        // The pointer itself is parked on the view's centre first: WPF takes a posted mouse press only
        // while the real pointer is over the window, and both hosts should start from the same state.
        MotionPlayer.ParkPointer(hwnd, cx, cy);
        double delayMs = (SystemParameters.KeyboardDelay + 1) * 250.0, hz = 2.5 + SystemParameters.KeyboardSpeed * (27.5 / 31.0);
        var messages = motion.Events(delayMs, hz).Select(e => MotionPlayer.ToMessage(e, cx, cy)).ToList();
        MotionPlayer.Post(hwnd, messages, said => Dispatcher.BeginInvoke(() => done?.Invoke(said)));
    }

    // ---- the frame, shared by both hosts ----

    internal bool WantsFrame => _renderMode == ViewRenderMode.Continuous || _dirty || Controls.IsFlying || (KeepDrawing?.Invoke() ?? false);

    /// <summary>Called by the host when a frame could be drawn. False parks an on-demand view.</summary>
    internal bool FrameDue()
    {
        if (_disposed) return false;
        if (WantsFrame) return true;
        _parked = true;
        return false;
    }

    /// <summary>Called by the host after a frame. True when another is wanted straight away.</summary>
    internal bool FrameDone()
    {
        if (_disposed) return false;
        if (WantsFrame) return true;
        _parked = true;
        return false;
    }

    private string? _backendLabel;

    /// <summary>The log's host column: the host, what paces it, and the render mode.</summary>
    private void UpdateHostLabel()
    {
        string label = _backend.Label;
        if (ReferenceEquals(label, _backendLabel)) return;
        _backendLabel = label;
        Stats.HostLabel = label + (_renderMode == ViewRenderMode.Continuous ? " continuous" : " on-demand");
    }

    internal void RunFrame(int width, int height, Action? present, double queuedMs)
    {
        double t0 = NowMs;
        UpdateHostLabel();
        string reason = _reason ?? (Controls.IsFlying ? "fly" : _renderMode == ViewRenderMode.Continuous ? "continuous" : "none");
        double reasonAge = _reason is null ? 0 : t0 - _reasonAt;
        int inputs = _inputs;
        bool resumed = _resumed || !_drewOnce;
        _dirty = false;
        _reason = null;
        _inputs = 0;
        _resumed = false;
        _drewOnce = true;

        if (!_ready)
        {
            _ready = true;
            GlReady?.Invoke();
        }
        if (ControlsEnabled) Controls.Advance();
        _gpu.Begin();
        RenderFrame?.Invoke(new ViewFrame(width, height, _lastStart < 0 ? 0 : (t0 - _lastStart) / 1000.0, _frame));
        double gpuMs = _gpu.End();
        double t1 = NowMs;
        present?.Invoke();
        double t2 = NowMs;

        double previous = Stats.LastPresentMs;
        var eye = _camera.GetEyePosition();
        Stats.Add(new FrameSample(_frame, t0, t2, previous < 0 ? 0 : t2 - previous, _lastStart < 0 ? 0 : t0 - _lastStart, t1 - t0,
            present is null ? double.NaN : t2 - t1, gpuMs, resumed || previous < 0, reason, reasonAge, queuedMs, inputs,
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
            _camera.Azimuth, _camera.Elevation, eye.X, eye.Y, eye.Z, width, height));
        _lastStart = t0;
        _frame++;
        FrameDrawn?.Invoke();
    }

    /// <summary>The view's window is going: GL objects are freed now. If the view gets a new window
    /// (it moved to another top-level window), <see cref="GlReady"/> is raised again before its next frame.</summary>
    internal void RaiseGlClosing()
    {
        if (!_ready) return;
        _ready = false;
        GlClosing?.Invoke();
    }

    private void OnCameraChanged() => Invalidate("camera");

    // ---- input, from either host, in WPF units ----

    internal void OnPointerDown(ViewButton button, double x, double y)
    {
        Count("input");
        if (ControlsEnabled) Controls.PointerDown(button, (float)x, (float)y);
        Input?.Invoke(new ViewInput(ViewInputKind.PointerDown, x, y, button, Key.None, false, 0));
    }

    internal void OnPointerMove(double x, double y)
    {
        Count("input");
        if (ControlsEnabled) Controls.PointerMove((float)x, (float)y, (float)_backend.Element.ActualHeight);
        Input?.Invoke(new ViewInput(ViewInputKind.PointerMove, x, y, default, Key.None, false, 0));
    }

    internal void OnPointerUp(ViewButton button, double x, double y)
    {
        Count("input");
        if (ControlsEnabled) Controls.PointerUp(button);
        Input?.Invoke(new ViewInput(ViewInputKind.PointerUp, x, y, button, Key.None, false, 0));
    }

    internal void OnWheel(int delta)
    {
        Count("input");
        if (ControlsEnabled) Controls.Wheel(delta);
        Input?.Invoke(new ViewInput(ViewInputKind.Wheel, 0, 0, default, Key.None, false, delta));
    }

    internal void OnKey(Key key, bool down, bool repeat)
    {
        Count("key");
        if (ControlsEnabled && FlyKeyOf(key) is { } fly)
        {
            if (down) Controls.KeyDown(fly);
            else Controls.KeyUp(fly);
        }
        Input?.Invoke(new ViewInput(down ? ViewInputKind.KeyDown : ViewInputKind.KeyUp, 0, 0, default, key, repeat, 0));
    }

    internal void OnFocusLost()
    {
        Controls.ReleaseAll();
        Input?.Invoke(new ViewInput(ViewInputKind.FocusLost, 0, 0, default, Key.None, false, 0));
    }

    private void Count(string reason)
    {
        _inputs++;
        Invalidate(reason);
    }

    /// <summary>W A S D along the view and sideways, E / Q up and down, Shift faster.</summary>
    private static FlyKey? FlyKeyOf(Key key) => key switch
    {
        Key.W => FlyKey.Forward, Key.S => FlyKey.Back, Key.A => FlyKey.Left, Key.D => FlyKey.Right,
        Key.E => FlyKey.Up, Key.Q => FlyKey.Down, Key.LeftShift or Key.RightShift => FlyKey.Fast,
        _ => null,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _readoutTimer?.Stop();
        Stats.StopLog();
        _camera.ViewChanged -= OnCameraChanged;
        _backend.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Posts a motion's events to a window as the messages a mouse and a keyboard produce.</summary>
internal static class MotionPlayer
{
    internal readonly record struct TimedMessage(double AtMs, int Msg, IntPtr WParam, IntPtr LParam);

    internal static TimedMessage ToMessage(MotionEvent e, int centreX, int centreY)
    {
        const int MK_RBUTTON = 0x2;
        static IntPtr Point(int x, int y) => (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF));
        // WM_KEYDOWN / WM_KEYUP lParam: repeat count 1, bit 30 the key's previous state, bit 31 set on a release.
        static IntPtr KeyParam(bool wasDown, bool up) => (IntPtr)(1L | (wasDown ? 1L << 30 : 0) | (up ? 1L << 31 : 0));
        int vk = e.Key switch
        {
            FlyKey.Forward => 'W', FlyKey.Back => 'S', FlyKey.Left => 'A', FlyKey.Right => 'D',
            FlyKey.Up => 'E', FlyKey.Down => 'Q', _ => 0x10,
        };
        var at = Point(centreX + e.X, centreY + e.Y);
        return e.Kind switch
        {
            MotionEventKind.RightDown => new(e.AtMs, 0x0204, (IntPtr)MK_RBUTTON, at),
            MotionEventKind.Move => new(e.AtMs, 0x0200, (IntPtr)MK_RBUTTON, at),
            MotionEventKind.RightUp => new(e.AtMs, 0x0205, IntPtr.Zero, at),
            MotionEventKind.KeyDown => new(e.AtMs, 0x0100, (IntPtr)vk, KeyParam(e.Repeat, false)),
            _ => new(e.AtMs, 0x0101, (IntPtr)vk, KeyParam(true, true)),
        };
    }

    /// <summary>Post the messages on their timetable from a background thread (a spin on the clock,
    /// so the process's timer resolution is left as it is).</summary>
    internal static void Post(IntPtr hwnd, IReadOnlyList<TimedMessage> messages, Action<string> done)
    {
        var thread = new Thread(() =>
        {
            var watch = Stopwatch.StartNew();
            int failed = 0;
            double late = 0;
            foreach (var m in messages)
            {
                while (watch.Elapsed.TotalMilliseconds < m.AtMs) Thread.Yield();
                late = Math.Max(late, watch.Elapsed.TotalMilliseconds - m.AtMs);
                if (!PostMessage(hwnd, m.Msg, m.WParam, m.LParam)) failed++;
            }
            done(FormattableString.Invariant($"{messages.Count} messages posted over {watch.Elapsed.TotalSeconds:0.00} s, latest {late:0.0} ms behind its time") +
                 (failed > 0 ? $", {failed} could not be posted" : ""));
        }) { IsBackground = true, Name = "SkyRender view motion" };
        thread.Start();
    }

    internal static void ParkPointer(IntPtr hwnd, int clientX, int clientY)
    {
        var point = new POINT { X = clientX, Y = clientY };
        if (ClientToScreen(hwnd, ref point)) SetCursorPos(point.X, point.Y);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
}
