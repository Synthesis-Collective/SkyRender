using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SkyRender.World;

namespace SkyRender.CellDemo;

/// <summary>One model, parsed: its shapes as NifMeshBuilder built them and its root-node transform
/// (which a placed reference does not take; see ReferencePlacement).</summary>
internal sealed record PreparedModel(List<NifMeshBuilder.BuiltMesh> Shapes, OpenTK.Mathematics.Matrix4? Root);

/// <summary>A cell selection read and parsed, ready for the GL thread.</summary>
internal sealed class PreparedScene
{
    public required SceneData Data { get; init; }
    public required int Generation { get; init; }
    public Dictionary<string, PreparedModel> Models { get; } = new(StringComparer.OrdinalIgnoreCase);
    public GrassPlacement? Grass { get; set; }
    public Dictionary<string, List<NifMeshBuilder.BuiltMesh>> GrassModels { get; } = new(StringComparer.OrdinalIgnoreCase);
    public DistantLodData? Lod { get; set; }
    public int MissingModels { get; set; }
    /// <summary>Runs from the start of the load.</summary>
    public Stopwatch Clock { get; init; } = Stopwatch.StartNew();
    public List<string> Timings { get; } = new();

    public void Mark(string stage) => Timings.Add($"{stage} {Clock.ElapsedMilliseconds} ms");
}

/// <summary>
/// The GL-free half of loading a cell, on a worker thread: the cell walk, then every distinct model
/// resolved (archives extracted) and parsed, the textures the scene names resolved ahead of the GL
/// thread, grass placed and distant LOD read. Cancellation is checked between steps, so a new
/// selection stops an old load quickly.
/// </summary>
internal sealed class SceneLoader
{
    private readonly GameData _game;
    private readonly AssetCache _assets;
    private readonly IRenderLogger _log;
    private readonly CellWalk _walk;

    public SceneLoader(GameData game, AssetCache assets, IRenderLogger log)
    {
        _game = game;
        _assets = assets;
        _log = log;
        _walk = new CellWalk(game, log) { WeatherOverride = StartupOptions.Current.Weather };
    }

    /// <summary>Distant LOD reaches about 100 cells, the render distance the game is commonly set to
    /// draw everything at; it is also the camera's far plane while LOD is on.</summary>
    public const float LodReach = 409600f;

    public PreparedScene Prepare(CellEntry cell, WorldEntry? world, bool ring, bool lod, int generation,
        Action<string> progress, CancellationToken ct)
    {
        progress("Reading the cell's records...");
        var clock = Stopwatch.StartNew();
        var data = _walk.Build(cell, world, ring, ct);
        var prepared = new PreparedScene { Data = data, Generation = generation, Clock = clock };
        prepared.Mark("records");
        _log.LogMessage($"{data.Title}: {data.Models.Count} placed model(s), {data.Lights.Count} light(s), " +
            $"{data.Terrain.Count} terrain cell(s), {data.Water.Count} water surface(s); " +
            (data.Skipped.Count == 0 ? "nothing skipped" : "skipped " + string.Join(", ", data.Skipped.Select(s => $"{s.Value} {s.Key}"))));
        _log.LogMessage("  light: " + data.EnvironmentText);

        // Models: resolve (which extracts from an archive) on this thread, one at a time, then parse
        // in parallel. NifMeshBuilder's parse is thread-safe and cached by AssetCache.
        var paths = data.Models.Select(m => m.ModelPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var resolved = new List<(string Path, string Disk)>();
        for (int i = 0; i < paths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i % 50 == 0) progress($"Finding models {i}/{paths.Count}...");
            if (_assets.ResolveAssetPath(paths[i]) is { } disk) resolved.Add((paths[i], disk));
            else prepared.MissingModels++;
        }
        prepared.Mark("models found");
        progress($"Reading {resolved.Count} model(s)...");
        var parsed = new ConcurrentDictionary<string, PreparedModel>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(resolved, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
            item =>
            {
                try
                {
                    var shapes = _assets.MeshBuilder.BuildFromFile(item.Disk, ct: ct);
                    parsed[item.Path] = new PreparedModel(shapes, ReferencePlacement.ReadRootTransform(item.Disk, _assets.Resolver));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _log.LogError("Could not read " + item.Path, ex); }
            });
        foreach (var (k, v) in parsed) prepared.Models[k] = v;
        prepared.Mark("models read");

        // Grass, placed on the terrain by the GRAS rules (engine [Grass] defaults).
        if (data.Grass && data.Terrain.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            progress("Growing grass...");
            prepared.Grass = GrassPlacer.Place(data.Terrain, new GrassSettings(),
                c => data.WaterHeights.TryGetValue((c.GridX, c.GridY), out var h) ? h : null);
            foreach (var t in prepared.Grass.Types.Where(t => t.Instances.Count > 0))
            {
                ct.ThrowIfCancellationRequested();
                if (prepared.GrassModels.ContainsKey(t.Type.ModelPath)) continue;
                if (_assets.ResolveAssetPath(t.Type.ModelPath) is not { } disk) continue;
                try { prepared.GrassModels[t.Type.ModelPath] = _assets.MeshBuilder.BuildFromFile(disk, ct: ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _log.LogError("Could not read grass model " + t.Type.ModelPath, ex); }
            }
            prepared.Mark("grass");
        }

        // Distant LOD (opt-in): the blocks the engine would draw from the starting view.
        if (lod && !data.Interior && data.LodWorldspace is { } lodWorld)
        {
            ct.ThrowIfCancellationRequested();
            progress("Reading distant LOD...");
            var c = data.Camera;
            var eye = EyeOf(c);
            prepared.Lod = DistantLodLoader.Load(new DistantLodRequest
            {
                Worldspace = lodWorld,
                Eye = eye,
                LoadedCells = new LoadedCells(data.LoadedCells),
                MaxDistance = LodReach,
            }, _assets.ResolveAssetPath, _assets.Resolver);
            prepared.Mark("LOD");
        }

        // Textures: resolve (extract) every path the scene names, so the GL thread only uploads.
        var textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in prepared.Models.Values)
            foreach (var s in m.Shapes)
            {
                foreach (var t in s.TexturePaths.Values) if (!string.IsNullOrWhiteSpace(t)) textures.Add(t);
                if (s.EffectGreyscaleTexture is { Length: > 0 } g) textures.Add(g);
            }
        foreach (var m in prepared.GrassModels.Values)
            foreach (var s in m)
                if (s.TexturePaths.TryGetValue(0, out var t) && !string.IsNullOrWhiteSpace(t)) textures.Add(t);
        foreach (var land in data.Terrain)
            foreach (var q in land.Quadrants)
            {
                if (q?.Base is { } b) { textures.Add(b.DiffusePath); if (b.NormalPath is { } bn) textures.Add(bn); }
                foreach (var l in q?.Layers ?? Array.Empty<TerrainLayer>())
                {
                    textures.Add(l.Texture.DiffusePath);
                    if (l.Texture.NormalPath is { } ln) textures.Add(ln);
                }
            }
        foreach (var w in data.Water) if (w.Material.NoiseTexturePath is { } n) textures.Add(n);
        if (data.LodWater?.NoiseTexturePath is { } ln2) textures.Add(ln2);
        if (data.Models.Any(m => m.Cover is not null))
        {
            textures.Add(GlScene.ProjectedNoise);
            textures.Add(GlScene.ProjectedDiffuse);
        }
        int done = 0;
        foreach (var t in textures)
        {
            ct.ThrowIfCancellationRequested();
            if (done++ % 100 == 0) progress($"Finding textures {done}/{textures.Count}...");
            _assets.ResolveAssetPath(t);
        }
        prepared.Mark($"{textures.Count} textures found");
        return prepared;
    }

    /// <summary>The camera start's eye, in Skyrim units (the same orbit maths as OrbitCamera, in Z-up).</summary>
    public static System.Numerics.Vector3 EyeOf(CameraStart c)
    {
        var target = ReferencePlacement.ToMeshSpace(c.Target);
        float az = OpenTK.Mathematics.MathHelper.DegreesToRadians(c.Azimuth);
        float el = OpenTK.Mathematics.MathHelper.DegreesToRadians(c.Elevation);
        var offset = new OpenTK.Mathematics.Vector3(MathF.Cos(el) * MathF.Sin(az), MathF.Sin(el), MathF.Cos(el) * MathF.Cos(az)) * c.Distance;
        return ReferencePlacement.ToWorldSpace(target + offset);
    }
}
