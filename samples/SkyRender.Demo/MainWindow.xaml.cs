using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Wpf;

namespace SkyRender.Demo;

/// <summary>
/// Minimal SkyRender host: parse a NIF off the UI thread, then upload and texture it on the GL
/// thread (the control's render callback), frame the camera on its bounds, and draw every frame.
/// Every stage is logged to the log pane and to a file.
/// </summary>
public partial class MainWindow : Window
{
    private const int MaxLogChars = 400_000;

    private readonly DemoLog _logger = DemoLog.Instance;
    private readonly RenderLogGate _logGate = new();

    private GlRenderer? _renderer;
    private GlTextureManager? _textures;
    private readonly OrbitCamera _camera = new();
    private string? _lastRenderError;

    // Set by a finished background parse; consumed by the next render callback (GL thread).
    private PendingScene? _pending;
    private sealed record PendingScene(string NifPath, List<NifMeshBuilder.BuiltMesh> Shapes,
        AssetCache Assets, GameAssetResolver Resolver, int ArchiveCount);

    public MainWindow()
    {
        InitializeComponent();
        _logger.Attach(Dispatcher, AppendLogLine);
        LogPathText.Text = _logger.FilePath ?? "(no log file)";
        _logger.Info("SkyRender " + SkyRenderInfo.Version + ", shaders from " + ModuleResourceLocator.ShaderDirectory);

        Loaded += (_, _) =>
        {
            try
            {
                GlControl.Start(new GLWpfControlSettings
                {
                    MajorVersion = 3,
                    MinorVersion = 3,
                    RenderContinuously = true,
                });
                // GLWpfControl 4.x hooks its render loop up in IsVisibleChanged, which never fires
                // for a control that is already visible when Start() runs: without this toggle no
                // frame is drawn until something else forces a redraw.
                GlControl.Visibility = Visibility.Collapsed;
                GlControl.Visibility = Visibility.Visible;
                _logger.Info("GL control started (requested OpenGL 3.3).");
            }
            catch (Exception ex)
            {
                _logger.LogError("Could not start the GL control", ex);
                StatusText.Text = "OpenGL could not start: " + ex.Message;
            }
        };
        Closed += (_, _) =>
        {
            _renderer?.ClearMeshes();
            _textures?.Dispose();
            _renderer?.Dispose();
        };
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "NIF meshes (*.nif)|*.nif|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        string nifPath = dialog.FileName;

        // A NIF inside <Data>\meshes\... tells us where the Data folder is.
        if (string.IsNullOrWhiteSpace(DataFolderBox.Text) && GuessDataFolder(nifPath) is { } guessed)
            DataFolderBox.Text = guessed;
        string dataFolder = DataFolderBox.Text.Trim();

        _logger.Info("Open " + nifPath);
        _logger.Info("Data folder: " + (dataFolder.Length > 0 ? dataFolder : "(none)"));

        OpenButton.IsEnabled = false;
        StatusText.Text = "Loading " + Path.GetFileName(nifPath) + "…";
        try
        {
            _pending = await Task.Run(() => Parse(nifPath, dataFolder));
            _logger.Info("Waiting for the next frame to upload the meshes.");
        }
        catch (Exception ex)
        {
            _logger.LogError("Could not read " + nifPath, ex);
            StatusText.Text = "Could not read " + Path.GetFileName(nifPath) + ": " + ex.Message;
        }
        finally
        {
            OpenButton.IsEnabled = true;
        }
    }

    private void BrowseData_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the game's Data folder" };
        if (dialog.ShowDialog(this) == true) DataFolderBox.Text = dialog.FolderName;
    }

    /// <summary>GL-free work: index archives, set up asset resolution, parse the NIF.</summary>
    private PendingScene Parse(string nifPath, string dataFolder)
    {
        var clock = Stopwatch.StartNew();
        var bsa = new FolderBsaProvider(dataFolder);
        _logger.Info($"Indexed {bsa.ArchiveCount} archive(s) in {clock.ElapsedMilliseconds} ms.");

        var resolver = new GameAssetResolver(new FixedDataFolder(dataFolder), bsa, _logGate, _logger);
        var assets = new AssetCache(resolver, _logger, _logGate);
        clock.Restart();
        var shapes = assets.MeshBuilder.BuildFromFile(nifPath);
        _logger.Info($"Parsed {shapes.Count} shape(s) in {clock.ElapsedMilliseconds} ms:");
        foreach (var shape in shapes)
        {
            _logger.Info($"  {shape.ShapeName}: {shape.Positions.Length} vertices, {shape.Indices.Length / 3} triangles, " +
                $"textures [{string.Join(", ", shape.TexturePaths.OrderBy(t => t.Key).Select(t => t.Key + "=" + t.Value))}]");
        }
        return new PendingScene(nifPath, shapes, assets, resolver, bsa.ArchiveCount);
    }

    private void GlControl_Render(TimeSpan delta)
    {
        try
        {
            if (_renderer == null)
            {
                _logger.Info($"OpenGL {GL.GetString(StringName.Version)} on {GL.GetString(StringName.Renderer)} " +
                    $"({GL.GetString(StringName.Vendor)}).");
                _renderer = new GlRenderer();
                _renderer.Initialize(ModuleResourceLocator.ShaderDirectory);
                _logger.Info("Renderer initialised.");
            }

            if (_pending is { } scene)
            {
                _pending = null;
                Install(scene);
            }

            var source = PresentationSource.FromVisual(GlControl);
            double sx = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double sy = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            int width = Math.Max(1, (int)(GlControl.ActualWidth * sx));
            int height = Math.Max(1, (int)(GlControl.ActualHeight * sy));
            _renderer.Render(_camera, width, height);
            _lastRenderError = null;
        }
        catch (Exception ex)
        {
            // Log each distinct failure once rather than every frame.
            if (ex.Message != _lastRenderError)
            {
                _lastRenderError = ex.Message;
                _logger.LogError("Render failed", ex);
                StatusText.Text = "Render failed: " + ex.Message + " (see the log)";
            }
        }
    }

    /// <summary>GL-thread work: upload each shape, bind its NIF material and textures, frame the camera.</summary>
    private void Install(PendingScene scene)
    {
        var clock = Stopwatch.StartNew();
        _logger.Info("Uploading " + scene.Shapes.Count + " shape(s).");
        _renderer!.ClearMeshes();
        _textures?.Dispose();
        _textures = new GlTextureManager(scene.Assets, _logger);
        _textures.Initialize();

        var options = new MaterialBindOptions { RenderMissingTextureAsWireframe = true };
        foreach (var shape in scene.Shapes)
        {
            var shapeClock = Stopwatch.StartNew();
            var mesh = GlMeshFactory.Create(shape);
            NifMaterialBinder.Bind(_textures, scene.Resolver, mesh, shape, shape.TexturePaths, options);
            _renderer.AddMesh(mesh);
            _logger.Info($"  {shape.ShapeName} ready in {shapeClock.ElapsedMilliseconds} ms.");
        }

        FrameCamera();
        var missing = _textures.MissingTexturePaths;
        foreach (var path in missing) _logger.Info("  Texture not found: " + path);
        _logger.Info($"Scene ready in {clock.ElapsedMilliseconds} ms.");

        StatusText.Text = $"{Path.GetFileName(scene.NifPath)}: {scene.Shapes.Count} shape(s), " +
            $"{scene.ArchiveCount} archive(s) indexed" +
            (missing.Count > 0 ? $", {missing.Count} texture(s) not found" : "") + ".";
    }

    /// <summary>Points the orbit camera at the scene's bounding box and fits the shadow map to it.</summary>
    private void FrameCamera()
    {
        var points = _renderer!.Meshes.Where(m => m.CpuPositions != null).SelectMany(m => m.CpuPositions!).ToList();
        if (points.Count == 0)
        {
            _logger.Info("Nothing to frame: the scene has no vertex positions.");
            return;
        }

        var min = new Vector3(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z));
        var max = new Vector3(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z));
        var center = (min + max) * 0.5f;
        float radius = Math.Max(1f, (max - min).Length * 0.5f);

        _camera.Target = center;
        _camera.MinDistance = radius * 0.05f;
        _camera.MaxDistance = radius * 20f;
        _camera.FarPlane = radius * 50f;
        // Back off until the bounding sphere fits the (narrow, portrait-style) field of view.
        _camera.Distance = radius / MathF.Sin(MathHelper.DegreesToRadians(_camera.FieldOfView) * 0.5f) * 1.1f;
        _camera.Azimuth = 180f;   // NIF models face -Z; this looks at the front.
        _camera.Elevation = 15f;
        _renderer.ShadowBounds = (center, radius);
        _logger.Info($"Framed bounds {min} to {max}, camera distance {_camera.Distance:0.#}.");
    }

    private static string? GuessDataFolder(string nifPath)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(nifPath)!);
        for (var d = dir; d != null; d = d.Parent)
        {
            if (string.Equals(d.Name, "meshes", StringComparison.OrdinalIgnoreCase))
                return d.Parent?.FullName;
        }
        return null;
    }

    // ---- Log pane ----

    private void AppendLogLine(string line)
    {
        if (LogBox.Text.Length > MaxLogChars)
            LogBox.Text = LogBox.Text[^(MaxLogChars / 2)..];
        bool atEnd = LogBox.VerticalOffset + LogBox.ViewportHeight >= LogBox.ExtentHeight - 1;
        LogBox.AppendText(line + Environment.NewLine);
        if (atEnd) LogBox.ScrollToEnd();
    }

    private void VerboseBox_Changed(object sender, RoutedEventArgs e) => _logGate.Verbose = VerboseBox.IsChecked == true;

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (LogBox.Text.Length > 0) Clipboard.SetText(LogBox.Text);
    }

    private void OpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        if (_logger.FilePath is { } path && File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    // ---- Camera input ----

    private void GlControl_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(GlControl);
        _camera.OnMouseDown((float)p.X, (float)p.Y,
            e.LeftButton == MouseButtonState.Pressed, e.MiddleButton == MouseButtonState.Pressed);
        GlControl.CaptureMouse();
    }

    private void GlControl_MouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(GlControl);
        _camera.OnMouseMove((float)p.X, (float)p.Y);
    }

    private void GlControl_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _camera.OnMouseUp();
        GlControl.ReleaseMouseCapture();
    }

    private void GlControl_MouseWheel(object sender, MouseWheelEventArgs e) => _camera.OnMouseWheel(e.Delta);
}
