using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SkyRender.World;
using TK = OpenTK.Mathematics.Vector3;

namespace SkyRender.CellDemo;

/// <summary>
/// The GL half of a loaded cell: uploads a <see cref="PreparedScene"/> into the renderer a slice at a
/// time (so the window keeps responding while a large exterior goes in), and owns every GL resource
/// that cell made. <see cref="Dispose"/> removes and frees all of it: the renderer's meshes and this
/// cell's textures. Every call is on the GL thread.
/// </summary>
internal sealed class GlScene : IDisposable
{
    /// <summary>The engine's textures for single-pass projected materials (snow, ash).</summary>
    public const string ProjectedNoise = @"textures\effects\projectednoise.dds";
    public const string ProjectedDiffuse = @"textures\effects\projecteddiffuse.dds";
    /// <summary>The engine default for fProjectedUVDiffuseNormalTilingScale (SkyrimPrefs.ini).</summary>
    private const float ProjectedTiling = 0.5f;

    private readonly GlRenderer _renderer;
    private readonly GameAssetResolver _resolver;
    private readonly IRenderLogger _log;
    private readonly IEnumerator<string> _work;
    private readonly List<PointLight> _lights = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly MaterialBindOptions _bind = new() { RenderMissingTextureAsWireframe = false };
    private bool _disposed;

    public GlScene(GlRenderer renderer, AssetCache assets, GameAssetResolver resolver, IRenderLogger log, PreparedScene scene)
    {
        _renderer = renderer;
        _resolver = resolver;
        _log = log;
        Scene = scene;
        Textures = new GlTextureManager(assets, log);
        Textures.Initialize();

        // The cell's light: weather or cell lighting, image space, fog, sky.
        renderer.ApplyEnvironment(scene.Data.Environment);
        renderer.EnableShadows = scene.Data.SunShadows;
        renderer.ShadowBounds = null;
        renderer.PointLights.Clear();
        foreach (var l in scene.Data.Lights)
            _lights.Add(new PointLight(ReferencePlacement.ToMeshSpace(l.Position), new TK(l.Color.X, l.Color.Y, l.Color.Z), l.Radius)
            {
                CastsShadow = l.CastsShadow,
            });
        _work = Install().GetEnumerator();
    }

    public PreparedScene Scene { get; }
    public GlTextureManager Textures { get; }
    public bool Installed { get; private set; }
    public int MeshCount { get; private set; }
    public int ShapesSkipped { get; private set; }
    public double InstallMs { get; private set; }
    public string Stage { get; private set; } = "starting";

    /// <summary>Runs installation work for about <paramref name="budgetMs"/>. True once everything is in.</summary>
    public bool Step(double budgetMs)
    {
        if (Installed || _disposed) return Installed;
        var slice = Stopwatch.StartNew();
        while (slice.Elapsed.TotalMilliseconds < budgetMs)
        {
            if (!_work.MoveNext())
            {
                Installed = true;
                InstallMs = _clock.Elapsed.TotalMilliseconds;
                break;
            }
            Stage = _work.Current;
        }
        return Installed;
    }

    /// <summary>The installation, one yield per unit of work.</summary>
    private IEnumerable<string> Install()
    {
        var data = Scene.Data;
        foreach (var cell in data.Terrain)
        {
            GlWorldFactory.CreateTerrain(cell, Textures).AddTo(_renderer);
            MeshCount += 4;
            yield return "terrain";
        }

        var ground = new TerrainHeightField(data.Terrain);
        foreach (var surface in data.Water)
        {
            _renderer.AddMesh(GlWorldFactory.CreateWater(surface, ground, Textures));
            MeshCount++;
            yield return "water";
        }

        int done = 0;
        foreach (var placed in data.Models)
        {
            if (Scene.Models.TryGetValue(placed.ModelPath, out var model)) AddModel(placed, model);
            if (++done % 4 == 0) yield return $"objects {done}/{data.Models.Count}";
        }

        if (Scene.Grass is { } placement)
        {
            var grass = GlWorldFactory.CreateGrass(placement, type =>
            {
                if (!Scene.GrassModels.TryGetValue(type.ModelPath, out var shapes)) return null;
                var meshes = new List<GlMesh>();
                foreach (var b in shapes)
                {
                    if (b.Positions.Length == 0 || b.Indices.Length == 0 || b.HasEffectShader) continue;
                    var gm = GlMeshFactory.Create(b);
                    gm.HasVertexColors = b.VertexColors is { Length: > 0 };
                    gm.IsDoubleSided = b.IsDoubleSided;
                    gm.DiffuseTexture = b.TexturePaths.TryGetValue(0, out var d) && d.Length > 0 ? Textures.LoadTexture(d) : Textures.WhiteTexture;
                    // The NIF's own alpha-test threshold; 128 (the usual cutout reference) when it has none.
                    gm.AlphaThreshold = b.HasAlphaTest ? b.AlphaThreshold : 128f / 255f;
                    meshes.Add(gm);
                }
                return meshes;
            });
            grass.AddTo(_renderer);
            MeshCount += grass.Meshes.Count;
            GrassInstances = grass.InstancesDrawn;
            yield return "grass";
        }

        if (Scene.Lod is { Problem: null } lod)
        {
            var glLod = GlWorldFactory.CreateDistantLod(lod, Textures, data.LodWater);
            glLod.AddTo(_renderer);
            MeshCount += glLod.Meshes.Count;
            yield return "distant LOD";
        }
    }

    public int GrassInstances { get; private set; }

    private void AddModel(PlacedModel placed, PreparedModel model)
    {
        var transform = ReferencePlacement.ModelTransform(placed.Position, placed.Rotation, placed.Scale, model.Root);
        foreach (var b in model.Shapes)
        {
            if (b.Positions.Length == 0 || b.Indices.Length == 0) { ShapesSkipped++; continue; }
            // The editor's stand-in shapes (the Creation Kit draws them, the game hides them).
            if (b.ShapeName.StartsWith("EditorMarker", StringComparison.OrdinalIgnoreCase)) { ShapesSkipped++; continue; }
            // A lighting shape with a normal map in its diffuse slot (a heat-shimmer plane, for one)
            // only draws normal-map colours here: the engine refracts through it, SkyRender does not.
            if (!b.HasEffectShader && b.TexturePaths.TryGetValue(0, out var slot0)
                && slot0.EndsWith("_n.dds", StringComparison.OrdinalIgnoreCase)) { ShapesSkipped++; continue; }

            var mesh = GlMeshFactory.Create(b);
            NifMaterialBinder.Bind(Textures, _resolver, mesh, b, b.TexturePaths, _bind);
            mesh.ModelTransform = transform;
            if (placed.Cover is { } c && !b.HasEffectShader)
            {
                mesh.HasProjectedMaterial = true;
                mesh.ProjectedDirection = System.Numerics.Vector3.UnitY;
                // Max angle 90: the angle fold under-covers against the game (judged in game).
                mesh.ProjectedParams = GlMesh.ProjectedParamsFor(c.FalloffScale, c.FalloffBias, c.NoiseUvScale, 90f);
                mesh.ProjectedColor = c.Color;
                mesh.ProjectedDiffuseTiling = ProjectedTiling;
                mesh.ProjectedNoiseTexture = Textures.LoadTexture(ProjectedNoise);
                mesh.ProjectedDiffuseTexture = Textures.LoadTexture(ProjectedDiffuse);
            }
            _renderer.AddMesh(mesh);
            MeshCount++;
        }
    }

    /// <summary>Per frame: the point lights nearest the eye, up to the renderer's limit.</summary>
    public void UpdateLights(TK eye)
    {
        if (_lights.Count == 0) return;
        _renderer.PointLights.Clear();
        if (_lights.Count <= GlRenderer.MaxPointLights) _renderer.PointLights.AddRange(_lights);
        else _renderer.PointLights.AddRange(_lights.OrderBy(l => (l.Position - eye).LengthSquared).Take(GlRenderer.MaxPointLights));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _work.Dispose();
        int meshes = _renderer.Meshes.Count, textures = Textures.OwnedTextureCount;
        _renderer.ClearMeshes();   // disposes every mesh this cell added
        _renderer.PointLights.Clear();
        _renderer.ShadowBounds = null;
        Textures.Dispose();
        _log.LogMessage($"Released {meshes} mesh(es) and {textures} texture(s) of {Scene.Data.Title}.");
    }
}
