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
using OpenTK.Wpf;
using SkyRender.Character;
using SkyRender.Character.Offscreen;

namespace SkyRender.CharacterDemo;

/// <summary>
/// The character viewer. Picking an NPC reads its records on a worker thread (NpcResolver), then
/// hands the paths to SkyRender.Character's <see cref="VM_CharacterViewer"/>, which parses the
/// models on a worker thread and uploads them a slice per frame from the GL control's render
/// callback. The default outfit goes in as mesh overrides with the same load. Picking another NPC
/// cancels the load still running; the viewer frees the previous NPC's meshes and textures when
/// the next one goes in.
/// </summary>
public partial class MainWindow : Window
{
    private const int MaxLogChars = 400_000;

    private readonly DemoLog _log = DemoLog.Instance;
    private readonly RenderLogGate _logGate = new();
    private readonly DemoViewerSettings _settings = new();
    private readonly StartupOptions _options = StartupOptions.Current;

    private GameData? _game;
    private NpcResolver? _resolver;
    private VM_CharacterViewer? _viewer;
    private CancellationTokenSource? _loadCts;
    private int _generation;
    private string? _lastRenderError;
    private bool _glStarted;

    // The NPC on screen (or going in), and the timings of its load.
    private NpcEntry? _current;
    private NpcResolution? _currentResolution;
    private Stopwatch? _loadClock;
    private long _resolveMs, _parseMs;

    // Scripted runs (StartupOptions): the queue of NPCs to open and screenshots to take.
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
        _log.Info("SkyRender " + SkyRenderInfo.Version + " character demo");
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
            DisposeViewer();
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
            _glStarted = true;
            _log.Info("GL control started (requested OpenGL 3.3).");
        }
        catch (Exception ex)
        {
            _log.LogError("Could not start the GL control", ex);
            StatusText.Text = "OpenGL could not start: " + ex.Message;
        }
    }

    // ---- the game ----

    /// <summary>Opens the game's data (detected, or <paramref name="dataFolder"/>), makes a viewer
    /// for it and fills the list.</summary>
    private async Task OpenGameAsync(string? dataFolder)
    {
        _loadCts?.Cancel();
        try
        {
            StatusText.Text = dataFolder is null ? "Looking for Skyrim Special Edition and reading its load order..." : "Reading " + dataFolder + "...";
            var clock = Stopwatch.StartNew();
            var game = await Task.Run(() => GameData.Open(dataFolder, _options.LoadOrderFile, _log, CancellationToken.None));

            // One viewer per load order: its asset resolver and caches belong to the Data folder.
            DisposeViewer();
            _game?.Dispose();
            _game = game;
            _resolver = new NpcResolver(game);
            DataFolderBox.Text = game.DataFolder;
            var assetResolver = new GameAssetResolver(new FixedDataFolder(game.DataFolder),
                new LoadOrderBsaProvider(game.DataFolder, game.Mods.Select(m => m.ModKey)), _logGate, _log,
                extractionDirectory: Path.Combine(Path.GetTempPath(), "SkyRender.CharacterDemo", "extracted"));
            var previewCache = new CharacterPreviewCache(_resolver, assetResolver, _log, _logGate, _settings);
            _viewer = new VM_CharacterViewer(assetResolver, _settings, previewCache, _logGate, _log, new UiThreadMarshaller());
            _viewer.SceneCommitted += Viewer_SceneCommitted;
            _viewer.MeshOverridesApplied += Viewer_MeshOverridesApplied;
            _current = null;

            FilterList();
            StatusText.Text = $"{game.Mods.Count} plugin(s), {game.Npcs.Count} NPCs; ready in {clock.Elapsed.TotalSeconds:0.0} s. Pick an NPC.";
            _log.Info(StatusText.Text);
        }
        catch (Exception ex)
        {
            _log.LogError("Could not open the game data", ex);
            StatusText.Text = "Could not read the game: " + ex.Message + " Use Browse… to pick the Data folder.";
            if (_options.Exit) Close();
            return;
        }

        // A scripted start: open the named NPC.
        if (_options.Npc is { } name)
        {
            if (FindNpc(name) is { } start) SelectAndLoad(start);
            else
            {
                _log.LogError("NPC not found: " + name);
                AdvanceScript();
            }
        }
    }

    /// <summary>Releases the viewer's GL resources. The UI thread owns the GL context, so this runs
    /// with it current.</summary>
    private void DisposeViewer()
    {
        if (_viewer == null) return;
        _viewer.SceneCommitted -= Viewer_SceneCommitted;
        _viewer.MeshOverridesApplied -= Viewer_MeshOverridesApplied;
        try
        {
            if (_glStarted) GlControl.Context?.MakeCurrent();
            _viewer.Dispose();
        }
        catch (Exception ex)
        {
            _log.LogError("Could not release the viewer", ex);
        }
        _viewer = null;
    }

    private async void BrowseData_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the game's Data folder" };
        if (dialog.ShowDialog(this) == true) await OpenGameAsync(dialog.FolderName);
    }

    /// <summary>An NPC by EditorID or FormKey.</summary>
    private NpcEntry? FindNpc(string name)
    {
        if (_game is null) return null;
        return _game.Npcs.FirstOrDefault(n => string.Equals(n.EditorId, name, StringComparison.OrdinalIgnoreCase))
               ?? _game.Npcs.FirstOrDefault(n => string.Equals(n.Key.ToString(), name, StringComparison.OrdinalIgnoreCase));
    }

    // ---- the list ----

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => FilterList();

    private void FilterList()
    {
        if (_game is null) return;
        string f = SearchBox.Text.Trim();
        var list = _game.Npcs.Where(n => n.Matches(f)).ToList();
        _suppressSelection = true;
        NpcList.ItemsSource = list;
        if (_current != null && list.Contains(_current)) NpcList.SelectedItem = _current;
        _suppressSelection = false;
        CountText.Text = $"{list.Count} of {_game.Npcs.Count} NPC(s)";
    }

    private bool _suppressSelection;

    private void NpcList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || NpcList.SelectedItem is not NpcEntry npc || ReferenceEquals(npc, _current)) return;
        LoadNpc(npc);
    }

    /// <summary>Shows <paramref name="npc"/> in the list and loads it.</summary>
    private void SelectAndLoad(NpcEntry npc)
    {
        _suppressSelection = true;
        SearchBox.Text = "";
        FilterList();
        _suppressSelection = true;
        NpcList.SelectedItem = npc;
        NpcList.ScrollIntoView(npc);
        _suppressSelection = false;
        LoadNpc(npc);
    }

    // ---- loading ----

    private async void LoadNpc(NpcEntry npc)
    {
        if (_resolver is null || _viewer is null) return;
        var viewer = _viewer;
        var resolver = _resolver;
        _current = npc;
        _currentResolution = null;
        _settleFrames = -1;
        SelectedText.Text = npc.Title;
        WinnerText.Text = npc.Key + ", winning plugin " + npc.WinningPlugin;
        OutfitText.Text = "";

        // Cancel the load still running, if any.
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        int generation = ++_generation;
        _loadClock = Stopwatch.StartNew();
        _log.Info($"Load {npc.Title} ({npc.Key}, winner {npc.WinningPlugin}).");
        StatusText.Text = "Loading " + npc.Title + "...";

        try
        {
            // 1. The records, on a worker thread.
            var resolution = await Task.Run(() => resolver.ResolveNpc(npc.Key), cts.Token);
            if (generation != _generation) return;
            _resolveMs = _loadClock.ElapsedMilliseconds;
            if (resolution == null)
            {
                _log.LogError("Could not resolve " + npc.Key);
                StatusText.Text = "Could not resolve " + npc.Title + ".";
                AdvanceScript();
                return;
            }
            _currentResolution = resolution;
            foreach (var step in resolution.Trail) _log.LogMessage("  " + step);
            foreach (var warning in resolution.Warnings) _log.Warn("  " + warning);
            foreach (var piece in resolution.Outfit)
                _log.LogMessage($"  outfit piece {piece.Key}: {piece.MeshPath} (slots {SlotList(piece.BipedSlots)})");
            OutfitText.Text = resolution.OutfitName is { } o
                ? $"Outfit {o}: {resolution.Outfit.Count} piece(s)" + (resolution.OutfitCoversBody ? "" : ", none on the body slot")
                : "No default outfit.";
            if (_options.Describe)
            {
                _log.Info($"{npc.Title}: outfit covers the body slot: {(resolution.OutfitCoversBody ? "yes" : "no")}.");
                StatusText.Text = npc.Title + ": described (not drawn).";
                AdvanceScript();
                return;
            }

            // 2. The outfit is queued first, then the base character. The viewer keeps a queued
            //    outfit for the newest load, so it goes in with this NPC's scene even when the
            //    previous NPC's upload finishes in between. LoadAsync parses on a worker thread
            //    and returns once the scene is queued for the render callback.
            var identity = new NpcIdentity(npc.Key.ToString(), npc.Title);
            viewer.ApplyMeshOverrides(resolution.Outfit);
            await viewer.LoadAsync(identity, resolution.Paths, externalCt: cts.Token);
            if (generation != _generation) return;
            _parseMs = _loadClock.ElapsedMilliseconds - _resolveMs;
            StatusText.Text = npc.Title + ": uploading...";
            // Viewer_SceneCommitted takes it from here.
        }
        catch (OperationCanceledException)
        {
            _log.Info($"Load of {npc.Title} cancelled after {_loadClock?.ElapsedMilliseconds} ms (another NPC was picked).");
        }
        catch (Exception ex)
        {
            _log.LogError("Could not load " + npc.Title, ex);
            if (generation == _generation)
            {
                StatusText.Text = "Could not load " + npc.Title + ": " + ex.Message;
                AdvanceScript();
            }
        }
    }

    /// <summary>An outfit set went into the scene (the log names the NPC it went on).</summary>
    private void Viewer_MeshOverridesApplied()
        => _log.LogMessage($"  outfit applied to {_viewer?.CurrentLoadedIdentityKey} ({_viewer?.Renderer.Meshes.Count} meshes in the scene).");

    /// <summary>
    /// A scene is installed, with the outfit queued for it (the viewer drains it just before
    /// raising this). Runs on the render callback (the UI thread). The previous NPC's scene can
    /// finish installing while this one's models are still being read; the viewer raises this for
    /// it too, with the outfit still queued for this NPC, so it is skipped here.
    /// </summary>
    private void Viewer_SceneCommitted()
    {
        if (_viewer is not { } viewer || _current is not { } npc) return;
        if (viewer.CurrentLoadedIdentityKey != npc.Key.ToString())
        {
            _log.LogMessage($"  an earlier NPC's scene ({viewer.CurrentLoadedIdentityKey}) finished installing; still loading {npc.Title}.");
            return;
        }
        Dispatcher.BeginInvoke(Ready);
    }

    /// <summary>The NPC and the outfit are on screen: frame them and report.</summary>
    private void Ready()
    {
        if (_viewer is not { } viewer || _current is not { } npc) return;
        if (viewer.CurrentLoadedIdentityKey != npc.Key.ToString()) return;
        FrameCamera();

        var proc = Process.GetCurrentProcess();
        long total = _loadClock?.ElapsedMilliseconds ?? 0;
        int textures = viewer.TextureManager?.OwnedTextureCount ?? 0;
        StatusText.Text = $"{npc.Title} [{npc.WinningPlugin}]: {viewer.Renderer.Meshes.Count} meshes, {textures} textures" +
            (_currentResolution?.OutfitName is { } o ? $", outfit {o}" : ", no outfit") +
            $". Loaded in {total / 1000.0:0.00} s (records {_resolveMs} ms, models {_parseMs} ms, upload {total - _resolveMs - _parseMs} ms).";
        _log.Info(StatusText.Text);
        _log.Info($"  memory: managed {GC.GetTotalMemory(false) / 1048576.0:0} MB, private {proc.PrivateMemorySize64 / 1048576.0:0} MB; " +
            $"{viewer.Renderer.Meshes.Count} meshes in the renderer.");
        // Every shape in the scene, and whether it is drawn: a shape an outfit piece hides (its
        // biped slot is covered) is listed as hidden.
        foreach (var mesh in viewer.Renderer.Meshes)
            _log.LogMessage($"  mesh {mesh.ShapeName} [{mesh.BodyPart}]" + (mesh.ShouldRender ? "" : mesh.IsHiddenByHost ? " (hidden by an outfit piece)" : " (not drawn)"));
        foreach (var m in viewer.MissingMeshPaths) _log.LogMessage("  model not found: " + m);
        foreach (var w in viewer.MeshOverrideWarnings) _log.LogMessage("  outfit: " + w);
        foreach (var t in viewer.MissingTexturePaths.Take(10)) _log.LogMessage("  texture not found: " + t);
        if (_options.Shot != null || _cycle.Count > 0) _settleFrames = 20;
        else if (_options.Exit) Dispatcher.BeginInvoke(Close);
    }

    private static string SlotList(int slots)
        => string.Join(",", Enumerable.Range(0, 32).Where(b => (slots & (1 << b)) != 0).Select(b => (b + 30).ToString()));

    // ---- GL ----

    private void GlControl_Render(TimeSpan delta)
    {
        if (_viewer is not { } viewer) return;
        try
        {
            if (!viewer.IsGlInitialized)
            {
                _log.Info($"OpenGL {GL.GetString(StringName.Version)} on {GL.GetString(StringName.Renderer)} ({GL.GetString(StringName.Vendor)}).");
                viewer.InitializeGl(ModuleResourceLocator.ShaderDirectory);
                _log.Info("Viewer initialised (character render profile).");
            }

            // Installs a loaded scene a slice per frame; frees the previous NPC's meshes and
            // textures first.
            viewer.ProcessPendingScene();

            var (width, height) = ViewSize();
            viewer.Renderer.Render(viewer.Camera, width, height);
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

    /// <summary>The GL control's size in device pixels.</summary>
    private (int Width, int Height) ViewSize()
    {
        var source = PresentationSource.FromVisual(GlControl);
        double sx = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double sy = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        return (Math.Max(1, (int)(GlControl.ActualWidth * sx)), Math.Max(1, (int)(GlControl.ActualHeight * sy)));
    }

    // ---- camera ----

    /// <summary>
    /// Fits the camera to the loaded meshes, seen from the front (NIF models face -Z, so azimuth
    /// 180): the whole figure, or with --portrait the head and hair.
    /// </summary>
    private void FrameCamera()
    {
        if (_viewer is not { IsSceneReady: true } viewer) return;
        var shapes = _options.Portrait
            ? new List<FramingShape>
            {
                new() { Selector = FramingShapeSelector.PrimaryHead.Instance },
                new()
                {
                    Selector = FramingShapeSelector.HeadAccessories.Instance,
                    Filter = FramingShapeFilter.AboveLowerYOfPrimaryHead.Instance,
                },
            }
            : new List<FramingShape> { new() { Selector = FramingShapeSelector.AllLoaded.Instance } };
        var framing = _options.Portrait
            ? new CameraFraming.MeshAware(shapes, FrameTopFraction: 0.95f, FrameBottomFraction: 0.10f)
            : new CameraFraming.MeshAware(shapes, FrameTopFraction: 0.97f, FrameBottomFraction: 0.03f);
        var (width, height) = ViewSize();
        try
        {
            MeshAwareCameraFitter.ApplyTo(viewer, framing, width, height);
        }
        catch (Exception ex)
        {
            _log.LogError("Could not frame the camera", ex);
        }
    }

    private void GlControl_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewer is null) return;
        GlControl.Focus();
        var p = e.GetPosition(GlControl);
        _viewer.Camera.OnMouseDown((float)p.X, (float)p.Y,
            e.LeftButton == MouseButtonState.Pressed, e.MiddleButton == MouseButtonState.Pressed);
        GlControl.CaptureMouse();
    }

    private void GlControl_MouseMove(object sender, MouseEventArgs e)
    {
        if (_viewer is null) return;
        var p = e.GetPosition(GlControl);
        _viewer.Camera.OnMouseMove((float)p.X, (float)p.Y, (float)GlControl.ActualHeight);
    }

    private void GlControl_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _viewer?.Camera.OnMouseUp();
        if (e.LeftButton == MouseButtonState.Released && e.MiddleButton == MouseButtonState.Released)
            GlControl.ReleaseMouseCapture();
    }

    private void GlControl_MouseWheel(object sender, MouseWheelEventArgs e) => _viewer?.Camera.OnMouseWheel(e.Delta);

    private void GlControl_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Home) FrameCamera();
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

    /// <summary>Saves the 3D view (the bound framebuffer of the GL control) as a PNG, unless
    /// --require-outfit is set and the NPC's outfit does not cover the body slot.</summary>
    private void Screenshot(int width, int height)
    {
        if (_options.Shot is { } shot)
        {
            string path = _shotIndex == 0 ? shot
                : Path.Combine(Path.GetDirectoryName(shot)!, Path.GetFileNameWithoutExtension(shot) + $"_{_shotIndex:00}.png");
            _shotIndex++;
            string? skip = null;
            if (_options.RequireOutfit)
            {
                if (_currentResolution is not { OutfitCoversBody: true }) skip = "the outfit has no body-slot piece";
                else if (_viewer is { MeshOverrideWarnings.Count: > 0 }) skip = "an outfit piece did not load";
            }
            if (skip != null)
            {
                _log.Info($"Screenshot {path} skipped for {_current?.Title}: {skip}.");
            }
            else
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
                var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bgr, width * 4);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var file = File.Create(path)) encoder.Save(file);
                _log.Info($"Saved {path} ({width} x {height}) for {_current?.Title}.");
                if (_shotIndex == 1) SaveWindowImage(Path.Combine(Path.GetDirectoryName(shot)!, Path.GetFileNameWithoutExtension(shot) + "_window.png"));
            }
        }
        AdvanceScript();
    }

    /// <summary>The whole window as WPF draws it (panels, list and the 3D view), for documentation.</summary>
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

    /// <summary>Opens the next NPC of --cycle, or closes with --exit.</summary>
    private void AdvanceScript()
    {
        if (_cycle.Count > 0)
        {
            string next = _cycle.Dequeue();
            // "!Npc" starts that load and picks the next NPC 200 ms (--interrupt-ms) later, while
            // it is still loading: a cancellation test.
            bool interrupt = next.StartsWith('!');
            if (interrupt) next = next[1..];
            Dispatcher.BeginInvoke(async () =>
            {
                if (FindNpc(next) is { } npc) SelectAndLoad(npc);
                else { _log.LogError("NPC not found: " + next); AdvanceScript(); return; }
                if (interrupt)
                {
                    await Task.Delay(_options.InterruptMs);
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

    // The viewer keeps the log gate in step with the settings' verbose flag.
    private void VerboseBox_Changed(object sender, RoutedEventArgs e) => _settings.CharacterViewerVerboseLog = VerboseBox.IsChecked == true;

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
