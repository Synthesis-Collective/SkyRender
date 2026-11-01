using System.Numerics;
using FluentAssertions;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;
using Xunit;

namespace SkyRender.Tests;

/// <summary>
/// NIF reading and mesh building on any OS: each fixture is an SSE NIF authored here with Nifly,
/// saved to bytes and read back through SkyRender's loader, so no game data is involved.
/// </summary>
public sealed class NifRoundTripTests
{
    private static readonly Vector3[] Quad =
    {
        new(0f, 0f, 0f), new(10f, 0f, 0f), new(10f, 10f, 0f), new(0f, 10f, 0f),
    };

    [Fact]
    public void BuildsTheAuthoredShape()
    {
        var meshes = Build(Author(Vector3.Zero, 1f));

        meshes.Should().ContainSingle();
        var mesh = meshes[0];
        mesh.ShapeName.Should().Be("Quad");
        mesh.Positions.Should().HaveCount(4);
        mesh.Indices.Should().Equal(0, 1, 2, 0, 2, 3);
        mesh.TextureCoordinates.Should().HaveCount(4);
        mesh.TextureCoordinates[2].X.Should().BeApproximately(1f, 1e-3f);
        mesh.TextureCoordinates[2].Y.Should().BeApproximately(1f, 1e-3f);
    }

    [Fact]
    public void TrimsTexturePathsLikeCppNifly()
    {
        // C++ nifly's TrimTexturePaths on load: forward slashes become backslashes, anything
        // before "textures\" goes, and a path without that prefix gets it.
        var mesh = Build(Author(Vector3.Zero, 1f)).Single();

        mesh.TexturePaths[0].Should().Be(@"textures\test\diffuse.dds");
        mesh.TexturePaths[1].Should().Be(@"textures\test\normal_n.dds");
    }

    [Fact]
    public void AppliesTheParentNodeTransform()
    {
        var plain = Build(Author(Vector3.Zero, 1f)).Single();
        var moved = Build(Author(new Vector3(100f, 200f, 300f), 2f)).Single();

        // Whatever axis convention the builder outputs in, a parent translation moves every vertex
        // by the same distance and a parent scale doubles every edge.
        var shift = (moved.Positions[0] - plain.Positions[0]).Length();
        shift.Should().BeApproximately(new Vector3(100f, 200f, 300f).Length(), 1e-2f);
        var edgePlain = (plain.Positions[1] - plain.Positions[0]).Length();
        var edgeMoved = (moved.Positions[1] - moved.Positions[0]).Length();
        edgeMoved.Should().BeApproximately(2f * edgePlain, 1e-3f);
    }

    [Fact]
    public void LoadsFromAFileThroughTheLoader()
    {
        var path = Path.Combine(Path.GetTempPath(), $"skyrender-core-{Guid.NewGuid():N}.nif");
        File.WriteAllBytes(path, Author(Vector3.Zero, 1f));
        try
        {
            var nif = NifFileLoader.TryLoad(path, resolver: null);

            nif.Should().NotBeNull();
            NifAccess.Shapes(nif!.File).Should().ContainSingle();
            NifFileLoader.TryReadBytes(path, resolver: null).Should().Equal(File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsBytesThatAreNotANif()
    {
        NifAccess.Load(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }).Should().BeNull();
        NifAccess.Load(Array.Empty<byte>()).Should().BeNull();
    }

    /// <summary>A root node (with the given translation and scale) holding one textured quad.</summary>
    private static byte[] Author(Vector3 rootTranslation, float rootScale)
    {
        var version = NiVersion.GetSSE();
        var nif = new NifFile(version, withRootNode: true);
        var root = nif.GetRootNode();
        root.Translation = rootTranslation;
        root.Scale = rootScale;

        var textures = new BSShaderTextureSet
        {
            Textures = Enumerable.Range(0, 9).Select(i => new NiString4(i switch
            {
                0 => "Data/Textures/test/diffuse.dds",
                1 => "test\\normal_n.dds",
                _ => "",
            }, false)).ToList(),
            NumTextures = 9,
        };
        int texturesId = nif.AddBlock(textures);

        var shader = new BSLightingShaderProperty
        {
            Name = new NiStringRef(""),
            TextureSetRef = new NiBlockRef<BSShaderTextureSet>(texturesId),
        };
        int shaderId = nif.AddBlock(shader);

        var shape = new BSTriShape(version, Quad.ToList(),
            new List<Triangle> { new(0, 1, 2), new(0, 2, 3) },
            new List<TexCoord> { new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f) },
            Enumerable.Repeat(Vector3.UnitZ, 4).ToList())
        {
            Name = new NiStringRef("Quad"),
            ShaderPropertyRef = new NiBlockRef<BSShaderProperty>(shaderId),
        };
        // Nifly's BSTriShape constructor sets the Skinned vertex flag, and an SSE write of a skinned
        // shape leaves the geometry to the skin partition; this one has no skin.
        shape.IsSkinned = false;
        int shapeId = nif.AddBlock(shape);
        root.Children.AddBlockRef(shapeId);

        using var stream = new MemoryStream();
        nif.Save(stream);
        return stream.ToArray();
    }

    private static List<NifMeshBuilder.BuiltMesh> Build(byte[] bytes)
    {
        var nif = NifAccess.Load(bytes);
        nif.Should().NotBeNull();
        return new NifMeshBuilder(new NullLogger(), new RenderLogGate()).BuildFromNif(nif!);
    }

    private sealed class NullLogger : IRenderLogger
    {
        public void LogMessage(string message) { }
        public void LogError(string message) { }
        public void LogError(string message, Exception ex) { }
    }
}
