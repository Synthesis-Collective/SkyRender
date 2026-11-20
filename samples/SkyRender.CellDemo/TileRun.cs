using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SkyRender.Offscreen;
using SkyRender.World;

namespace SkyRender.CellDemo;

/// <summary>What one tile render asks the builder for: the loaded cells and the camera over them.</summary>
internal sealed record TileScene(PreparedScene Prepared, TopDownCamera Camera);

/// <summary>
/// The scene builder for map tiles. It keeps one <see cref="GlRenderer"/> and one loaded scene (its
/// meshes and textures) across renders: a tile over cells that are already loaded only moves the
/// camera and draws. The scene is replaced when a request names different cells. Everything here
/// runs on the offscreen renderer's render thread.
/// </summary>
internal sealed class TileSceneBuilder : IOffscreenSceneBuilder
{
    private readonly AssetCache _assets;
    private readonly GameAssetResolver _resolver;
    private readonly IRenderLogger _log;
    private GlRenderer? _renderer;
    private GlScene? _scene;

    public TileSceneBuilder(AssetCache assets, GameAssetResolver resolver, IRenderLogger log)
    {
        _assets = assets;
        _resolver = resolver;
        _log = log;
    }

    /// <summary>Milliseconds the last <see cref="Build"/> spent uploading a scene (0 when it was already loaded).</summary>
    public double LastInstallMs { get; private set; }
    public int MeshCount => _scene?.MeshCount ?? 0;
    public int TextureCount => _scene?.Textures.OwnedTextureCount ?? 0;

    public IOffscreenScene Build(OffscreenSceneContext context, OffscreenRenderRequest request)
    {
        var tile = request.Scene as TileScene ?? throw new ArgumentException("The request's Scene must be a TileScene.");
        if (_renderer is null)
        {
            _renderer = new GlRenderer();
            _renderer.Initialize(ModuleResourceLocator.ShaderDirectory);
        }

        LastInstallMs = 0;
        if (_scene is null || !ReferenceEquals(_scene.Scene, tile.Prepared))
        {
            var clock = Stopwatch.StartNew();
            _scene?.Dispose();
            _scene = new GlScene(_renderer, _assets, _resolver, _log, tile.Prepared);
            while (!_scene.Step(1000)) request.Cancellation.ThrowIfCancellationRequested();

            // GlScene set the cell's weather. A map has no weather and no time of day: the same
            // light for every tile, no shadows, no point lights.
            _renderer.ApplyEnvironment(SceneEnvironment.TopDownMap);
            _renderer.EnableShadows = false;
            _renderer.PointLights.Clear();
            LastInstallMs = clock.Elapsed.TotalMilliseconds;
        }
        return new Shared(_renderer, tile.Camera);
    }

    /// <summary>The renderer outlives the render: disposing the scene frees nothing.</summary>
    private sealed class Shared(GlRenderer renderer, OrbitCamera camera) : IOffscreenScene
    {
        public GlRenderer Renderer { get; } = renderer;
        public OrbitCamera Camera { get; } = camera;
        public void Dispose() { }
    }
}

/// <summary>
/// The scripted <c>--tile</c> run: no window. Reads the cells, renders one top-down tile through the
/// offscreen renderer, renders it again with everything loaded, and writes the picture and the
/// timings (FILE.png and FILE.txt).
/// </summary>
internal static class TileRun
{
    public static async Task<int> RunAsync(StartupOptions o)
    {
        var log = CellLog.Instance;
        var inv = CultureInfo.InvariantCulture;
        var report = new List<string>();
        void Say(string line) { report.Add(line); log.Info(line); }
        try
        {
            Say("SkyRender " + SkyRenderInfo.Version + " top-down tile");
            var clock = Stopwatch.StartNew();
            using var game = await Task.Run(() => GameData.Open(o.DataFolder, o.LoadOrderFile, log, CancellationToken.None));
            Say($"Game data ({game.Mods.Count} plugins) opened in {clock.ElapsedMilliseconds} ms (not part of the tile times).");

            var logGate = new RenderLogGate();
            var resolver = new GameAssetResolver(new FixedDataFolder(game.DataFolder),
                new LoadOrderBsaProvider(game.DataFolder, game.Mods.Select(m => m.ModKey)), logGate, log,
                extractionDirectory: Path.Combine(Path.GetTempPath(), "SkyRender.CellDemo", "extracted"));
            var assets = new AssetCache(resolver, log, logGate);
            var loader = new SceneLoader(game, assets, log);

            var world = game.Worlds.FirstOrDefault(w => string.Equals(w.EditorId, o.World ?? "Tamriel", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Worldspace not found: " + o.World);
            var grid = o.Grid ?? throw new InvalidOperationException("--tile needs --grid X,Y.");
            if (!world.ByGrid.TryGetValue(grid, out var cell))
                throw new InvalidOperationException($"No cell at {grid.X},{grid.Y} in {world.EditorId}.");

            // The rectangle: given, or the picked cell. Every cell it touches is loaded.
            float size = TerrainCell.CellSize;
            var rect = o.TileRect ?? (grid.X * size, grid.Y * size, (grid.X + 1) * size, (grid.Y + 1) * size);
            var touched = new List<(int X, int Y)>();
            for (int gy = (int)MathF.Floor(rect.South / size); gy * size < rect.North; gy++)
                for (int gx = (int)MathF.Floor(rect.West / size); gx * size < rect.East; gx++)
                    touched.Add((gx, gy));
            var (floor, ceiling) = o.TileHeight ?? (-40000f, 60000f);
            var camera = new TopDownCamera(rect.West, rect.South, rect.East, rect.North, floor, ceiling);
            var (width, height) = camera.ImageSize(o.TileScale);
            Say($"{world.EditorId}, {touched.Count} cell(s){(o.Ring ? " and the ring around " + cell.Display : "")}: rectangle X {rect.West:0}..{rect.East:0}, Y {rect.South:0}..{rect.North:0}, " +
                $"heights {floor:0}..{ceiling:0}, {o.TileScale.ToString(inv)} units a pixel = {width} x {height} px.");

            var builder = new TileSceneBuilder(assets, resolver, log);
            await using var renderer = OffscreenRendererFactory.Create(builder, assets, resolver, logGate, log);

            OffscreenRenderRequest Request(PreparedScene prepared, RenderTimings timings) => new()
            {
                Width = width,
                Height = height,
                Scene = new TileScene(prepared, camera),
                TransparentBackground = !o.TileOpaque,
                TimingsOut = timings,
            };

            // Cold: nothing read, parsed, decoded or uploaded yet.
            var cold = Stopwatch.StartNew();
            var prepared = await Task.Run(() => loader.Prepare(cell, world, o.Ring, lod: false, generation: 1, _ => { }, CancellationToken.None, touched));
            if (o.TileNoGrass) prepared.Grass = null;
            double readMs = cold.Elapsed.TotalMilliseconds;
            var coldTimings = new RenderTimings();
            byte[] png = await renderer.RenderToPngAsync(Request(prepared, coldTimings));
            double coldMs = cold.Elapsed.TotalMilliseconds;
            Say($"Cold: {coldMs:0} ms = read and parse {readMs:0} ms ({string.Join(", ", prepared.Timings)}) + upload {builder.LastInstallMs:0} ms " +
                $"+ draw {coldTimings.DrawMs:0.0} ms + readback {coldTimings.ReadbackMs:0.0} ms + PNG {coldTimings.EncodeMs:0.0} ms.");
            Say($"Scene: {prepared.Data.Models.Count} placed objects, {builder.MeshCount} meshes, {builder.TextureCount} textures" +
                $"{(prepared.MissingModels > 0 ? $", {prepared.MissingModels} model(s) not found" : "")}.");

            // Warm: the same scene stays loaded; only the draw, the readback and the encode repeat.
            var warm = new List<(double Total, RenderTimings T)>();
            for (int i = 0; i < Math.Max(1, o.TileRepeat); i++)
            {
                var t = new RenderTimings();
                var w = Stopwatch.StartNew();
                png = await renderer.RenderToPngAsync(Request(prepared, t));
                warm.Add((w.Elapsed.TotalMilliseconds, t));
            }
            // The same without the PNG encode: the BGRA32 buffer a host can show directly.
            var raw = new List<double>();
            for (int i = 0; i < Math.Max(1, o.TileRepeat); i++)
            {
                var w = Stopwatch.StartNew();
                await renderer.RenderToBgra32Async(Request(prepared, new RenderTimings()));
                raw.Add(w.Elapsed.TotalMilliseconds);
            }
            raw.Sort();
            warm.Sort((a, b) => a.Total.CompareTo(b.Total));
            var median = warm[warm.Count / 2];
            Say($"Warm ({warm.Count} renders): median {median.Total:0.0} ms (draw {median.T.DrawMs:0.0}, readback {median.T.ReadbackMs:0.0}, PNG {median.T.EncodeMs:0.0}), " +
                $"fastest {warm[0].Total:0.0} ms, slowest {warm[^1].Total:0.0} ms. As BGRA32, no PNG: median {raw[raw.Count / 2]:0.0} ms.");

            string file = o.Tile!;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, png);
            Say($"Saved {file} ({png.Length / 1024.0:0} KB).");
            File.WriteAllLines(Path.ChangeExtension(file, ".txt"), report);
            return 0;
        }
        catch (Exception ex)
        {
            log.LogError("The tile run failed", ex);
            report.Add("FAILED: " + ex);
            if (o.Tile is { } f)
            {
                try { File.WriteAllLines(Path.ChangeExtension(f, ".txt"), report); } catch { /* the log has it */ }
            }
            return 1;
        }
    }
}
