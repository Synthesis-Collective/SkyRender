using System;
using FluentAssertions;
using OpenTK.Mathematics;
using SkyRender.Offscreen;
using SkyRender.World;
using Xunit;

namespace SkyRender.Tests;

/// <summary>The GL-free parts of top-down map tiles: where <see cref="TopDownCamera"/> puts a world
/// point in the image, the map light, and the transparent-background readback.</summary>
public class TopDownTileTests
{
    // Four cells around Whiterun's market: the tile the feature was measured on.
    private const float West = 20480f, South = -12288f, East = 28672f, North = -4096f;
    private const float Floor = -40000f, Ceiling = 60000f;

    /// <summary>A Skyrim world point (Z up) in normalised device coordinates: x right, y up, z depth.</summary>
    private static Vector3 Project(TopDownCamera camera, float x, float y, float z)
    {
        var mesh = ReferencePlacement.ToMeshSpace(new System.Numerics.Vector3(x, y, z));
        var clip = new Vector4(mesh, 1f) * camera.GetViewMatrix() * camera.GetProjectionMatrix(1f);
        clip.W.Should().BeApproximately(1f, 1e-6f, "an orthographic projection has no perspective divide");
        return clip.Xyz;
    }

    [Fact]
    public void Rectangle_FillsTheImage_NorthUp_EastRight()
    {
        var camera = new TopDownCamera(West, South, East, North, Floor, Ceiling);

        var northWest = Project(camera, West, North, 0f);
        var southEast = Project(camera, East, South, 0f);
        var centre = Project(camera, (West + East) / 2, (South + North) / 2, 0f);

        northWest.X.Should().BeApproximately(-1f, 1e-4f);
        northWest.Y.Should().BeApproximately(1f, 1e-4f);
        southEast.X.Should().BeApproximately(1f, 1e-4f);
        southEast.Y.Should().BeApproximately(-1f, 1e-4f);
        centre.X.Should().BeApproximately(0f, 1e-4f);
        centre.Y.Should().BeApproximately(0f, 1e-4f);
    }

    [Theory]
    [InlineData(-3000f)]
    [InlineData(0f)]
    [InlineData(25000f)]
    public void Height_DoesNotMoveAPoint_InTheImage(float z)
    {
        var camera = new TopDownCamera(West, South, East, North, Floor, Ceiling);
        var ground = Project(camera, 23000f, -7000f, 0f);
        var raised = Project(camera, 23000f, -7000f, z);

        raised.X.Should().BeApproximately(ground.X, 1e-5f);
        raised.Y.Should().BeApproximately(ground.Y, 1e-5f);
    }

    [Fact]
    public void HeightRange_MapsToTheDepthRange_CeilingNearest()
    {
        var camera = new TopDownCamera(West, South, East, North, Floor, Ceiling);

        Project(camera, 23000f, -7000f, Ceiling).Z.Should().BeApproximately(-1f, 1e-4f);
        Project(camera, 23000f, -7000f, Floor).Z.Should().BeApproximately(1f, 1e-4f);
        Project(camera, 23000f, -7000f, Ceiling + 500f).Z.Should().BeLessThan(-1f, "above the ceiling is cut");
        Project(camera, 23000f, -7000f, Floor - 500f).Z.Should().BeGreaterThan(1f, "below the floor is cut");
    }

    [Fact]
    public void NeighbouringTiles_ShareTheirEdge()
    {
        float middle = (West + East) / 2;
        var left = new TopDownCamera(West, South, middle, North, Floor, Ceiling);
        var right = new TopDownCamera(middle, South, East, North, Floor, Ceiling);

        // The same world line is the last column of one tile and the first of the next, at every height.
        foreach (float z in new[] { -500f, 0f, 9000f })
        {
            Project(left, middle, -7000f, z).X.Should().BeApproximately(1f, 1e-4f);
            Project(right, middle, -7000f, z).X.Should().BeApproximately(-1f, 1e-4f);
            Project(left, middle, -7000f, z).Y.Should().BeApproximately(Project(right, middle, -7000f, z).Y, 1e-5f);
        }
    }

    [Fact]
    public void ShadingEye_IsFarAbove_SoViewRaysAreVertical()
    {
        var camera = new TopDownCamera(West, South, East, North, Floor, Ceiling);
        var eye = camera.GetEyePosition();
        var corner = ReferencePlacement.ToMeshSpace(new System.Numerics.Vector3(West, South, 0f));

        var ray = Vector3.Normalize(eye - corner);
        ray.Y.Should().BeGreaterThan(0.999999f, "the view ray at a tile corner is as vertical as at its centre");
    }

    [Fact]
    public void ImageSize_IsTheRectangleAtTheScale()
    {
        TopDownCamera.ImageSize(West, South, East, North, 16f).Should().Be((512, 512));
        TopDownCamera.ImageSize(0f, 0f, 4096f, 8192f, 8f).Should().Be((512, 1024));
        new TopDownCamera(West, South, East, North, Floor, Ceiling).ImageSize(32f).Should().Be((256, 256));
        TopDownCamera.ImageSize(0f, 0f, 1f, 1f, 16f).Should().Be((1, 1), "never an empty image");
        FluentActions.Invoking(() => TopDownCamera.ImageSize(0f, 0f, 1f, 1f, 0f)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(10f, 0f, 10f, 5f, 0f, 1f)]   // no width
    [InlineData(0f, 5f, 10f, 5f, 0f, 1f)]    // no height
    [InlineData(0f, 0f, 10f, 5f, 3f, 3f)]    // no height range
    [InlineData(0f, 0f, 10f, 5f, 3f, 2f)]    // ceiling under floor
    public void EmptyRectangleOrRange_IsRefused(float w, float s, float e, float n, float floor, float ceiling)
    {
        FluentActions.Invoking(() => new TopDownCamera(w, s, e, n, floor, ceiling)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void IsAnOrbitCamera_WhoseVirtualsTheRendererCalls()
    {
        OrbitCamera camera = new TopDownCamera(West, South, East, North, Floor, Ceiling);
        var perspective = new OrbitCamera();

        // Through the base type, as GlRenderer.Render holds it.
        camera.GetProjectionMatrix(1f).M44.Should().Be(1f, "orthographic");
        perspective.GetProjectionMatrix(1f).M44.Should().Be(0f, "the orbit camera's own projection is unchanged");
        camera.GetEyePosition().Y.Should().BeGreaterThan(1e6f);
    }

    [Fact]
    public void MapLight_IsFixed_FromTheNorthWest_WithNoFogOrImageSpace()
    {
        var map = SceneEnvironment.TopDownMap;
        map.Fog.Should().BeNull();
        map.ImageSpace.Should().BeNull();
        map.SkyGradient.Should().BeNull();

        // Applied to a renderer (no GL needed for the light slots): the key light comes from the
        // west (mesh -X) and the north (mesh -Z), from above.
        var renderer = new GlRenderer();
        renderer.ApplyEnvironment(map);
        var toLight = renderer.Lights[1].Direction;
        toLight.X.Should().BeLessThan(0f);
        toLight.Z.Should().BeLessThan(0f);
        toLight.Y.Should().BeGreaterThan(0.5f);
        renderer.Fog.Should().BeNull();
        renderer.ImageSpace.Should().BeNull();
    }

    [Fact]
    public void Unpremultiply_RestoresTheSurfaceColour_AndLeavesOpaquePixelsAlone()
    {
        byte[] rgba =
        {
            200, 100, 50, 255,   // opaque: untouched
            100, 50, 25, 128,    // half covered: colour was mixed with black by 128/255
            9, 9, 9, 0,          // nothing drawn: cleared colour, whatever it holds
            250, 250, 250, 128,  // brighter than its alpha allows (additive glow): clamps
        };

        GameWindowOffscreenRenderer.UnpremultiplyAlpha(rgba);

        rgba.Should().Equal(
            200, 100, 50, 255,
            199, 100, 50, 128,
            0, 0, 0, 0,
            255, 255, 255, 128);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 3)]
    [InlineData(64, 48)]
    public void EncodeRgba_RoundTripsEveryPixel_AlphaIncluded(int width, int height)
    {
        var rgba = new byte[width * height * 4];
        new Random(width * 1000 + height).NextBytes(rgba);

        var png = PngWriter.EncodeRgba(rgba, width, height);
        var decoded = PngWriterTests.Decode(png, out int w, out int h, channels: 4);

        w.Should().Be(width);
        h.Should().Be(height);
        decoded.Should().Equal(rgba);
    }
}
