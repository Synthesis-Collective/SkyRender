using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Wpf;
using SkyRender.Viewing;

namespace SkyRender.Wpf;

/// <summary>
/// The view as an <c>OpenTK.Wpf.GLWpfControl</c>: the scene is drawn into a framebuffer shared
/// with Direct3D 9 (<c>WGL_NV_DX_interop</c>) and WPF composites it like any other element, so WPF
/// content can overlap it. Frames are drawn on WPF's own frame tick
/// (<c>CompositionTarget.Rendering</c>); there is no buffer swap and no pacer.
/// </summary>
internal sealed class WpfTextureBackend : IViewBackend
{
    private readonly SkyRenderView _view;
    private readonly GLWpfControl _control = new() { Focusable = true };
    private readonly int _samples;
    private readonly int[] _viewport = new int[4];
    private bool _started, _continuous, _requested = true;
    private TimeSpan _lastTick = TimeSpan.FromSeconds(-1);

    public WpfTextureBackend(SkyRenderView view, int samples, bool continuous)
    {
        _view = view;
        _samples = samples;
        _continuous = continuous;
        _control.Render += OnRender;
        _control.Loaded += (_, _) => Start();
        _control.MouseDown += (_, e) =>
        {
            _control.Focus();
            var p = e.GetPosition(_control);
            _control.CaptureMouse();
            _view.OnPointerDown(Button(e.ChangedButton), p.X, p.Y);
        };
        _control.MouseMove += (_, e) =>
        {
            var p = e.GetPosition(_control);
            _view.OnPointerMove(p.X, p.Y);
        };
        _control.MouseUp += (_, e) =>
        {
            var p = e.GetPosition(_control);
            _view.OnPointerUp(Button(e.ChangedButton), p.X, p.Y);
            if (e.LeftButton == MouseButtonState.Released && e.MiddleButton == MouseButtonState.Released && e.RightButton == MouseButtonState.Released)
                _control.ReleaseMouseCapture();
        };
        _control.MouseWheel += (_, e) => _view.OnWheel(e.Delta);
        _control.KeyDown += (_, e) => _view.OnKey(e.Key == Key.System ? e.SystemKey : e.Key, down: true, e.IsRepeat);
        _control.KeyUp += (_, e) => _view.OnKey(e.Key == Key.System ? e.SystemKey : e.Key, down: false, repeat: false);
        _control.LostKeyboardFocus += (_, _) => _view.OnFocusLost();
    }

    public FrameworkElement Element => _control;
    public string Label => "wpf";
    public string? Note => null;

    private static ViewButton Button(MouseButton b) => b switch
    {
        MouseButton.Right => ViewButton.Right,
        MouseButton.Middle => ViewButton.Middle,
        _ => ViewButton.Left,
    };

    private void Start()
    {
        if (_started) return;
        _started = true;
        _control.Start(new GLWpfControlSettings { MajorVersion = 3, MinorVersion = 3, RenderContinuously = _continuous, Samples = _samples });
        // GLWpfControl 4.x hooks its frame tick up in IsVisibleChanged, which never fires for a
        // control that is already visible when Start() runs: without this toggle no frame is drawn
        // until something else forces a redraw.
        _control.Visibility = Visibility.Collapsed;
        _control.Visibility = Visibility.Visible;
        CompositionTarget.Rendering += OnTick;
    }

    /// <summary>On demand: redraw on WPF's frame tick only while something wants a frame. (With
    /// continuous rendering the control invalidates itself on every tick.)</summary>
    private void OnTick(object? sender, EventArgs e)
    {
        if (_control.RenderContinuously) return;
        // The event can be raised twice for one WPF frame; draw once.
        var time = (e as RenderingEventArgs)?.RenderingTime ?? TimeSpan.Zero;
        if (time == _lastTick) return;
        _lastTick = time;
        if (_requested || _view.WantsFrame)
        {
            _requested = false;
            _control.InvalidateVisual();
        }
    }

    private void OnRender(TimeSpan delta)
    {
        // The control has bound its framebuffer and set the viewport to it. (Its FrameBufferWidth
        // and FrameBufferHeight read 0 in 4.3.3 once the shared image exists, so the size comes from GL.)
        GL.GetInteger(GetPName.Viewport, _viewport);
        _view.FrameDue();
        _view.RunFrame(Math.Max(1, _viewport[2]), Math.Max(1, _viewport[3]), present: null, queuedMs: double.NaN);
        _view.FrameDone();
    }

    public void Request() => _requested = true;

    public void SetContinuous(bool continuous)
    {
        _continuous = continuous;
        if (_started) _control.RenderContinuously = continuous;
    }

    public bool MakeCurrent()
    {
        if (_control.Context is not { } context) return false;
        context.MakeCurrent();
        return true;
    }

    public void FocusView() => _control.Focus();

    public (IntPtr Hwnd, int CentreX, int CentreY) InputOrigin()
    {
        if (PresentationSource.FromVisual(_control) is not HwndSource source) return (IntPtr.Zero, 0, 0);
        var centre = _control.TransformToAncestor(source.RootVisual).Transform(new Point(_control.ActualWidth / 2, _control.ActualHeight / 2));
        var toDevice = source.CompositionTarget.TransformToDevice;
        return (source.Handle, (int)(centre.X * toDevice.M11), (int)(centre.Y * toDevice.M22));
    }

    public void Dispose()
    {
        CompositionTarget.Rendering -= OnTick;
        if (!_started) return;
        if (MakeCurrent()) _view.RaiseGlClosing();
        _control.Dispose();
    }
}
