using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyRender.World;
using TK = OpenTK.Mathematics.Vector3;

namespace SkyRender.CellDemo;

/// <summary>
/// The cell walk: reads one cell selection out of the load order into a <see cref="SceneData"/>.
/// <list type="bullet">
/// <item><b>References.</b> Every copy of each drawn cell, in load order, contributes the placed
/// objects (REFR) filed in it; a later copy of a reference replaces an earlier one and a deleted copy
/// removes it. For an exterior, the worldspace's persistent references (filed in its persistent cell)
/// join by position. NPCs and creatures (ACHR) are not read. References that start disabled, or
/// whose enable parent hides them, are left out, as are editor markers.</item>
/// <item><b>Base objects</b> come from the link cache (the winning record): their model, a light's
/// colour and radius, a static's projected material.</item>
/// <item><b>Terrain, water and grass</b> (exteriors): the winning LAND of each drawn cell with its
/// texture layers (LTEX -> TXST) and their grasses (LTEX -> GRAS); the cell's water (its own height
/// and type, else the worldspace's).</item>
/// <item><b>Light.</b> An exterior takes a weather: the most likely one of the cell's region, else of
/// the worldspace's climate, else SkyrimClear, in its day colours and image space, with the sun where
/// the climate puts it at noon. An interior takes its own lighting (XCLL, with what it inherits from
/// its lighting template), its image space and its placed lights.</item>
/// </list>
/// Values are copied out of the records; nothing here holds a Mutagen object after it returns.
/// </summary>
internal sealed class CellWalk
{
    // Record header flags (the CK's names): Deleted 0x20, Initially Disabled 0x800, Is Marker 0x800000.
    private const int InitiallyDisabledFlag = 0x800;
    private const int IsMarkerFlag = 0x00800000;
    /// <summary>The engine's default landscape texture (sDefaultLandDiffuseTexture): what it draws
    /// for a quadrant with no base layer and for a layer whose texture is zero.</summary>
    private const string DefaultLandDiffuse = @"textures\landscape\dirt02.dds";
    /// <summary>The weather used when neither region nor climate names one.</summary>
    private const string FallbackWeather = "SkyrimClear";
    /// <summary>The hour the sun is placed for.</summary>
    private const float GameHour = 12f;
    /// <summary>The adapted luminance the image space's contrast pivots on: one frame has no eye
    /// adaptation history, so the value read at SkyrimClear noon stands in (SceneEnvironment).</summary>
    private const float AdaptedLuminance = 0.367f;
    /// <summary>The vertical field of view the game draws with at its default 80 degrees (16:9).</summary>
    public const float GameFieldOfView = 50.534f;

    private readonly GameData _game;
    private readonly IRenderLogger _log;
    // Per worldspace: its persistent references (winner per FormKey), built on first use.
    private readonly Dictionary<FormKey, Dictionary<FormKey, RefCopy>> _persistentByWorld = new();

    /// <summary>A weather (EditorID) to use for every exterior instead of the region's or climate's.</summary>
    public string? WeatherOverride { get; set; }

    public CellWalk(GameData game, IRenderLogger log)
    {
        _game = game;
        _log = log;
    }

    /// <summary>One placed reference's values as its winning copy states them.</summary>
    private sealed record RefCopy(FormKey Key, FormKey Base, Vector3 Position, Vector3 Rotation, float Scale,
        bool InitiallyDisabled, FormKey? EnableParent, bool OppositeOfParent, float FadeOffset, float RadiusOffset);

    public SceneData Build(CellEntry target, WorldEntry? world, bool ring, CancellationToken ct)
    {
        var copies = _game.CellCopies(target.Key);
        if (copies.Count == 0) throw new InvalidOperationException("The cell is not in the load order: " + target.Display);
        var winner = copies[^1].Cell;

        // ---- which cells ---------------------------------------------------------------------
        var cells = new List<CellEntry> { target };
        if (!target.IsInterior && ring && world is not null && target.Grid is { } tg)
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if ((dx != 0 || dy != 0) && world.ByGrid.TryGetValue((tg.X + dx, tg.Y + dy), out var n))
                        cells.Add(n);

        var scene = new SceneData
        {
            Title = target.Display,
            Interior = target.IsInterior,
            WinningPlugin = target.WinningPlugin,
            Environment = SceneEnvironment.VanillaClearDay,
        };

        // ---- references ----------------------------------------------------------------------
        var refs = new Dictionary<FormKey, RefCopy>();
        foreach (var cell in cells)
            foreach (var (_, copy) in _game.CellCopies(cell.Key))
            {
                ct.ThrowIfCancellationRequested();
                TakeRefs(copy.Temporary, refs);
                TakeRefs(copy.Persistent, refs);
            }
        if (!target.IsInterior && world is not null)
        {
            var squares = cells.Where(c => c.Grid is not null).Select(c => c.Grid!.Value).ToHashSet();
            foreach (var (key, r) in Persistent(world.Key, ct))
                if (!refs.ContainsKey(key) && squares.Contains(GridOf(r.Position)))
                    refs[key] = r;
            scene.LoadedCells.AddRange(squares);
        }

        var lights = new List<(RefCopy Ref, ILightGetter Light)>();
        (Vector3 Position, float Heading)? entry = null, doorEntry = null;
        var coverCache = new Dictionary<FormKey, ProjectedCover?>();
        foreach (var r in refs.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsEnabled(r, refs, 0)) { scene.Skip("disabled at start"); continue; }
            var baseRec = _game.Resolve<IPlaceableObjectGetter>(r.Base);
            if (baseRec is null) { scene.Skip("base object not found (or not a placeable object)"); continue; }
            if ((baseRec.MajorRecordFlagsRaw & IsMarkerFlag) != 0)
            {
                // The marker "coc" puts the player at: the best place to start an interior's view.
                if (string.Equals(baseRec.EditorID, "COCMarkerHeading", StringComparison.OrdinalIgnoreCase)) entry ??= (r.Position, r.Rotation.Z);
                scene.Skip("editor marker");
                continue;
            }
            if (baseRec is IDoorGetter && doorEntry is null && _teleports.Contains(r.Key)) doorEntry = (r.Position, r.Rotation.Z);
            if (baseRec is ILightGetter light) lights.Add((r, light));
            if (baseRec is not IModeledGetter modeled || modeled.Model?.File.GivenPath is not { Length: > 0 } model)
            {
                if (baseRec is not ILightGetter) scene.Skip("no model");
                continue;
            }
            ProjectedCover? cover = null;
            if (baseRec is IStaticGetter stat && !stat.Material.IsNull)
            {
                if (!coverCache.TryGetValue(stat.Material.FormKey, out cover))
                    coverCache[stat.Material.FormKey] = cover = Cover(stat.Material.FormKey);
            }
            scene.Models.Add(new PlacedModel(MeshPath(model), r.Position, r.Rotation, r.Scale,
                baseRec.EditorID ?? baseRec.FormKey.ToString(), cover));
        }

        if (target.IsInterior)
        {
            InteriorLight(scene, winner, lights);
            scene.Camera = InteriorCamera(scene, entry, doorEntry);
        }
        else if (world is not null)
        {
            var wrld = WinningWorld(world.Key);
            foreach (var cell in cells) Terrain(scene, cell, wrld, ct);
            scene.Grass = wrld is null || !wrld.Flags.HasFlag(Worldspace.Flag.NoGrass);
            ExteriorLight(scene, winner, wrld);
            Lod(scene, world.Key);
            scene.Camera = ExteriorCamera(scene, target, ring);
        }
        return scene;
    }

    // ---- references -----------------------------------------------------------------------------

    // References that are load doors (they carry a teleport destination), from the copies read.
    private readonly HashSet<FormKey> _teleports = new();

    private void TakeRefs(IReadOnlyList<IPlacedGetter>? list, Dictionary<FormKey, RefCopy> refs)
    {
        if (list is null) return;
        foreach (var placed in list)
        {
            if (placed is not IPlacedObjectGetter po) continue;   // NPCs, hazards, projectiles: not drawn
            if (po.IsDeleted || po.Placement is null || po.Base.FormKeyNullable is not { IsNull: false } baseKey)
            {
                refs.Remove(po.FormKey);
                continue;
            }
            refs[po.FormKey] = Copy(po, baseKey);
            if (po.TeleportDestination is not null) _teleports.Add(po.FormKey);
        }
    }

    private static RefCopy Copy(IPlacedObjectGetter po, FormKey baseKey)
    {
        var p = po.Placement!;
        var parent = po.EnableParent;
        return new RefCopy(po.FormKey, baseKey,
            new Vector3(p.Position.X, p.Position.Y, p.Position.Z),
            new Vector3(p.Rotation.X, p.Rotation.Y, p.Rotation.Z),
            po.Scale ?? 1f,
            (po.MajorRecordFlagsRaw & InitiallyDisabledFlag) != 0,
            parent is not null && !parent.Reference.IsNull ? parent.Reference.FormKey : null,
            parent is not null && parent.Flags.HasFlag(EnableParent.Flag.SetEnableStateToOppositeOfParent),
            po.LightData?.FadeOffset ?? 0f, po.Radius ?? 0f);
    }

    /// <summary>The worldspace's persistent references (filed in its persistent cell, in any copy),
    /// winner per FormKey. The game loads them with the worldspace; a cell draws the ones that stand
    /// in it.</summary>
    private Dictionary<FormKey, RefCopy> Persistent(FormKey world, CancellationToken ct)
    {
        if (_persistentByWorld.TryGetValue(world, out var cached)) return cached;
        var refs = new Dictionary<FormKey, RefCopy>();
        foreach (var (_, w) in _game.WorldCopies(world))
        {
            ct.ThrowIfCancellationRequested();
            if (w.TopCell is { } top)
            {
                TakeRefs(top.Persistent, refs);
                TakeRefs(top.Temporary, refs);
            }
        }
        _persistentByWorld[world] = refs;
        return refs;
    }

    /// <summary>Whether a reference is enabled when the game starts: not flagged initially disabled,
    /// and its enable parent (when it is one of the references read here) enabled, or disabled for a
    /// reference set to the opposite of its parent. A parent this walk did not read counts as
    /// enabled.</summary>
    private static bool IsEnabled(RefCopy r, Dictionary<FormKey, RefCopy> refs, int depth)
    {
        if (r.InitiallyDisabled) return false;
        if (r.EnableParent is not { } pk || depth > 8 || !refs.TryGetValue(pk, out var parent)) return true;
        bool parentEnabled = IsEnabled(parent, refs, depth + 1);
        return r.OppositeOfParent ? !parentEnabled : parentEnabled;
    }

    private ProjectedCover? Cover(FormKey mato)
    {
        // Only a single-pass material draws in the base shape's own shader; the others are separate
        // passes this sample does not draw.
        var m = _game.Resolve<IMaterialObjectGetter>(mato);
        if (m is null || !m.Flags.HasFlag(MaterialObject.Flag.SinglePass)) return null;
        return new ProjectedCover(m.FalloffScale, m.FalloffBias, m.NoiseUvScale, Rgb(m.SinglePassColor));
    }

    // ---- terrain, water, grass ------------------------------------------------------------------

    private void Terrain(SceneData scene, CellEntry cell, IWorldspaceGetter? world, CancellationToken ct)
    {
        if (cell.Grid is not { } g) return;
        var copies = _game.CellCopies(cell.Key);
        ILandscapeGetter? land = null;
        foreach (var (_, copy) in copies)
        {
            // A copy without LAND keeps the earlier terrain; a LAND that declares no heights removes it.
            if (copy.Landscape is not { } l) continue;
            land = l.VertexHeightMap is null && !(l.Flags ?? 0).HasFlag(Landscape.Flag.VertexNormalsHeightMap) ? null : l;
        }
        var winner = copies[^1].Cell;
        WaterFor(scene, winner, world, g);
        if (land?.VertexHeightMap is not { } vh) return;
        ct.ThrowIfCancellationRequested();

        const int side = TerrainCell.Side;
        var gradients = new sbyte[side * side];
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
                gradients[y * side + x] = vh.HeightMap[x, y];
        var terrain = new TerrainCell(g.X, g.Y, LandHeightDecoder.Decode(vh.Offset, gradients))
        {
            Normals = land.VertexNormals is { } vn ? TerrainCell.DecodeNormals(Bytes(vn, p => (sbyte)p.X, p => (sbyte)p.Y, p => (sbyte)p.Z)) : null,
            Colors = land.VertexColors is { } vc ? TerrainCell.DecodeColors(Bytes(vc, p => p.X, p => p.Y, p => p.Z)) : null,
        };

        // Each quadrant: its base layer (BTXT) and its alpha layers (ATXT + VTXT) in layer order.
        var baseTex = new FormKey?[4];
        var hasBase = new bool[4];
        var alpha = new List<(int Quadrant, int Layer, FormKey? Tex, float[] Opacity)>();
        try
        {
            foreach (var layer in land.Layers)
            {
                if (layer.Header is not { } h) continue;
                int q = (int)h.Quadrant;
                if (q is < 0 or > 3) continue;
                FormKey? tex = h.Texture.IsNull ? null : h.Texture.FormKey;
                if (layer is IAlphaLayerGetter al)
                {
                    var op = new float[TerrainCell.QuadrantSide * TerrainCell.QuadrantSide];
                    // An ATXT with no VTXT reads back without data: it paints nothing.
                    foreach (var pt in (IEnumerable<IAlphaLayerDataGetter>?)al.AlphaLayerData ?? Array.Empty<IAlphaLayerDataGetter>())
                        if (pt.Position < op.Length) op[pt.Position] = pt.Opacity;
                    alpha.Add((q, h.LayerNumber, tex, op));
                }
                else if (!hasBase[q])
                {
                    hasBase[q] = true;
                    baseTex[q] = tex;
                }
            }
        }
        catch (Exception ex)
        {
            // A damaged layer changes paint, not ground: keep the layers read before it.
            _log.LogError($"Terrain layers of cell ({g.X}, {g.Y}) could not all be read", ex);
        }

        for (int q = 0; q < 4; q++)
        {
            terrain.Quadrants[q] = new TerrainQuadrant
            {
                Base = Texture(baseTex[q]),
                Layers = alpha.Where(a => a.Quadrant == q).OrderBy(a => a.Layer)
                    .Select(a => new TerrainLayer(Texture(a.Tex), a.Opacity)).ToList(),
            };
        }
        scene.Terrain.Add(terrain);
    }

    private readonly Dictionary<FormKey, TerrainTexture> _landTextures = new();
    private readonly Dictionary<FormKey, GrassType?> _grasses = new();

    /// <summary>A landscape texture (LTEX) as its diffuse, normal map and grasses; a missing or zero
    /// reference is the engine's default ground.</summary>
    private TerrainTexture Texture(FormKey? ltex)
    {
        if (ltex is not { } key) return new TerrainTexture(DefaultLandDiffuse);
        if (_landTextures.TryGetValue(key, out var cached)) return cached;
        var lt = _game.Resolve<ILandscapeTextureGetter>(key);
        var ts = lt?.TextureSet.FormKeyNullable is { IsNull: false } tk ? _game.Resolve<ITextureSetGetter>(tk) : null;
        string diffuse = TexturePath(ts?.Diffuse?.GivenPath) ?? DefaultLandDiffuse;
        string? normal = TexturePath(ts?.NormalOrGloss?.GivenPath);
        var grasses = new List<GrassType>();
        if (lt is not null)
            foreach (var link in lt.Grasses)
                if (!link.IsNull && Grass(link.FormKey) is { } gt) grasses.Add(gt);
        var tex = new TerrainTexture(diffuse, normal) { Grasses = grasses.Count > 0 ? grasses : null };
        _landTextures[key] = tex;
        return tex;
    }

    private GrassType? Grass(FormKey key)
    {
        if (_grasses.TryGetValue(key, out var cached)) return cached;
        GrassType? type = null;
        if (_game.Resolve<IGrassGetter>(key) is { } g && g.Model?.File.GivenPath is { Length: > 0 } model)
        {
            type = new GrassType
            {
                Id = StableId(key),
                ModelPath = MeshPath(model),
                Density = g.Density,
                MinSlope = g.MinSlope,
                MaxSlope = g.MaxSlope,
                UnitsFromWater = g.UnitsFromWater,
                WaterRule = (GrassWaterRule)(int)g.UnitsFromWaterType,
                PositionRange = g.PositionRange,
                HeightRange = g.HeightRange,
                ColorRange = g.ColorRange,
                WavePeriod = g.WavePeriod,
                Flags = (g.Flags.HasFlag(Mutagen.Bethesda.Skyrim.Grass.Flag.VertexLighting) ? GrassFlags.VertexLighting : 0)
                      | (g.Flags.HasFlag(Mutagen.Bethesda.Skyrim.Grass.Flag.UniformScaling) ? GrassFlags.UniformScaling : 0)
                      | (g.Flags.HasFlag(Mutagen.Bethesda.Skyrim.Grass.Flag.FitToSlope) ? GrassFlags.FitToSlope : 0),
            };
        }
        _grasses[key] = type;
        return type;
    }

    /// <summary>A FormKey as a 32-bit seed for the grass placement: its ID mixed with its plugin's name.</summary>
    private static uint StableId(FormKey k)
    {
        uint h = 2166136261u;
        foreach (char c in k.ModKey.FileName.String.ToLowerInvariant()) { h ^= c; h *= 16777619u; }
        return h ^ k.ID;
    }

    private void WaterFor(SceneData scene, ICellGetter cell, IWorldspaceGetter? world, (int X, int Y) g)
    {
        if (!cell.Flags.HasFlag(Cell.Flag.HasWater)) return;
        float? defaultHeight = world?.LandDefaults?.DefaultWaterHeight;
        if (SurfaceHeight(cell.WaterHeight, defaultHeight) is not { } z) return;
        var waterKey = cell.Water.FormKeyNullable is { IsNull: false } cw ? cw
                     : world?.Water.FormKeyNullable is { IsNull: false } ww ? ww : (FormKey?)null;
        var material = waterKey is { } wk ? WaterMaterialOf(wk) : null;
        material ??= new WaterMaterial(new Vector3(0.14f, 0.25f, 0.25f), new Vector3(0.03f, 0.13f, 0.13f),
            new Vector3(0.3f, 0.3f, 0.3f), 0.4f, 0.07f) { ReflectsSky = true };
        float x0 = g.X * TerrainCell.CellSize, y0 = g.Y * TerrainCell.CellSize;
        scene.Water.Add(new WaterSurface(z, x0, y0, x0 + TerrainCell.CellSize, y0 + TerrainCell.CellSize, material)
        {
            Label = $"water ({g.X}, {g.Y})",
        });
        scene.WaterHeights[g] = z;
    }

    /// <summary>
    /// The engine's reading of a cell's water height: a real height is used; unset (or the
    /// float-max / wrapped sentinels) defers to the worldspace default; the negative sentinels place
    /// no surface. (Sentinel behaviour measured in game; the CK writes them for "no water height".)
    /// </summary>
    private static float? SurfaceHeight(float? h, float? worldDefault)
    {
        static bool Real(float? v) => v is { } f && !float.IsNaN(f) && MathF.Abs(f) < 100_000f;
        if (Real(h)) return h;
        bool defers = h is null || h == float.MaxValue || h is > 4.0e9f and < 4.3e9f;
        return defers && Real(worldDefault) ? worldDefault : null;
    }

    /// <summary>A water type (WATR) as SkyRender's water material: colours, Fresnel, noise layer one
    /// as the wave normal map, the above-water fog, the sun specular. The sky is reflected.</summary>
    private WaterMaterial? WaterMaterialOf(FormKey key)
    {
        if (_game.Resolve<IWaterGetter>(key) is not { } w) return null;
        return new WaterMaterial(Rgb(w.ShallowColor), Rgb(w.DeepColor), Rgb(w.ReflectionColor), w.WaterReflectivity, w.WaterFresnel)
        {
            NoiseTexturePath = TexturePath(w.NoiseLayerOneTexture?.GivenPath),
            NoiseUvScale = w.NoiseLayerOneUvScale,
            ReflectsSky = true,
            NoiseFalloff = w.NoiseFalloff,
            DepthFog = w.FogAboveWaterDistanceFarPlane > 0f
                ? new WaterDepthFog(w.FogAboveWaterDistanceNearPlane, w.FogAboveWaterDistanceFarPlane, w.FogAboveWaterAmount)
                : null,
            SunSpecularPower = w.SpecularSunPower,
            SunSpecularMagnitude = w.SpecularSunSpecularMagnitude,
            DepthSpecularLighting = w.DepthSpecularLighting,
        };
    }

    // ---- light ----------------------------------------------------------------------------------

    private IWorldspaceGetter? WinningWorld(FormKey key) => _game.WorldCopies(key) is { Count: > 0 } c ? c[^1].World : null;

    /// <summary>A worldspace, or its parent when it takes the parent's data of a kind
    /// (<paramref name="flag"/>), followed up to four steps.</summary>
    private IWorldspaceGetter? DataSource(IWorldspaceGetter? w, WorldspaceParent.Flag flag)
    {
        for (int i = 0; i < 4 && w is not null; i++)
        {
            if (w.Parent is { } p && p.Flags.HasFlag(flag) && !p.Worldspace.IsNull
                && WinningWorld(p.Worldspace.FormKey) is { } parent)
            {
                w = parent;
                continue;
            }
            return w;
        }
        return w;
    }

    private void ExteriorLight(SceneData scene, ICellGetter cell, IWorldspaceGetter? world)
    {
        // The weather: the cell's highest-priority region that has weather, its most likely entry;
        // else the climate's most likely weather; else SkyrimClear.
        IWeatherGetter? weather = null;
        string why = "";
        if (WeatherOverride is { } named && _game.Links.TryResolve<IWeatherGetter>(named, out var chosen))
        {
            weather = chosen;
            why = "chosen";
        }
        var region = cell.Regions
            .Select(r => r.IsNull ? null : _game.Resolve<IRegionGetter>(r.FormKey))
            .Where(r => r?.Weather?.Weathers is { Count: > 0 })
            .OrderByDescending(r => r!.Weather!.Priority).FirstOrDefault();
        if (weather is null && region is not null)
            foreach (var e in region.Weather!.Weathers.OrderByDescending(e => e.Chance))
                if (!e.Weather.IsNull && _game.Resolve<IWeatherGetter>(e.Weather.FormKey) is { } wr)
                {
                    weather = wr;
                    why = $"region {region.EditorID}'s most likely weather";
                    break;
                }
        var climateWorld = DataSource(world, WorldspaceParent.Flag.UseClimateData);
        var climate = climateWorld?.Climate.FormKeyNullable is { IsNull: false } ck ? _game.Resolve<IClimateGetter>(ck) : null;
        if (weather is null && climate is not null)
            foreach (var e in climate.WeatherTypes.OrderByDescending(e => e.Chance))
                if (!e.Weather.IsNull && _game.Resolve<IWeatherGetter>(e.Weather.FormKey) is { } wc)
                {
                    weather = wc;
                    why = $"climate {climate.EditorID}'s most likely weather";
                    break;
                }
        if (weather is null && _game.Links.TryResolve<IWeatherGetter>(FallbackWeather, out var fallback))
        {
            weather = fallback;
            why = "the fallback weather";
        }

        (float Azimuth, float Elevation)? sun = null;
        if (climate is not null)
        {
            static float H(TimeOnly t) => t.Hour + t.Minute / 60f;
            sun = SceneEnvironment.SunDirection(H(climate.SunriseBegin), H(climate.SunriseEnd),
                H(climate.SunsetBegin), H(climate.SunsetEnd), GameHour);
        }
        var clear = SceneEnvironment.VanillaClearDay;
        sun ??= (clear.SunAzimuthDegrees, clear.SunElevationDegrees);
        scene.SunShadows = true;
        if (weather is null)
        {
            scene.Environment = clear with { SunAzimuthDegrees = sun.Value.Azimuth, SunElevationDegrees = sun.Value.Elevation };
            scene.EnvironmentText = "no weather found: vanilla clear day";
            return;
        }

        var image = weather.ImageSpaces is { } isp && !isp.Day.IsNull ? _game.Resolve<IImageSpaceGetter>(isp.Day.FormKey) : null;
        float skyScale = image?.Hdr?.SkyScale ?? 0f;
        float fogMax = weather.FogDistanceDayMax;
        var gradient = SkyGradient.FromSkyDome(Tk(weather.HorizonColor.Day), Tk(weather.SkyLowerColor.Day),
            Tk(weather.SkyUpperColor.Day), Tk(weather.FogFarColor.Day), fogMax, skyScale);
        // The ambient on an up-facing surface: the directional ambient's Z- colour (the light
        // travelling from the sky down).
        var ambient = weather.DirectionalAmbientLightingColors?.Day is { } dalc ? Tk(dalc.DirectionalZMinus) : clear.AmbientColor;
        scene.Environment = new SceneEnvironment(
            Background: gradient.At(20f),
            SkyColor: Tk(weather.SkyUpperColor.Day),
            SkyGradient: gradient,
            SunAzimuthDegrees: sun.Value.Azimuth,
            SunElevationDegrees: sun.Value.Elevation,
            SunColor: Tk(weather.SunlightColor.Day),
            SunIntensity: image?.Hdr?.SunlightScale ?? 1f,
            AmbientColor: ambient,
            Fog: weather.FogDistanceDayFar > weather.FogDistanceDayNear
                ? new DistanceFog(Tk(weather.FogNearColor.Day), Tk(weather.FogFarColor.Day), weather.FogDistanceDayNear,
                    weather.FogDistanceDayFar, weather.FogDistanceDayPower, fogMax)
                : null,
            ImageSpace: ImageSpaceOf(image));
        scene.EnvironmentText = $"weather {weather.EditorID} (day; {why}), image space {image?.EditorID ?? "none"}, " +
            $"sun at azimuth {sun.Value.Azimuth:0.#} elevation {sun.Value.Elevation:0.#}" +
            (climate is null ? " (no climate: the clear-day sun)" : $" ({climate.EditorID} at {GameHour:0}:00)");
    }

    private static ImageSpaceFinish? ImageSpaceOf(IImageSpaceGetter? i)
    {
        if (i is null) return null;
        var h = i.Hdr; var c = i.Cinematic; var t = i.Tint;
        return new ImageSpaceFinish(h?.White ?? 1f, c?.Saturation ?? 1f, c?.Brightness ?? 1f, c?.Contrast ?? 1f,
            t is null ? TK.One : Tk(t.Color), t?.Amount ?? 0f, AdaptedLuminance);
    }

    /// <summary>One XCLL or lighting template's values.</summary>
    private sealed record Lighting(TK Ambient, TK Directional, int RotXY, int RotZ, float Fade,
        TK FogNear, TK FogFar, float FogNearDistance, float FogFarDistance, float FogPower, float FogMax);

    private void InteriorLight(SceneData scene, ICellGetter cell, List<(RefCopy Ref, ILightGetter Light)> lights)
    {
        var template = cell.LightingTemplate.IsNull ? null : _game.Resolve<ILightingTemplateGetter>(cell.LightingTemplate.FormKey);
        Lighting? t = template is null ? null : new Lighting(Tk(template.AmbientColor), Tk(template.DirectionalColor),
            template.DirectionalRotationXY, template.DirectionalRotationZ, template.DirectionalFade,
            Tk(template.FogNearColor), Tk(template.FogFarColor), template.FogNear, template.FogFar, template.FogPower, template.FogMax);
        Lighting? v = null;
        if (cell.Lighting is { } x)
        {
            v = new Lighting(Tk(x.AmbientColor), Tk(x.DirectionalColor), x.DirectionalRotationXY, x.DirectionalRotationZ,
                x.DirectionalFade, Tk(x.FogNearColor), Tk(x.FogFarColor), x.FogNear, x.FogFar, x.FogPower, x.FogMax);
            if (t is not null)
            {
                // Each value the cell marks as inherited comes from its lighting template.
                var inh = x.Inherits;
                bool I(Mutagen.Bethesda.Skyrim.CellLighting.Inherit f) => inh.HasFlag(f);
                v = v with
                {
                    Ambient = I(Mutagen.Bethesda.Skyrim.CellLighting.Inherit.AmbientColor) ? t.Ambient : v.Ambient,
                    Directional = I(Mutagen.Bethesda.Skyrim.CellLighting.Inherit.DirectionalColor) ? t.Directional : v.Directional,
                    RotXY = I(Mutagen.Bethesda.Skyrim.CellLighting.Inherit.DirectionalRotation) ? t.RotXY : v.RotXY,
                    RotZ = I(Mutagen.Bethesda.Skyrim.CellLighting.Inherit.DirectionalRotation) ? t.RotZ : v.RotZ,
                    Fade = I(Mutagen.Bethesda.Skyrim.CellLighting.Inherit.DirectionalFade) ? t.Fade : v.Fade,
                    FogNear = I(Mutagen.Bethesda.Skyrim.CellLighting.Inherit.FogColor) ? t.FogNear : v.FogNear,
                    FogFar = I(Mutagen.Bethesda.Skyrim.CellLighting.Inherit.FogColor) ? t.FogFar : v.FogFar,
                    FogNearDistance = I(Mutagen.Bethesda.Skyrim.CellLighting.Inherit.FogNear) ? t.FogNearDistance : v.FogNearDistance,
                    FogFarDistance = I(Mutagen.Bethesda.Skyrim.CellLighting.Inherit.FogFar) ? t.FogFarDistance : v.FogFarDistance,
                    FogPower = I(Mutagen.Bethesda.Skyrim.CellLighting.Inherit.FogPower) ? t.FogPower : v.FogPower,
                    FogMax = I(Mutagen.Bethesda.Skyrim.CellLighting.Inherit.FogMax) ? t.FogMax : v.FogMax,
                };
            }
        }
        v ??= t;
        var image = cell.ImageSpace.FormKeyNullable is { IsNull: false } ik ? _game.Resolve<IImageSpaceGetter>(ik) : null;
        if (v is null)
        {
            scene.Environment = new SceneEnvironment(new TK(0.05f), new TK(0.05f), null, 135f, 55f, new TK(0.5f), 0.5f,
                new TK(0.35f), null, ImageSpaceOf(image));
            scene.EnvironmentText = "the cell has no lighting data: a neutral dim light";
        }
        else
        {
            scene.Environment = new SceneEnvironment(
                Background: v.FogFar,
                SkyColor: v.FogFar,
                SkyGradient: null,
                SunAzimuthDegrees: v.RotXY,
                SunElevationDegrees: v.RotZ,
                SunColor: v.Directional,
                SunIntensity: v.Fade,
                AmbientColor: v.Ambient,
                Fog: v.FogFarDistance > v.FogNearDistance
                    ? new DistanceFog(v.FogNear, v.FogFar, v.FogNearDistance, v.FogFarDistance,
                        v.FogPower > 0f ? v.FogPower : 1f, v.FogMax > 0f ? v.FogMax : 1f)
                    : null,
                ImageSpace: ImageSpaceOf(image));
            scene.EnvironmentText = $"cell lighting: ambient {v.Ambient * 255f:0}, directional {v.Directional * 255f:0} x {v.Fade:0.##}" +
                $"{(template is null ? "" : $", template {template.EditorID}")}; image space {image?.EditorID ?? "none"}";
        }

        // Placed lights: the base's colour times its fade (plus the reference's fade offset), its
        // radius plus the reference's radius offset; negative lights subtract.
        foreach (var (r, l) in lights)
        {
            float fade = MathF.Max(l.FadeValue + r.FadeOffset, 0f);
            float radius = MathF.Max(l.Radius + r.RadiusOffset, 0f);
            if (radius <= 0f || fade <= 0f) continue;
            var color = Rgb(l.Color) * fade * (l.Flags.HasFlag(Light.Flag.Negative) ? -1f : 1f);
            bool shadow = (l.Flags & (Light.Flag.ShadowSpotlight | Light.Flag.ShadowHemisphere | Light.Flag.ShadowOmnidirectional)) != 0;
            scene.Lights.Add(new PlacedLight(r.Position, color, radius, shadow, l.EditorID ?? l.FormKey.ToString()));
        }
    }

    // ---- distant LOD ----------------------------------------------------------------------------

    private void Lod(SceneData scene, FormKey worldKey)
    {
        var world = WinningWorld(worldKey);
        var lodWorld = DataSource(world, WorldspaceParent.Flag.UseLodData);
        scene.LodWorldspace = lodWorld?.EditorID;
        if (world is null || world.Flags.HasFlag(Worldspace.Flag.NoLodWater)) return;
        if (lodWorld?.LodWater.FormKeyNullable is { IsNull: false } lw)
            scene.LodWater = WaterMaterialOf(lw) is { } m ? m with { DepthFog = null } : null;
    }

    // ---- camera ---------------------------------------------------------------------------------

    private static CameraStart ExteriorCamera(SceneData scene, CellEntry target, bool ring)
    {
        var g = target.Grid ?? (0, 0);
        float cx = (g.X + 0.5f) * TerrainCell.CellSize, cy = (g.Y + 0.5f) * TerrainCell.CellSize;
        float ground = new TerrainHeightField(scene.Terrain).HeightAt(cx, cy) ?? 0f;
        // A three-quarter view from the south-south-west, the whole cell (or ring) in frame.
        return new CameraStart(new Vector3(cx, cy, ground), 20f, 40f, ring ? 11000f : 6000f, GameFieldOfView);
    }

    private static CameraStart InteriorCamera(SceneData scene, (Vector3 Position, float Heading)? coc, (Vector3 Position, float Heading)? door)
    {
        // Where the player would stand: the coc marker, looking along its heading; else just inside
        // a load door, looking toward the middle of the room (a door's own heading depends on how
        // its model is authored, so it does not say which way the room is).
        if (coc is { } at)
            return FirstPerson(at.Position + new Vector3(0f, 0f, 120f), new Vector3(MathF.Sin(at.Heading), MathF.Cos(at.Heading), 0f), 65f);
        if (door is { } d && scene.Models.Count > 0)
        {
            var middle = Middle(scene);
            var toward = new Vector3(middle.X - d.Position.X, middle.Y - d.Position.Y, 0f);
            if (toward.LengthSquared() > 1f)
            {
                toward = Vector3.Normalize(toward);
                return FirstPerson(d.Position + new Vector3(0f, 0f, 120f) + toward * 120f, toward, 65f);
            }
        }
        if (scene.Models.Count == 0) return new CameraStart(Vector3.Zero, 0f, 10f, 300f, 65f);
        return new CameraStart(Middle(scene), 0f, 15f, 60f, 65f);
    }

    /// <summary>The middle of the placed objects (medians, which a stray far-off object does not
    /// drag), at least eye height above the lowest one.</summary>
    private static Vector3 Middle(SceneData scene)
    {
        static float Median(IEnumerable<float> v) { var s = v.OrderBy(x => x).ToArray(); return s[s.Length / 2]; }
        var p = scene.Models.Select(m => m.Position).ToList();
        float z = p.Min(q => q.Z);
        return new Vector3(Median(p.Select(q => q.X)), Median(p.Select(q => q.Y)), MathF.Max(Median(p.Select(q => q.Z)), z + 120f));
    }

    /// <summary>An orbit that puts the eye at <paramref name="eye"/> looking along <paramref name="forward"/>
    /// (Z-up): the target a short way ahead, the orbit angles from the eye's offset.</summary>
    private static CameraStart FirstPerson(Vector3 eye, Vector3 forward, float fov)
    {
        const float d = 40f;
        var target = eye + Vector3.Normalize(forward) * d;
        var offset = ReferencePlacement.ToMeshSpace(eye) - ReferencePlacement.ToMeshSpace(target);
        float elevation = MathF.Asin(Math.Clamp(offset.Y / d, -1f, 1f)) * 180f / MathF.PI;
        float azimuth = MathF.Atan2(offset.X, offset.Z) * 180f / MathF.PI;
        return new CameraStart(target, azimuth, elevation, d, fov);
    }

    // ---- small helpers --------------------------------------------------------------------------

    private static (int X, int Y) GridOf(Vector3 p)
        => ((int)MathF.Floor(p.X / TerrainCell.CellSize), (int)MathF.Floor(p.Y / TerrainCell.CellSize));

    private static Vector3 Rgb(System.Drawing.Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f);
    private static TK Tk(System.Drawing.Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

    /// <summary>A record's model path as a game-relative path under meshes\.</summary>
    public static string MeshPath(string given)
    {
        var p = given.Replace('/', '\\').TrimStart('\\');
        return p.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase) ? p : "meshes\\" + p;
    }

    /// <summary>A record's texture path as a game-relative path under textures\ (records write it
    /// with or without the prefix, and water types with a Data\ prefix too).</summary>
    public static string? TexturePath(string? given)
    {
        if (string.IsNullOrWhiteSpace(given)) return null;
        var p = given.Replace('/', '\\').TrimStart('\\');
        if (p.StartsWith("data\\", StringComparison.OrdinalIgnoreCase)) p = p[5..];
        return p.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase) ? p : "textures\\" + p;
    }

    // A 33x33 VNML / VCLR grid ([x, y], as the height map is read) as x-fastest triples.
    private static T[] Bytes<T>(Noggog.IReadOnlyArray2d<Noggog.P3UInt8> grid, Func<Noggog.P3UInt8, T> x,
        Func<Noggog.P3UInt8, T> y, Func<Noggog.P3UInt8, T> z)
    {
        const int side = TerrainCell.Side;
        var b = new T[side * side * 3];
        for (int gy = 0; gy < side; gy++)
            for (int gx = 0; gx < side; gx++)
            {
                var p = grid[gx, gy];
                int i = (gy * side + gx) * 3;
                b[i] = x(p); b[i + 1] = y(p); b[i + 2] = z(p);
            }
        return b;
    }
}
