using System.Collections.Generic;
using System.Numerics;
using SkyRender.World;

namespace SkyRender.CellDemo;

// What the cell walk (CellWalk.cs) hands to the loader and the GL thread. Plain data: no Mutagen
// types, no GL handles. Positions are Skyrim world units, Z up, unless a name says otherwise.

/// <summary>A snow, ash or moss covering a static's material object (STAT DNAM -> MATO) lays over
/// its model, drawn inside the model's own shader (SkyRender's projected material).</summary>
internal sealed record ProjectedCover(float FalloffScale, float FalloffBias, float NoiseUvScale, Vector3 Color);

/// <summary>One placed reference that draws a model.</summary>
internal sealed record PlacedModel(string ModelPath, Vector3 Position, Vector3 Rotation, float Scale,
    string BaseLabel, ProjectedCover? Cover);

/// <summary>One placed light (LIGH reference) as a point light: colour already times its fade.</summary>
internal sealed record PlacedLight(Vector3 Position, Vector3 Color, float Radius, bool CastsShadow, string Label);

/// <summary>Where the camera starts: a point to look at and the orbit around it (degrees, units).</summary>
internal sealed record CameraStart(Vector3 Target, float Azimuth, float Elevation, float Distance, float FieldOfView);

/// <summary>Everything one cell selection draws.</summary>
internal sealed class SceneData
{
    public required string Title { get; init; }
    public required bool Interior { get; init; }
    public string WinningPlugin { get; init; } = "";

    public List<PlacedModel> Models { get; } = new();
    public List<PlacedLight> Lights { get; } = new();
    public List<TerrainCell> Terrain { get; } = new();
    public List<WaterSurface> Water { get; } = new();
    /// <summary>Each terrain cell's water height, for the grass water rule.</summary>
    public Dictionary<(int X, int Y), float> WaterHeights { get; } = new();
    public bool Grass { get; set; }

    public required SceneEnvironment Environment { get; set; }
    /// <summary>One line naming where the light came from (weather, cell lighting).</summary>
    public string EnvironmentText { get; set; } = "";
    /// <summary>The sun casts shadows (exteriors).</summary>
    public bool SunShadows { get; set; }

    /// <summary>The grid squares drawn at full detail (exteriors), which distant LOD is cut under.</summary>
    public List<(int X, int Y)> LoadedCells { get; } = new();
    /// <summary>The worldspace whose LOD files to read (the parent's when it uses its parent's LOD), or null.</summary>
    public string? LodWorldspace { get; set; }
    /// <summary>The worldspace's LOD water type, or null when LOD water is not drawn.</summary>
    public WaterMaterial? LodWater { get; set; }

    public CameraStart Camera { get; set; } = new(Vector3.Zero, 0f, 30f, 2000f, 50.534f);

    /// <summary>What was left out and why, for the log (disabled references, editor markers, ...).</summary>
    public Dictionary<string, int> Skipped { get; } = new();

    public void Skip(string why)
    {
        Skipped.TryGetValue(why, out int n);
        Skipped[why] = n + 1;
    }
}
