using System.Windows;
using System.Windows.Threading;
using FluentAssertions;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SkyRender.Wpf;
using Xunit;
using Window = System.Windows.Window;

namespace SkyRender.Tests;

/// <summary>
/// A <see cref="SkyRenderView"/> on the host's own GL context (<see cref="ViewGlContext"/>): what
/// the host built on the context before the view existed draws in the view, and the context is
/// the host's again, alive and current on its hidden window, once the view has gone.
///
/// <para>Needs a GPU and a desktop: a real GL 3.3 context and a window on screen (the view's
/// framebuffer is read back, and pixels of a window off screen are undefined). The release
/// workflow filters it out (<c>Needs!=GL</c>); run it on a workstation.</para>
/// </summary>
[Trait("Needs", "GL")]
[Collection(GlWindowCollection.Name)]
public class SharedContextViewTests
{
    private static readonly byte[] Colour = [200, 40, 90, 255];

    [Fact]
    public void A_texture_built_on_the_host_context_draws_in_the_view_and_the_context_outlives_it()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Run(); }
            catch (Exception e) { failure = e; }
        }) { IsBackground = true, Name = "shared-context view test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue("the test window should close within 30 s");
        if (failure is not null) throw new Exception("The shared-context view test failed on its UI thread.", failure);
    }

    private static void Run()
    {
        // xunit runs no test on the process's main thread. On Windows GLFW needs only that one thread
        // makes all its calls, and this test is the suite's only GLFW user.
        GLFWProvider.CheckForMainThread = false;
        // The host: a hidden GLFW window whose context holds the scene, built before any view exists.
        using var host = new NativeWindow(new NativeWindowSettings
        {
            StartVisible = false, StartFocused = false, APIVersion = new Version(3, 3),
            Profile = ContextProfile.Core, Flags = ContextFlags.Offscreen, WindowBorder = WindowBorder.Hidden,
        });
        int texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        var pixels = Enumerable.Range(0, 16).SelectMany(_ => Colour).ToArray();
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 4, 4, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.Finish();

        var context = ViewGlContext.From(host);
        var view = new SkyRenderView(context);
        view.HostContext.Should().BeSameAs(context);
        view.Mode.Should().Be(ViewHostMode.NativeWindow);

        bool textureSeen = false, closingRaised = false;
        byte[]? centre = null;
        var window = new Window
        {
            Width = 240, Height = 200, Content = view, ShowInTaskbar = false, Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Title = "SkyRender shared-context test",
        };
        view.GlClosing += () => closingRaised = true;
        view.RenderFrame += f =>
        {
            if (centre is not null) return;
            textureSeen = GL.IsTexture(texture);
            // Copy the host's texture over the whole view, then read the view's back buffer.
            int fbo = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fbo);
            GL.FramebufferTexture2D(FramebufferTarget.ReadFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, texture, 0);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
            GL.BlitFramebuffer(0, 0, 4, 4, 0, 0, f.Width, f.Height, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
            GL.DeleteFramebuffer(fbo);
            GL.ReadBuffer(ReadBufferMode.Back);
            var px = new byte[4];
            GL.ReadPixels(f.Width / 2, f.Height / 2, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, px);
            centre = px;
            window.Dispatcher.BeginInvoke(window.Close, DispatcherPriority.Background);
        };
        var timeout = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Normal, (_, _) => window.Close(), window.Dispatcher);
        window.Closed += (_, _) => { timeout.Stop(); window.Dispatcher.InvokeShutdown(); };
        window.Show();
        Dispatcher.Run();
        view.Dispose();

        centre.Should().NotBeNull("the view should have drawn a frame before the timeout");
        textureSeen.Should().BeTrue("the view's frame runs on the host's context, where the texture lives");
        centre.Should().Equal(Colour, "the host's texture should be what the view drew");
        closingRaised.Should().BeTrue();

        // The context is the host's again: current on its hidden window, with its objects alive.
        host.Context.IsCurrent.Should().BeTrue("the view should leave the host's context current on its hidden window");
        GL.IsTexture(texture).Should().BeTrue("the view must not dispose the host's context");
        GL.DeleteTexture(texture);
    }
}

/// <summary>Tests that put a GL window on screen run alone: a live view's frames and the window's
/// own drawing load the UI and the clock that the pacer tests time themselves on.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlWindowCollection
{
    public const string Name = "GL window";
}
