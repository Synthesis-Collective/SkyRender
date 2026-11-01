using System;
using System.Collections.Generic;
using NiflySharp;
using NiflySharp.Blocks;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace SkyRender.World;

/// <summary>
/// One drawable shape of a terrain or object LOD block (<c>.btr</c> / <c>.bto</c>), as plain data in
/// Skyrim's Z-up space, relative to the block's south-west corner (the engine places the file there).
/// </summary>
public sealed class LodShape
{
    public required string Name { get; init; }
    /// <summary>Vertex positions with the shape's node chain applied (LOD shapes carry a scale equal
    /// to their level, so their half-precision vertices cover the block).</summary>
    public required Vector3[] Positions { get; init; }
    /// <summary>Vertex normals (unit, Z-up), or null when the shape stores none (terrain LOD, whose
    /// normals are in its model-space normal texture).</summary>
    public Vector3[]? Normals { get; init; }
    public Vector3[]? Tangents { get; init; }
    public Vector3[]? Bitangents { get; init; }
    /// <summary>Vertex colours (RGBA, 0..1), read only when the shader has
    /// <c>SLSF2_Vertex_Colors</c>; null otherwise. In vanilla object LOD the HD shapes (objHD,
    /// objsnowHD: 612 of 1743 Tamriel shapes) carry them; so does DynDOLOD's object and grass LOD.</summary>
    public Vector4[]? Colors { get; init; }
    public required Vector2[] Uvs { get; init; }
    /// <summary>Triangle list, three indices per triangle.</summary>
    public required int[] Indices { get; init; }
    /// <summary>The shape's scale to the file root: the engine's LOD landscape shader lowers vertices
    /// under the loaded cells by 230 MODEL units, which is 230 × this in world units.</summary>
    public float Scale { get; init; } = 1f;
    public string? DiffusePath { get; init; }
    public string? NormalPath { get; init; }
    public uint ShaderType { get; init; }
    public uint ShaderFlags1 { get; init; }
    public uint ShaderFlags2 { get; init; }
    public bool ModelSpaceNormals => (ShaderFlags1 & 0x1000u) != 0;     // SLSF1_Model_Space_Normals
    public bool DoubleSided => (ShaderFlags2 & 0x10u) != 0;            // SLSF2_Double_Sided
    public bool HasSpecular => (ShaderFlags1 & 0x1u) != 0;             // SLSF1_Specular
    public bool HasVertexColors => (ShaderFlags2 & 0x20u) != 0;        // SLSF2_Vertex_Colors
    /// <summary>A LOD water quad set: a shape under a terrain block's <c>WATER</c> node, with no
    /// shader property (the engine draws it with the worldspace's LOD water type).</summary>
    public bool IsWater { get; init; }
    public bool AlphaTest { get; init; }
    public float AlphaThreshold { get; init; }
    /// <summary>A BSSubIndexTriShape's segments as (first triangle, triangle count), in segment order;
    /// empty for a plain BSTriShape. In level-4 object LOD, segment k holds the objects of cell
    /// (k / 4, k % 4) of the block (see <see cref="LodMeshBuilder.BuildObjects"/>).</summary>
    public IReadOnlyList<(int FirstTriangle, int TriangleCount)> Segments { get; init; } = Array.Empty<(int, int)>();
}

/// <summary>Reads the shapes of a <c>.btr</c> / <c>.bto</c> file (they are NIFs) through Nifly.
/// Shapes with no shader property are skipped, except, with <c>includeWater</c>, the flat LOD water
/// quads under a terrain block's <c>WATER</c> node (<see cref="LodShape.IsWater"/>).</summary>
public static class LodNifReader
{
    /// <summary>The node a terrain LOD block keeps its LOD water quads under.</summary>
    public const string WaterNodeName = "WATER";

    public static List<LodShape> Read(string diskPath, bool includeWater = false, GameAssetResolver? resolver = null)
    {
        var shapesOut = new List<LodShape>();
        var nif = NifFileLoader.TryLoad(diskPath, resolver)?.File
            ?? throw new InvalidOperationException($"Could not read '{diskPath}' as a NIF");
        foreach (var shape in NifAccess.Shapes(nif))
        {
            // The concrete block (the INiShader view answers type and flags with defaults).
            var shaderObj = NifAccess.Shader(nif, shape);
            bool isWater = false;
            if (shaderObj is null)
            {
                // LOD water: no shader; the engine applies the worldspace's LOD water type.
                if (!includeWater || !string.Equals(NifAccess.Name(NifAccess.ParentNode(nif, shape)), WaterNodeName, StringComparison.OrdinalIgnoreCase))
                    continue;
                isWater = true;
            }
            NifAccess.Vertices(nif, shape, out var verts);
            if (verts.Length == 0 || !NifAccess.Triangles(shape, out var tris) || tris.Count == 0) continue;
            int n = verts.Length;

            var xf = TransformToRoot(nif, shape);
            var pos = new Vector3[n];
            for (int i = 0; i < n; i++) pos[i] = xf.Point(verts[i]);

            Vector3[]? normals = ReadDirections(NifAccess.Normals(nif, shape), n, xf);
            Vector3[]? tangents = ReadDirections(NifAccess.Tangents(nif, shape), n, xf);
            Vector3[]? bitangents = ReadDirections(NifAccess.Bitangents(nif, shape), n, xf);

            var uvs = new Vector2[n];
            var nuv = NifAccess.Uvs(nif, shape);
            if (nuv != null && nuv.Length == n) Array.Copy(nuv, uvs, n);

            var idx = new int[tris.Count * 3];
            for (int i = 0; i < tris.Count; i++)
            {
                var t = tris[i];
                idx[i * 3] = t.V1; idx[i * 3 + 1] = t.V2; idx[i * 3 + 2] = t.V3;
            }

            uint type = 0, f1 = 0, f2 = 0;
            if (shaderObj is BSLightingShaderProperty bslsp)
            {
                type = (uint)bslsp.ShaderType_SK_FO4;
                (f1, f2) = NifAccess.ShaderFlags(nif, bslsp);
            }
            string? diffuse = isWater ? null : Texture(nif, shape, 0), normal = isWater ? null : Texture(nif, shape, 1);

            Vector4[]? colors = null;
            if ((f2 & 0x20u) != 0)   // SLSF2_Vertex_Colors
            {
                var nc = NifAccess.Colors(nif, shape);
                if (nc != null && nc.Length == n) colors = nc;
            }

            bool alphaTest = false;
            float threshold = 0f;
            if (shape.HasAlphaProperty && NifAccess.Block<INiObject>(nif, shape.AlphaPropertyRef) is NiAlphaProperty ap)
            {
                alphaTest = (ap.Flags.Value & 0x200) != 0;   // NiAlphaProperty bit 9: alpha test
                threshold = ap.Threshold / 255f;
            }

            var segments = new List<(int, int)>();
            if (shape is BSSubIndexTriShape sit && sit.Segments != null)
            {
                foreach (var s in sit.Segments)
                {
                    // The segment's start is stored in index units (three per triangle).
                    segments.Add(((int)(s.StartIndex / 3), (int)s.NumPrimitives));
                }
            }

            shapesOut.Add(new LodShape
            {
                Name = NifAccess.Name(shape) ?? "",
                Positions = pos,
                Normals = normals,
                Tangents = tangents,
                Bitangents = bitangents,
                Colors = colors,
                Uvs = uvs,
                Indices = idx,
                Scale = xf.Scale,
                DiffusePath = diffuse,
                NormalPath = normal,
                ShaderType = type,
                ShaderFlags1 = f1,
                ShaderFlags2 = f2,
                AlphaTest = alphaTest,
                AlphaThreshold = threshold,
                Segments = segments,
                IsWater = isWater,
            });
        }
        return shapesOut;
    }

    private static string? Texture(NifFile nif, INiShape shape, int slot)
    {
        try
        {
            string p = NifAccess.TexturePath(nif, shape, slot);
            return string.IsNullOrWhiteSpace(p) ? null : p;
        }
        catch { return null; }
    }

    private static Vector3[]? ReadDirections(Vector3[]? v, int n, Xf xf)
    {
        if (v is null || v.Length != n) return null;
        var r = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            var d = xf.Direction(v[i]);
            r[i] = d.LengthSquared() > 1e-12f ? Vector3.Normalize(d) : Vector3.UnitZ;
        }
        return r;
    }

    // The shape's transform to the file root, flattened once so vertices transform in plain arithmetic.
    private readonly record struct Xf(Vector3 R0, Vector3 R1, Vector3 R2, Vector3 T, float Scale)
    {
        public Vector3 Direction(Vector3 v) => new(Vector3.Dot(R0, v), Vector3.Dot(R1, v), Vector3.Dot(R2, v));
        public Vector3 Point(Vector3 v) => Direction(v * Scale) + T;
    }

    private static Xf TransformToRoot(NifFile nif, INiShape shape)
    {
        var result = NifAccess.TransformToGlobal(nif, shape);
        // The rotation's columns as rotation * basis vector, then regrouped into rows.
        var c0 = result.Rotate(Vector3.UnitX); var c1 = result.Rotate(Vector3.UnitY); var c2 = result.Rotate(Vector3.UnitZ);
        return new Xf(new Vector3(c0.X, c1.X, c2.X), new Vector3(c0.Y, c1.Y, c2.Y), new Vector3(c0.Z, c1.Z, c2.Z),
            result.T, result.S);
    }
}
