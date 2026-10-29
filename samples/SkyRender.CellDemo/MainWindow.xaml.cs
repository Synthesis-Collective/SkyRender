using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Wpf;
using SkyRender.World;

namespace SkyRender.CellDemo;

/// <summary>
/// The cell viewer. Picking a cell starts a load on a worker thread (the cell walk, model parsing,
/// archive extraction; SceneLoader), cancelling any load still running. When it finishes, the GL
/// render callback disposes the previous cell's GL resources and uploads the new one a slice per
/// frame (GlScene), so the window keeps responding. Every stage is logged with its timing.
/// </summary>
public partial class MainWindow : Window
{
    private const int MaxLogChars = 400_000;
    /// <summary>GL upload time per frame while a cell goes in.</summary>
    private const double InstallBudgetMs = 30;

    private readonly CellLog _log = CellLog.Instance;
    private readonly RenderLogGate _logGate = new();
    private readonly StartupOptions _options = StartupOptions.Current;

    private GameData? _game;
    private GameAssetResolver? _resolver;
    private AssetCache? _assets;
    private SceneLoader? _loader;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private CancellationTokenSource? _loadCts;
    private int _generation;

    private GlRenderer? _renderer;
    private GlScene? _scene;
    private PreparedScene? _pending;                 // handed from the loader to the GL thread
    private readonly OrbitCamera _camera = new();
    private string? _lastRenderError;
    private readonly Stopwatch _frameClock = Stopwatch.StartNew();
    private double _lastFrameSeconds;
    private CellEntry? _current;
    private bool _suppressSelection;

    // Scripted runs (StartupOptions): the queue of cells to open and screenshots to take.
    private readonly Queue<string> _cycle = new();
    private int _settleFrames = -1;
    private int _shotIndex;

    public MainWindow()
    {
        InitializeComponent();
        // A scripted run opens without taking the focus from whatever the user is doing.
        if (_options.Automated) ShowActivated = false;
        _log.Attach(Dispatcher, AppendLogLine);
        LogPathText.Text = _log.FilePath ?? "(no log file)";
        _log.Info("SkyRender " + SkyRenderInfo.Version + " cell demo");
        LodBox.IsChecked = _options.Lod;
        RingBox.IsChecked = _options.Ring;
        foreach (var c in _options.Cycle) _cycle.Enqueue(c);

        Loaded += async (_, _) =>
        {
            StartGl();
            if (_options.Size is { } size) FitWindowToView(size.W, size.H);
            await OpenGameAsync(_options.DataFolder);
        };
        Closed += (_, _) =>
        {
            _loadCts?.Cancel();
            _scene?.Dispose();
            _renderer?.Dispose();
            _game?.Dispose();
        };
    }

    private void StartGl()
    {
        try
        {
            GlControl.Start(new GLWpfControlSettings { MajorVersion = 3, MinorVersion = 3, RenderContinuously = true });
            // GLWpfControl 4.x hooks its render loop up in IsVisibleChanged, which never fires for a
            // control that is already visible when Start() runs: without this toggle no frame is drawn
            // until something else forces a redraw (USAGE.md, section 3).
            GlControl.Visibility = Visibility.Collapsed;
            GlControl.Visibility = Visibility.Visible;
            _log.Info("GL control started (requested OpenGL 3.3).");
        }
        catch (Exception ex)
        {
            _log.LogError("Could not start the GL control", ex);
            StatusText.Text = "OpenGL could not start: " + ex.Message;
        }
    }

    // ---- the game ----

    /// <summary>Opens the game's data (detected, or <paramref name="dataFolder"/>) and fills the lists.</summary>
    private async Task OpenGameAsync(string? dataFolder)
    {
        _loadCts?.Cancel();
        await _loadGate.WaitAsync();
        try
        {
            StatusText.Text = dataFolder is null ? "Looking for Skyrim Special Edition and reading its load order..." : "Reading " + dataFolder + "...";
            var clock = Stopwatch.StartNew();
            var game = await Task.Run(() => GameData.Open(dataFolder, _options.LoadOrderFile, _log, CancellationToken.None));
            var oldGame = _game;
            _game = game;
            DataFolderBox.Text = game.DataFolder;
            _resolver = new GameAssetResolver(new FixedDataFolder(game.DataFolder),
                new LoadOrderBsaProvider(game.DataFolder, game.Mods.Select(m => m.ModKey)), _logGate, _log,
                extractionDirectory: Path.Combine(Path.GetTempPath(), "SkyRender.CellDemo", "extracted"));
            _assets = new AssetCache(_resolver, _log, _logGate);
            _loader = new SceneLoader(game, _assets, _log);
            oldGame?.Dispose();

            _suppressSelection = true;
            WorldBox.ItemsSource = game.Worlds;
            WorldBox.SelectedItem = game.Worlds.FirstOrDefault(w => string.Equals(w.EditorId, "Tamriel", StringComparison.OrdinalIgnoreCase))
                                    ?? game.Worlds.FirstOrDefault();
            FilterInteriors();
            FilterExteriors();
            _suppressSelection = false;
            StatusText.Text = $"{game.Mods.Count} plugin(s), {game.Interiors.Count} interiors, " +
                $"{game.Worlds.Sum(w => w.Cells.Count)} exterior cells; ready in {clock.Elapsed.TotalSeconds:0.0} s. Pick a cell.";
            _log.Info(StatusText.Text);
        }
        catch (Exception ex)
        {
            _log.LogError("Could not open the game data", ex);
            StatusText.Text = "Could not read the game: " + ex.Message + " Use Browse… to pick the Data folder.";
            if (_options.Exit) Close();
            return;
        }
        finally
        {
            _loadGate.Release();
        }

        // A scripted start: open the named cell.
        if (FindCell(_options.Cell, _options.World, _options.Grid) is { } start) SelectAndLoad(start.Cell, start.World);
        else if (_options.Cell is not null || _options.Grid is not null)
        {
            _log.LogError("Cell not found: " + (_options.Cell ?? $"{_options.World} {_options.Grid}"));
            if (_options.Exit) Close();
        }
    }

    private async void BrowseData_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the game's Data folder" };
        if (dialog.ShowDialog(this) == true) await OpenGameAsync(dialog.FolderName);
    }

    /// <summary>A cell by EditorID (interior or exterior), "World@X,Y", or worldspace + grid.</summary>
    private (CellEntry Cell, WorldEntry? World)? FindCell(string? name, string? world, (int X, int Y)? grid)
    {
        if (_game is null) return null;
        if (name is { } n && n.Contains('@'))
        {
            var parts = n.Split('@');
            var xy = parts[1].Split(',');
            return FindCell(null, parts[0], (int.Parse(xy[0]), int.Parse(xy[1])));
        }
        if (grid is { } g)
        {
            var w = _game.Worlds.FirstOrDefault(x => string.Equals(x.EditorId, world ?? "Tamriel", StringComparison.OrdinalIgnoreCase));
            return w is not null && w.ByGrid.TryGetValue(g, out var c) ? (c, w) : null;
        }
        if (name is null) return null;
        if (_game.Interiors.FirstOrDefault(c => string.Equals(c.EditorId, name, StringComparison.OrdinalIgnoreCase)) is { } interior)
            return (interior, null);
        foreach (var w in _game.Worlds)
            if (w.Cells.FirstOrDefault(c => string.Equals(c.EditorId, name, StringComparison.OrdinalIgnoreCase)) is { } ext)
                return (ext, w);
        return null;
    }

    // ---- the lists ----

    private void InteriorSearch_TextChanged(object sender, TextChangedEventArgs e) => FilterInteriors();
    private void ExteriorSearch_TextChanged(object sender, TextChangedEventArgs e) => FilterExteriors();

    private void FilterInteriors()
    {
        if (_game is null) return;
        string f = InteriorSearch.Text.Trim();
        var list = _game.Interiors.Where(c => c.Matches(f)).ToList();
        InteriorList.ItemsSource = list;
        InteriorCount.Text = $"{list.Count} of {_game.Interiors.Count} interior cell(s)";
    }

    private void FilterExteriors()
    {
        if (WorldBox.SelectedItem is not WorldEntry w) { ExteriorList.ItemsSource = null; return; }
        string f = ExteriorSearch.Text.Trim();
        var list = w.Cells.Where(c => c.Matches(f)).ToList();
        ExteriorList.ItemsSource = list;
        ExteriorCount.Text = $"{list.Count} of {w.Cells.Count} cell(s) in {w.EditorId}";
    }

    private void WorldBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => FilterExteriors();
    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void CellList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || sender is not ListBox { SelectedItem: CellEntry cell }) return;
        if (ReferenceEquals(cell, _current)) return;
        LoadCell(cell, cell.IsInterior ? null : WorldBox.SelectedItem as WorldEntry);
    }

    /// <summary>Shows <paramref name="cell"/> in its list (switching tab and worldspace) and loads it.</summary>
    private void SelectAndLoad(CellEntry cell, WorldEntry? world)
    {
        _suppressSelection = true;
        if (cell.IsInterior)
        {
            Tabs.SelectedIndex = 0;
            InteriorSearch.Text = "";
            FilterInteriors();
            InteriorList.SelectedItem = cell;
            InteriorList.ScrollIntoView(cell);
        }
        else
        {
            Tabs.SelectedIndex = 1;
            WorldBox.SelectedItem = world;
            ExteriorSearch.Text = "";
            FilterExteriors();
            ExteriorList.SelectedItem = cell;
            ExteriorList.ScrollIntoView(cell);
        }
        _suppressSelection = false;
        LoadCell(cell, world);
    }

    private void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _current is null || _current.IsInterior) return;
        var world = _game?.Worlds.FirstOrDefault(w => w.Key == _current.World);
        LoadCell(_current, world, force: true);
    }

    // ---- loading ----

    private async void LoadCell(CellEntry cell, WorldEntry? world, bool force = false)
    {
        if (_loader is null || (!force && ReferenceEquals(cell, _current))) return;
        _current = cell;
        SelectedText.Text = cell.Display;
        WinnerText.Text = "Winning plugin: " + cell.WinningPlugin + (cell.IsInterior ? "  (interior)" : $"  ({world?.EditorId})");

        // Cancel the load still running, if any; loads run one at a time.
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        int generation = ++_generation;
        bool ring = RingBox.IsChecked == true, lod = LodBox.IsChecked == true;
        _log.Info($"Load {cell.Display} (winner {cell.WinningPlugin}){(ring ? ", with the neighbouring cells" : "")}{(lod ? ", with distant LOD" : "")}.");
        StatusText.Text = "Loading " + cell.Display + "...";

        PreparedScene? prepared = null;
        var clock = Stopwatch.StartNew();
        await _loadGate.WaitAsync();
        try
        {
            if (cts.IsCancellationRequested) return;
            var loader = _loader;
            prepared = await Task.Run(() => loader.Prepare(cell, world, ring, lod, generation,
                msg => Dispatcher.BeginInvoke(() => { if (generation == _generation) StatusText.Text = cell.Display + ": " + msg; }),
                cts.Token));
        }
        catch (OperationCanceledException)
        {
            _log.Info($"Load of {cell.Display} cancelled after {clock.ElapsedMilliseconds} ms (another cell was picked).");
            return;
        }
        catch (Exception ex)
        {
            _log.LogError("Could not load " + cell.Display, ex);
            if (generation == _generation) StatusText.Text = "Could not load " + cell.Display + ": " + ex.Message;
            AdvanceScript();
            return;
        }
        finally
        {
            _loadGate.Release();
        }
        if (generation != _generation) return;
        _log.Info($"{cell.Display} read in {clock.ElapsedMilliseconds} ms ({string.Join(", ", prepared.Timings)}); " +
            $"{prepared.Models.Count} distinct model(s){(prepared.MissingModels > 0 ? $", {prepared.MissingModels} not found" : "")}. Uploading.");
        _pending = prepared;   // picked up by the next frame
    }

    // ---- GL ----

    private void GlControl_Render(TimeSpan delta)
    {
        try
        {
            if (_renderer == null)
            {
                _log.Info($"OpenGL {GL.GetString(StringName.Version)} on {GL.GetString(StringName.Renderer)} ({GL.GetString(StringName.Vendor)}).");
                _renderer = new GlRenderer();   // the Scene profile: full detail
                _renderer.Initialize(ModuleResourceLocator.ShaderDirectory);
                _log.Info("Renderer initialised.");
            }

            if (_pending is { } next && next.Generation == _generation)
            {
                _pending = null;
                _scene?.Dispose();
                _scene = null;
                if (_assets is { } assets && _resolver is { } resolver)
                {
                    _scene = new GlScene(_renderer, assets, resolver, _log, next);
                    FrameCamera(next.Data);
                }
            }

            double now = _frameClock.Elapsed.TotalSeconds;
            float dt = (float)Math.Min(0.1, now - _lastFrameSeconds);
            _lastFrameSeconds = now;
            Fly(dt);

            if (_scene is { Installed: false } installing)
            {
                if (installing.Step(InstallBudgetMs)) Finished(installing);
                else StatusText.Text = $"{installing.Scene.Data.Title}: uploading {installing.Stage}...";
            }

            var eye = _camera.GetEyePosition();
            _scene?.UpdateLights(eye);
            if (_scene is { Scene.Data.SunShadows: true })
            {
                // The sun's shadow map follows the view: centred on what the camera looks at, wide
                // enough for the view's near ground, capped so it keeps its detail.
                float radius = Math.Clamp(_camera.Distance * 1.2f + 1500f, 2500f, 12000f);
                _renderer.ShadowBounds = (_camera.Target, radius);
            }

            var source = PresentationSource.FromVisual(GlControl);
            double sx = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double sy = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            int width = Math.Max(1, (int)(GlControl.ActualWidth * sx));
            int height = Math.Max(1, (int)(GlControl.ActualHeight * sy));
            _renderer.Render(_camera, width, height);
            _lastRenderError = null;

            if (_settleFrames > 0 && --_settleFrames == 0) Screenshot(width, height);
        }
        catch (Exception ex)
        {
            // Log each distinct failure once rather than every frame.
            if (ex.Message != _lastRenderError)
            {
                _lastRenderError = ex.Message;
                _log.LogError("Render failed", ex);
                StatusText.Text = "Render failed: " + ex.Message + " (see the log)";
            }
        }
    }

    private void Finished(GlScene scene)
    {
        var p = scene.Scene;
        double total = p.Clock.Elapsed.TotalMilliseconds;
        var proc = Process.GetCurrentProcess();
        string missing = scene.Textures.MissingTexturePaths.Count > 0 ? $", {scene.Textures.MissingTexturePaths.Count} texture(s) not found" : "";
        string lod = p.Lod is { } l ? (l.Problem is { } why ? $"; LOD not drawn: {why}" : $"; LOD {l.Parts.Count} part(s)") : "";
        StatusText.Text = $"{p.Data.Title} [{p.Data.WinningPlugin}]: {p.Data.Models.Count} objects, {scene.MeshCount} meshes, " +
            $"{scene.Textures.OwnedTextureCount} textures{missing}" +
            (scene.GrassInstances > 0 ? $", {scene.GrassInstances:N0} grass" : "") + lod +
            $". Loaded in {total / 1000:0.00} s (read {p.Timings.LastOrDefault()}, upload {scene.InstallMs:0} ms).";
        _log.Info(StatusText.Text);
        _log.Info($"  memory: managed {GC.GetTotalMemory(false) / 1048576.0:0} MB, private {proc.PrivateMemorySize64 / 1048576.0:0} MB; " +
            $"{_renderer!.Meshes.Count} meshes in the renderer.");
        foreach (var t in scene.Textures.MissingTexturePaths.Take(10)) _log.LogMessage("  texture not found: " + t);
        if (_options.Shot != null || _cycle.Count > 0) _settleFrames = 20;
        else if (_options.Exit) Dispatcher.BeginInvoke(Close);
    }

    // ---- camera ----

    private bool _customEyeUsed;

    private void FrameCamera(SceneData data)
    {
        var c = data.Camera;
        _camera.Target = ReferencePlacement.ToMeshSpace(c.Target);
        _camera.Azimuth = c.Azimuth;
        _camera.Elevation = c.Elevation;
        _camera.Distance = c.Distance;
        _camera.FieldOfView = c.FieldOfView;
        _camera.MinDistance = 1f;
        _camera.MaxDistance = 400000f;
        // The game's near plane (fNearDistance 15); the far plane reaches the LOD when it is drawn.
        _camera.NearPlane = data.Interior ? 5f : 15f;
        _camera.FarPlane = data.Interior ? 60000f : (LodBox.IsChecked == true ? SceneLoader.LodReach : 131072f);

        // A scripted camera (--eye / --yaw / --pitch): only for the first cell.
        if (_options.Eye is { } eye && !_customEyeUsed)
        {
            _customEyeUsed = true;
            float yaw = MathHelper.DegreesToRadians(_options.Yaw), pitch = MathHelper.DegreesToRadians(_options.Pitch);
            var forward = new System.Numerics.Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Cos(yaw) * MathF.Cos(pitch), -MathF.Sin(pitch));
            const float d = 100f;
            var target = ReferencePlacement.ToMeshSpace(eye + forward * d);
            var offset = ReferencePlacement.ToMeshSpace(eye) - target;
            _camera.Target = target;
            _camera.Distance = d;
            _camera.Elevation = Math.Clamp(MathHelper.RadiansToDegrees(MathF.Asin(offset.Y / d)), -89.5f, 89.5f);
            _camera.Azimuth = MathHelper.RadiansToDegrees(MathF.Atan2(offset.X, offset.Z));
            if (_options.Fov is { } fov) _camera.FieldOfView = fov;
        }
    }

    private bool _looking;
    private Point _lookFrom;

    private void GlControl_MouseDown(object sender, MouseButtonEventArgs e)
    {
        GlControl.Focus();
        var p = e.GetPosition(GlControl);
        if (e.ChangedButton == MouseButton.Right)
        {
            _looking = true;
            _lookFrom = p;
        }
        else
        {
            _camera.OnMouseDown((float)p.X, (float)p.Y,
                e.LeftButton == MouseButtonState.Pressed, e.MiddleButton == MouseButtonState.Pressed);
        }
        GlControl.CaptureMouse();
    }

    private void GlControl_MouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(GlControl);
        if (_looking)
        {
            // Look around: turn the view about the eye (the orbit's target moves, the eye stays).
            var eye = _camera.GetEyePosition();
            _camera.Azimuth -= (float)(p.X - _lookFrom.X) * 0.25f;
            _camera.Elevation = Math.Clamp(_camera.Elevation + (float)(p.Y - _lookFrom.Y) * 0.25f, -89f, 89f);
            _camera.Target = eye - OrbitOffset();
            _lookFrom = p;
            return;
        }
        _camera.OnMouseMove((float)p.X, (float)p.Y);
    }

    private void GlControl_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right) _looking = false;
        else _camera.OnMouseUp();
        if (!_looking && e.LeftButton == MouseButtonState.Released && e.MiddleButton == MouseButtonState.Released)
            GlControl.ReleaseMouseCapture();
    }

    private void GlControl_MouseWheel(object sender, MouseWheelEventArgs e) => _camera.OnMouseWheel(e.Delta);

    private void GlControl_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Home && _scene is { } s) FrameCamera(s.Scene.Data);
    }

    /// <summary>The eye's offset from the target (OrbitCamera's spherical coordinates).</summary>
    private Vector3 OrbitOffset()
    {
        float az = MathHelper.DegreesToRadians(_camera.Azimuth), el = MathHelper.DegreesToRadians(_camera.Elevation);
        return new Vector3(MathF.Cos(el) * MathF.Sin(az), MathF.Sin(el), MathF.Cos(el) * MathF.Cos(az)) * _camera.Distance;
    }

    /// <summary>Fly: W A S D along the view, Q / E down and up; the eye and target move together.</summary>
    private void Fly(float dt)
    {
        if (!GlControl.IsKeyboardFocused || dt <= 0f) return;
        var move = Vector3.Zero;
        var forward = -OrbitOffset().Normalized();
        var right = Vector3.Cross(forward, Vector3.UnitY);
        if (right.LengthSquared < 1e-6f) right = Vector3.UnitX;
        right.Normalize();
        if (Keyboard.IsKeyDown(Key.W)) move += forward;
        if (Keyboard.IsKeyDown(Key.S)) move -= forward;
        if (Keyboard.IsKeyDown(Key.D)) move += right;
        if (Keyboard.IsKeyDown(Key.A)) move -= right;
        if (Keyboard.IsKeyDown(Key.E)) move += Vector3.UnitY;
        if (Keyboard.IsKeyDown(Key.Q)) move -= Vector3.UnitY;
        if (move.LengthSquared < 1e-6f) return;
        float speed = (_scene?.Scene.Data.Interior ?? false) ? 400f : 1500f;
        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) speed *= 4f;
        _camera.Target += move.Normalized() * speed * dt;
    }

    // ---- scripted runs ----

    /// <summary>
    /// --size: grows or shrinks the window until the 3D view, which always fills its panel, is
    /// <paramref name="width"/> x <paramref name="height"/> device pixels. The view row shares the
    /// window's height with the log row, so this takes a few layout passes. If the screen will not
    /// take a window that large, the view is pinned at the size for the run and the log says so.
    /// </summary>
    private void FitWindowToView(int width, int height)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double w = width / dpi.DpiScaleX, h = height / dpi.DpiScaleY;
        int passes = 0;
        void Adjust()
        {
            double dw = w - GlHost.ActualWidth, dh = h - GlHost.ActualHeight;
            if (Math.Abs(dw) < 0.5 && Math.Abs(dh) < 0.5) return;
            if (++passes > 8)
            {
                GlControl.Width = w;
                GlControl.Height = h;
                _log.Info($"The window cannot fit a {width} x {height} view on this screen; the view is pinned at that size.");
                return;
            }
            Width += dw;
            Height += dh;
            // Measure again once the window has laid itself out at the new size.
            Dispatcher.BeginInvoke(Adjust, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        Adjust();
    }

    /// <summary>Saves the 3D view (the bound framebuffer of the GL control) as a PNG.</summary>
    private void Screenshot(int width, int height)
    {
        if (_options.Shot is { } shot)
        {
            var rgba = new byte[width * height * 4];
            GL.ReadPixels(0, 0, width, height, OpenTK.Graphics.OpenGL4.PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
            var bgr = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                int src = (height - 1 - y) * width * 4, dst = y * width * 4;   // GL rows are bottom-up
                for (int x = 0; x < width; x++, src += 4, dst += 4)
                {
                    bgr[dst] = rgba[src + 2]; bgr[dst + 1] = rgba[src + 1]; bgr[dst + 2] = rgba[src]; bgr[dst + 3] = 255;
                }
            }
            string path = _shotIndex == 0 ? shot
                : Path.Combine(Path.GetDirectoryName(shot)!, Path.GetFileNameWithoutExtension(shot) + $"_{_shotIndex:00}.png");
            _shotIndex++;
            var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bgr, width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var file = File.Create(path)) encoder.Save(file);
            _log.Info($"Saved {path} ({width} x {height}).");
            if (_shotIndex == 1) SaveWindowImage(Path.Combine(Path.GetDirectoryName(shot)!, Path.GetFileNameWithoutExtension(shot) + "_window.png"));
        }
        AdvanceScript();
    }

    /// <summary>The whole window as WPF draws it (panels, lists and the 3D view), for documentation.</summary>
    private void SaveWindowImage(string path)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var content = (FrameworkElement)Content;
            var bitmap = new RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
                96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(path);
            encoder.Save(file);
            _log.Info("Saved " + path + ".");
        }
        catch (Exception ex)
        {
            _log.LogError("Could not save the window image", ex);
        }
    }

    /// <summary>Opens the next cell of --cycle (then the first cell again), or closes with --exit.</summary>
    private void AdvanceScript()
    {
        if (_cycle.Count > 0)
        {
            string next = _cycle.Dequeue();
            // "!Cell" starts that load and picks the next cell 200 ms later, while it is still
            // loading: a cancellation test.
            bool interrupt = next.StartsWith('!');
            if (interrupt) next = next[1..];
            Dispatcher.BeginInvoke(async () =>
            {
                if (FindCell(next, null, null) is { } c) SelectAndLoad(c.Cell, c.World);
                else { _log.LogError("Cell not found: " + next); AdvanceScript(); return; }
                if (interrupt)
                {
                    await Task.Delay(200);
                    AdvanceScript();
                }
            });
        }
        else if (_options.Exit) Dispatcher.BeginInvoke(Close);
    }

    // ---- log pane ----

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
        if (_log.FilePath is { } path && File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
