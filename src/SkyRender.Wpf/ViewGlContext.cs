using System.Runtime.InteropServices;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace SkyRender.Wpf;

/// <summary>
/// A GL context the host already has, for a <see cref="SkyRenderView"/> to draw with instead of
/// making its own (<see cref="SkyRenderView(ViewGlContext)"/>). The context belongs to a hidden
/// window of the host's; the view's child window takes that window's pixel format, so the context
/// is valid on both, and whatever the host built on it (meshes, textures, shaders) the view draws.
///
/// <para>The view never disposes the context. When its window goes, it leaves the context current
/// on the hidden window again, so the host's GL calls on the UI thread keep working.</para>
///
/// <para>The context must live on the UI thread the view is on, and OpenTK's GL bindings must
/// already be loaded: creating any OpenTK window in the process does that.</para>
/// </summary>
public sealed class ViewGlContext
{
    private readonly Action _restore;

    private ViewGlContext(IntPtr hiddenHwnd, IntPtr hglrc, Action restore)
    {
        if (hiddenHwnd == IntPtr.Zero) throw new ArgumentException("The context's window handle is zero.", nameof(hiddenHwnd));
        if (hglrc == IntPtr.Zero) throw new ArgumentException("The GL context handle is zero.", nameof(hglrc));
        HiddenWindow = hiddenHwnd;
        Hglrc = hglrc;
        _restore = restore;
    }

    /// <summary>The Win32 window the context was made for; the view copies its pixel format.</summary>
    public IntPtr HiddenWindow { get; }

    /// <summary>The WGL context (<c>HGLRC</c>).</summary>
    public IntPtr Hglrc { get; }

    /// <summary>An OpenTK window's context (a <see cref="NativeWindow"/> or a <c>GameWindow</c>). It
    /// is made current again through OpenTK, so GLFW's own note of the current context stays right.</summary>
    public static ViewGlContext From(NativeWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        IntPtr hwnd, hglrc;
        unsafe
        {
            hwnd = GLFW.GetWin32Window(window.WindowPtr);
            hglrc = GLFW.GetWGLContext(window.WindowPtr);
        }
        return new ViewGlContext(hwnd, hglrc, window.MakeCurrent);
    }

    /// <summary>A context known only by its handles: the hidden window it was made for and the
    /// <c>HGLRC</c>. It is made current again with <c>wglMakeCurrent</c> on that window's DC.</summary>
    public static ViewGlContext FromHandles(IntPtr hiddenHwnd, IntPtr hglrc)
    {
        return new ViewGlContext(hiddenHwnd, hglrc, () =>
        {
            // A GL window has a DC of its own (CS_OWNDC: GLFW's windows do), so GetDC hands back
            // that same DC and ReleaseDC does nothing to it.
            var dc = GetDC(hiddenHwnd);
            try
            {
                if (!wglMakeCurrent(dc, hglrc))
                    throw new InvalidOperationException($"The host's GL context could not be made current on its window (Win32 error {Marshal.GetLastWin32Error()}).");
            }
            finally { ReleaseDC(hiddenHwnd, dc); }
        });
    }

    /// <summary>Make the context current on its hidden window.</summary>
    internal void Restore() => _restore();

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("opengl32.dll", SetLastError = true)] private static extern bool wglMakeCurrent(IntPtr hdc, IntPtr hglrc);
}
