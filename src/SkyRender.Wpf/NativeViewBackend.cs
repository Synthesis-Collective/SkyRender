using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SkyRender.Viewing;

namespace SkyRender.Wpf;

/// <summary>What a view host backend does for <see cref="SkyRenderView"/>.</summary>
internal interface IViewBackend : IDisposable
{
    FrameworkElement Element { get; }
    string Label { get; }
    /// <summary>Ask for a frame.</summary>
    void Request();
    void SetContinuous(bool continuous);
    bool MakeCurrent();
    void FocusView();
    /// <summary>The window scripted input is posted to, and the view's centre in its client area (device pixels).</summary>
    (IntPtr Hwnd, int CentreX, int CentreY) InputOrigin();
    string? Note { get; }
}

/// <summary>
/// The view as a plain Win32 child window inside the WPF window (an <see cref="HwndHost"/>), drawn
/// with a GL context of its own and presented with <c>SwapBuffers</c>, one frame per desktop
/// composition (<see cref="FramePacer"/>).
///
/// <para>The context belongs to a hidden window: by default a GLFW window of the view's own,
/// created as OpenTK creates any context, so the GL bindings load as they do everywhere else in
/// SkyRender; or the host's (<see cref="ViewGlContext"/>), which the view borrows and never
/// disposes. The child window takes the hidden window's pixel format, which makes the context
/// valid on it.</para>
/// </summary>
internal sealed class NativeViewBackend : HwndHost, IViewBackend
{
    private readonly SkyRenderView _view;
    private readonly NativeWindow? _ownWindow;          // null when the host's context is borrowed
    private readonly ViewGlContext _context;
    private readonly IntPtr _contextHwnd, _hglrc;
    private IntPtr _hwnd, _hdc;
    private FramePacer? _pacer;
    private bool _swapIntervalSet;
    private int _buttonsDown;

    public NativeViewBackend(SkyRenderView view, int samples)
    {
        _view = view;
        var settings = new NativeWindowSettings
        {
            StartVisible = false,
            StartFocused = false,
            NumberOfSamples = samples,
            APIVersion = new Version(3, 3),
            Profile = ContextProfile.Core,
            Flags = ContextFlags.Offscreen,
            WindowBorder = WindowBorder.Hidden,
        };
        _ownWindow = new NativeWindow(settings);         // makes its context current and loads the GL bindings
        _context = ViewGlContext.From(_ownWindow);
        _contextHwnd = _context.HiddenWindow;
        _hglrc = _context.Hglrc;
    }

    /// <summary>A view on the host's context: its pixel format, and the scene the host built on it.</summary>
    public NativeViewBackend(SkyRenderView view, ViewGlContext context)
    {
        _view = view;
        _context = context;
        _contextHwnd = context.HiddenWindow;
        _hglrc = context.Hglrc;
    }

    /// <summary>The pacer lives as long as the child window does (WPF builds a new window when the
    /// view moves to another top-level window).</summary>
    private void StartPacer()
    {
        // SKYRENDER_VIEW_PACER=compositor|clock keeps the pacer to one source, for diagnosis.
        PacerSource? forced = Environment.GetEnvironmentVariable("SKYRENDER_VIEW_PACER")?.ToLowerInvariant() switch
        {
            "compositor" => PacerSource.Compositor,
            "clock" => PacerSource.Clock,
            _ => null,
        };
        _pacer = new FramePacer(Dispatcher, PacedFrame, () => SkyRenderView.NowMs, forced);
        _swapIntervalSet = false;
        _pacer.Request();
    }

    public FrameworkElement Element => this;
    public string Label => _pacer?.Source == PacerSource.Clock ? "native/clock" : "native/compositor";
    public string? Note => _pacer?.Source == PacerSource.Clock
        ? "paced by the view's own clock: the desktop compositor is not keeping the display's rate"
        : null;

    public void Request() => _pacer?.Request();
    public void SetContinuous(bool continuous) { if (continuous) Request(); }
    public void FocusView() { if (_hwnd != IntPtr.Zero) SetFocus(_hwnd); }

    public (IntPtr Hwnd, int CentreX, int CentreY) InputOrigin()
    {
        var (w, h) = ClientSize();
        return (_hwnd, w / 2, h / 2);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;
        _hwnd = CreateWindowEx(0, "static", "", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
            0, 0, 64, 64, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"The view's child window could not be created (Win32 error {Marshal.GetLastWin32Error()}).");
        // SS_NOTIFY, so a static control receives mouse messages.
        SetWindowLongPtr(_hwnd, -16, (IntPtr)(GetWindowLongPtr(_hwnd, -16).ToInt64() | 0x0100));
        _hdc = GetDC(_hwnd);
        var srcDc = GetDC(_contextHwnd);
        int pf = GetPixelFormat(srcDc);
        var pfd = new PIXELFORMATDESCRIPTOR { nSize = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(), nVersion = 1 };
        DescribePixelFormat(srcDc, pf, pfd.nSize, ref pfd);
        ReleaseDC(_contextHwnd, srcDc);
        if (pf == 0 || !SetPixelFormat(_hdc, pf, ref pfd))
            throw new InvalidOperationException($"The view could not take its GL context's pixel format {pf} (Win32 error {Marshal.GetLastWin32Error()}).");
        StartPacer();
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Teardown();
        DestroyWindow(hwnd.Handle);
    }

    /// <summary>The child window is about to go (WPF destroys it, or its parent takes it along).
    /// GL objects are freed while the context is still current on a living window; after that the
    /// context goes back to its own hidden window, so later GL calls on this thread stay valid.</summary>
    private void Teardown()
    {
        if (_hwnd == IntPtr.Zero) return;
        _pacer?.Dispose();
        _pacer = null;
        if (MakeCurrent()) _view.RaiseGlClosing();
        _context.Restore();
        if (_hdc != IntPtr.Zero) ReleaseDC(_hwnd, _hdc);
        _hdc = IntPtr.Zero;
        _hwnd = IntPtr.Zero;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);                // destroys the child window (DestroyWindowCore)
        if (disposing)
        {
            _pacer?.Dispose();
            _ownWindow?.Dispose();              // a borrowed context stays the host's
        }
    }

    /// <summary>Make the view's context current on its window. False when the window does not exist yet.</summary>
    public bool MakeCurrent()
    {
        if (_hdc == IntPtr.Zero) return false;
        if (!wglMakeCurrent(_hdc, _hglrc))
            throw new InvalidOperationException($"The view's GL context could not be made current (Win32 error {Marshal.GetLastWin32Error()}).");
        return true;
    }

    private (int W, int H) ClientSize()
    {
        if (_hwnd == IntPtr.Zero) return (1, 1);
        GetClientRect(_hwnd, out var r);
        return (Math.Max(r.Right - r.Left, 1), Math.Max(r.Bottom - r.Top, 1));
    }

    /// <summary>The pacer's frame: drawn while something wants one; otherwise the loop parks until
    /// <see cref="SkyRenderView.Invalidate"/> asks again.</summary>
    private void PacedFrame()
    {
        if (_hwnd == IntPtr.Zero || !_view.FrameDue()) return;
        if (!MakeCurrent()) return;
        if (!_swapIntervalSet)
        {
            // The pacer times the frames; a swap that also waited would halve the rate where it does block.
            _swapIntervalSet = true;
            var p = wglGetProcAddress("wglSwapIntervalEXT");
            if (p != IntPtr.Zero) Marshal.GetDelegateForFunctionPointer<SwapIntervalFn>(p)(0);
        }
        var (w, h) = ClientSize();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.Viewport(0, 0, w, h);
        double queued = _pacer is { } pc ? SkyRenderView.NowMs - pc.PostedAtMs : double.NaN;
        _view.RunFrame(w, h, () => SwapBuffers(_hdc), queued);
        if (_view.FrameDone()) _pacer?.Request();
    }

    private delegate bool SwapIntervalFn(int interval);

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DESTROY = 0x0002, WM_SIZE = 0x0005, WM_KILLFOCUS = 0x0008, WM_PAINT = 0x000F, WM_GETDLGCODE = 0x0087,
                  WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_MOUSEMOVE = 0x0200, WM_MOUSEWHEEL = 0x020A;
        switch (msg)
        {
            case WM_MOUSEMOVE:
            {
                var (x, y) = Dips(lParam);
                _view.OnPointerMove(x, y);
                break;
            }
            case >= 0x0201 and <= 0x0209:
            {
                // 0x201..0x209: left, right, middle x (down, up, double-click)
                int index = msg - 0x0201;
                var button = (index / 3) switch { 0 => ViewButton.Left, 1 => ViewButton.Right, _ => ViewButton.Middle };
                var (x, y) = Dips(lParam);
                if (index % 3 == 1)
                {
                    _view.OnPointerUp(button, x, y);
                    if (--_buttonsDown <= 0) { _buttonsDown = 0; ReleaseCapture(); }
                }
                else
                {
                    SetFocus(_hwnd);
                    if (_buttonsDown++ == 0) SetCapture(_hwnd);
                    _view.OnPointerDown(button, x, y);
                }
                break;
            }
            case WM_MOUSEWHEEL:
                _view.OnWheel((short)((wParam.ToInt64() >> 16) & 0xFFFF));
                break;
            case WM_KEYDOWN:
                _view.OnKey(KeyInterop.KeyFromVirtualKey((int)wParam.ToInt64()), down: true, repeat: (lParam.ToInt64() & (1L << 30)) != 0);
                break;
            case WM_KEYUP:
                _view.OnKey(KeyInterop.KeyFromVirtualKey((int)wParam.ToInt64()), down: false, repeat: false);
                break;
            case WM_KILLFOCUS:
                _buttonsDown = 0;
                _view.OnFocusLost();
                break;
            case WM_DESTROY:
                Teardown();
                break;
            case WM_PAINT:
                _view.Invalidate("paint");
                break;
            case WM_SIZE:
                _view.Invalidate("size");
                break;
            case WM_GETDLGCODE:
                handled = true;
                return (IntPtr)0x0004;          // DLGC_WANTALLKEYS: the view takes the arrow, tab and letter keys
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    /// <summary>A mouse message's position in WPF units (device pixels over the DPI scale), so the
    /// controls feel the same in both hosts and at any scaling.</summary>
    private (double X, double Y) Dips(IntPtr lParam)
    {
        long l = lParam.ToInt64();
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        return ((short)(l & 0xFFFF) / dpi.DpiScaleX, (short)((l >> 16) & 0xFFFF) / dpi.DpiScaleY);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize, nVersion; public uint dwFlags; public byte iPixelType, cColorBits, cRedBits, cRedShift, cGreenBits, cGreenShift,
        cBlueBits, cBlueShift, cAlphaBits, cAlphaShift, cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits,
        cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved; public uint dwLayerMask, dwVisibleMask, dwDamageMask;
    }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int GetPixelFormat(IntPtr hdc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int DescribePixelFormat(IntPtr hdc, int pf, uint size, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool SetPixelFormat(IntPtr hdc, int pf, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll")] private static extern bool SwapBuffers(IntPtr hdc);
    [DllImport("opengl32.dll", SetLastError = true)] private static extern bool wglMakeCurrent(IntPtr hdc, IntPtr hglrc);
    [DllImport("opengl32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr wglGetProcAddress(string name);
}
