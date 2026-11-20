using System;
using OpenTK.Mathematics;

namespace SkyRender;

/// <summary>
/// A camera that looks straight down on a rectangle of the world with no perspective, for map
/// tiles: the rectangle fills the image exactly, north is up and east is right, so tiles of
/// neighbouring rectangles drawn at the same scale join without a seam.
///
/// <para>The rectangle and the height range are in Skyrim world units (X east, Y north, Z up), as
/// a host's records give them; the scene itself is in SkyRender's Y-up mesh space as usual
/// (<c>ReferencePlacement.ToMeshSpace</c>). Only what lies between <see cref="Floor"/> and
/// <see cref="Ceiling"/> is drawn. Put the ceiling above everything to draw it all, or lower it to
/// leave out what is above (a cliff overhang over a town): geometry it cuts through is left open.</para>
///
/// <para>The image must have the rectangle's proportions (<see cref="ImageSize"/> gives the size
/// for a scale); the aspect ratio passed to <see cref="GetProjectionMatrix"/> is not used. It
/// assumes <c>GlRenderer.ModelScale</c> is 1, as it is for scenes.</para>
///
/// <para>It is an <see cref="OrbitCamera"/> so that it goes wherever one does
/// (<c>GlRenderer.Render</c>, <c>IOffscreenScene.Camera</c>); the orbit properties and the mouse
/// handlers do not move it.</para>
/// </summary>
public sealed class TopDownCamera : OrbitCamera
{
    // The view direction a surface is shaded from (specular, Fresnel, environment maps) comes from
    // the eye position. A perspective eye just above the ceiling would shade the two sides of a
    // tile boundary from different angles; an eye this far up makes every view ray vertical to
    // within a thousandth of a degree, so water and gloss match across tiles.
    private const float ShadingEyeHeight = 1e7f;

    /// <summary>
    /// A camera over the rectangle <paramref name="west"/>..<paramref name="east"/> by
    /// <paramref name="south"/>..<paramref name="north"/>, drawing heights
    /// <paramref name="floor"/>..<paramref name="ceiling"/>. Skyrim world units.
    /// </summary>
    public TopDownCamera(float west, float south, float east, float north, float floor, float ceiling)
    {
        if (!(east > west)) throw new ArgumentException("east must be greater than west.", nameof(east));
        if (!(north > south)) throw new ArgumentException("north must be greater than south.", nameof(north));
        if (!(ceiling > floor)) throw new ArgumentException("ceiling must be greater than floor.", nameof(ceiling));
        West = west;
        South = south;
        East = east;
        North = north;
        Floor = floor;
        Ceiling = ceiling;

        // The orbit properties describe the same view as nearly as they can, for the code that
        // reads them (the renderer's gizmos scale by Distance).
        Target = new Vector3((west + east) * 0.5f, floor, -(south + north) * 0.5f);
        Distance = ceiling - floor;
        Elevation = 90f;
        Azimuth = 0f;
        NearPlane = 0f;
        FarPlane = ceiling - floor;
    }

    /// <summary>The rectangle's west edge (world X).</summary>
    public float West { get; }
    /// <summary>The rectangle's south edge (world Y).</summary>
    public float South { get; }
    /// <summary>The rectangle's east edge (world X).</summary>
    public float East { get; }
    /// <summary>The rectangle's north edge (world Y).</summary>
    public float North { get; }
    /// <summary>The lowest height drawn (world Z).</summary>
    public float Floor { get; }
    /// <summary>The highest height drawn (world Z).</summary>
    public float Ceiling { get; }

    /// <summary>
    /// The image size, in pixels, of a rectangle at <paramref name="unitsPerPixel"/> world units a
    /// pixel. Tiles join exactly when each rectangle's sides are whole multiples of the scale (a
    /// 4096-unit cell at 16 units a pixel is 256 pixels); otherwise the size is rounded and the
    /// scale is off by up to half a pixel across the tile.
    /// </summary>
    public static (int Width, int Height) ImageSize(float west, float south, float east, float north, float unitsPerPixel)
    {
        if (!(unitsPerPixel > 0f)) throw new ArgumentOutOfRangeException(nameof(unitsPerPixel));
        int w = Math.Max(1, (int)MathF.Round((east - west) / unitsPerPixel));
        int h = Math.Max(1, (int)MathF.Round((north - south) / unitsPerPixel));
        return (w, h);
    }

    /// <summary>The image size of this camera's rectangle at <paramref name="unitsPerPixel"/>.</summary>
    public (int Width, int Height) ImageSize(float unitsPerPixel) => ImageSize(West, South, East, North, unitsPerPixel);

    /// <summary>From the ceiling above the rectangle's centre, looking down, north (mesh -Z) up.</summary>
    public override Matrix4 GetViewMatrix()
    {
        var eye = new Vector3((West + East) * 0.5f, Ceiling, -(South + North) * 0.5f);
        return Matrix4.LookAt(eye, eye - Vector3.UnitY, -Vector3.UnitZ);
    }

    /// <summary>Orthographic over the rectangle and the height range. <paramref name="aspectRatio"/>
    /// is not used: the rectangle sets the proportions.</summary>
    public override Matrix4 GetProjectionMatrix(float aspectRatio)
        => Matrix4.CreateOrthographic(East - West, North - South, 0f, Ceiling - Floor);

    /// <summary>The point surfaces are shaded as seen from: far above the rectangle's centre, so
    /// every view ray is vertical (see the class remarks on tile seams).</summary>
    public override Vector3 GetEyePosition()
        => new((West + East) * 0.5f, Ceiling + ShadingEyeHeight, -(South + North) * 0.5f);
}
