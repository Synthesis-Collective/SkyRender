using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using OpenTK.Mathematics;
using OpenTK.Wpf;

namespace SkyRender.Demo;

/// <summary>
/// Minimal SkyRender host: parse a NIF off the UI thread, then upload and texture it on the GL
/// thread (the control's render callback), frame the camera on its bounds, and draw every frame.
/// </summary>
public partial class MainWindow : Window
{
    private readonly IRenderLogger _logger = new DebugLogger();
    private readonly RenderLogGate _logGate = new();

    private GlRenderer? _renderer;
    private GlTextureManager? _textures;
    private readonly OrbitCamera _camera = new();

    // Set by a finished background parse; consumed by the next render callback (GL thread).
    private PendingScene? _pending;
    private sealed record PendingScene(string NifPath, List<NifMeshBuilder.BuiltMesh> Shapes,
        AssetCache Assets, GameAssetResolver Resolver, int ArchiveCount);

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => GlControl.Start(new GLWpfControlSettings
        {
            MajorVersion = 3,
            MinorVersion = 3,
            RenderContinuously = true,
        });
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

        OpenButton.IsEnabled = false;
        StatusText.Text = "Loading " + Path.GetFileName(nifPath) + "…";
        try
        {
            _pending = await Task.Run(() => Parse(nifPath, dataFolder));
        }
        catch (Exception ex)
        {
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
        var bsa = new FolderBsaProvider(dataFolder);
        var resolver = new GameAssetResolver(new FixedDataFolder(dataFolder), bsa, _logGate, _logger);
        var assets = new AssetCache(resolver, _logger, _logGate);
        var shapes = assets.MeshBuilder.BuildFromFile(nifPath);
        return new PendingScene(nifPath, shapes, assets, resolver, bsa.ArchiveCount);
    }

    private void GlControl_Render(TimeSpan delta)
    {
        if (_renderer == null)
        {
            _renderer = new GlRenderer();
            _renderer.Initialize(ModuleResourceLocator.ShaderDirectory);
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
    }

    /// <summary>GL-thread work: upload each shape, bind its NIF material and textures, frame the camera.</summary>
    private void Install(PendingScene scene)
    {
        _renderer!.ClearMeshes();
        _textures?.Dispose();
        _textures = new GlTextureManager(scene.Assets, _logger);
        _textures.Initialize();

        var options = new MaterialBindOptions { RenderMissingTextureAsWireframe = true };
        foreach (var shape in scene.Shapes)
        {
            var mesh = GlMeshFactory.Create(shape);
            NifMaterialBinder.Bind(_textures, scene.Resolver, mesh, shape, shape.TexturePaths, options);
            _renderer.AddMesh(mesh);
        }

        FrameCamera();
        int missing = _textures.MissingTexturePaths.Count;
        StatusText.Text = $"{Path.GetFileName(scene.NifPath)}: {scene.Shapes.Count} shape(s), " +
            $"{scene.ArchiveCount} archive(s) indexed" +
            (missing > 0 ? $", {missing} texture(s) not found" : "") + ".";
    }

    /// <summary>Points the orbit camera at the scene's bounding box and fits the shadow map to it.</summary>
    private void FrameCamera()
    {
        var points = _renderer!.Meshes.Where(m => m.CpuPositions != null).SelectMany(m => m.CpuPositions!).ToList();
        if (points.Count == 0) return;

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
