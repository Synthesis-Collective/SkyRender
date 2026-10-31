using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace SkyRender;

/// <summary>
/// The NIF read helpers SkyRender used from niflysharp (SWIG over the C++ nifly), rebuilt on
/// Nifly's managed block model. Each one ports the matching C++ function in nifly's
/// <c>NifFile.cpp</c> / <c>Geometry.cpp</c> (SteveTownsend/nifly), so the renderer sees the same
/// values: the same shape order, the same decoding, the same transform arithmetic in the same
/// operation order. NifSnapshot's adapter diff checks every helper against niflysharp file by file.
/// </summary>
internal static class NifAccess
{
    /// <summary>Parses NIF bytes; null when the file does not load. A file Nifly refuses (including
    /// a host build that bounds-checks corrupt counts and throws) is a failed load, never an escape.</summary>
    public static LoadedNif? Load(byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        try
        {
            var nif = new NifFile();
            using var stream = new MemoryStream(bytes, writable: false);
            return nif.Load(stream) == 0 && nif.Valid ? new LoadedNif(nif) : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    // ---- Blocks and names ----

    /// <summary>Every shape, in block order (C++ <c>NifFile::GetShapes</c>). Particle systems are
    /// left out: Nifly derives NiParticleSystem from NiParticles (a shape), C++ nifly from
    /// NiAVObject, so C++ never listed them and the renderer never drew them.</summary>
    public static List<INiShape> Shapes(NifFile nif) =>
        nif.Blocks.OfType<INiShape>().Where(s => s is not NiParticleSystem).ToList();

    public static string? Name(object? block) => (block as NiflySharp.Interfaces.INiNamed)?.Name?.String;

    public static T? Block<T>(NifFile nif, INiRef? reference) where T : class =>
        reference == null || reference.IsEmpty() ? null : nif.GetBlock<T>(reference);

    public static int BlockId(NifFile nif, INiObject block) => nif.GetBlockIndex(block, out int id) ? id : -1;

    /// <summary>The node whose child list holds <paramref name="child"/>, scanning blocks in order
    /// (C++ <c>NifFile::GetParentNode</c>).</summary>
    public static NiNode? ParentNode(NifFile nif, INiObject? child)
    {
        if (child == null) return null;
        int id = BlockId(nif, child);
        if (id < 0) return null;
        foreach (var block in nif.Blocks)
        {
            if (block is NiNode node && node.Children != null && node.Children.Indices.Contains(id))
                return node;
        }
        return null;
    }

    // ---- Transforms (C++ MatTransform, same arithmetic order) ----

    /// <summary>Scale, rotate, translate. <see cref="R0"/>..<see cref="R2"/> are the rotation's rows
    /// as C++ nifly holds them: the 9 floats in file order, three per row.</summary>
    public readonly record struct Xform(Vector3 T, Vector3 R0, Vector3 R1, Vector3 R2, float S)
    {
        public static readonly Xform Identity = new(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, 1f);

        /// <summary>rotation * v (C++ <c>Matrix3::operator*(Vector3)</c>).</summary>
        public Vector3 Rotate(Vector3 v) => new(
            R0.X * v.X + R0.Y * v.Y + R0.Z * v.Z,
            R1.X * v.X + R1.Y * v.Y + R1.Z * v.Z,
            R2.X * v.X + R2.Y * v.Y + R2.Z * v.Z);

        /// <summary>translation + rotation * (v * scale) (C++ <c>MatTransform::ApplyTransform</c>).</summary>
        public Vector3 Apply(Vector3 v)
        {
            var r = Rotate(new Vector3(v.X * S, v.Y * S, v.Z * S));
            return new Vector3(T.X + r.X, T.Y + r.Y, T.Z + r.Z);
        }

        /// <summary>This after <paramref name="other"/>: Apply(Compose(o), v) == Apply(this, Apply(o, v))
        /// (C++ <c>MatTransform::ComposeTransforms</c>).</summary>
        public Xform Compose(Xform other)
        {
            static Vector3 Row(Vector3 r, Xform o) => new(
                r.X * o.R0.X + r.Y * o.R1.X + r.Z * o.R2.X,
                r.X * o.R0.Y + r.Y * o.R1.Y + r.Z * o.R2.Y,
                r.X * o.R0.Z + r.Y * o.R1.Z + r.Z * o.R2.Z);
            var t = Rotate(new Vector3(S * other.T.X, S * other.T.Y, S * other.T.Z));
            return new Xform(new Vector3(T.X + t.X, T.Y + t.Y, T.Z + t.Z),
                Row(R0, other), Row(R1, other), Row(R2, other), S * other.S);
        }

        // nif.xml names Matrix33's fields column-major (m11, m21, m31, m12, ...) and Nifly reads them
        // in that order; C++ nifly reads the same 9 floats straight into its rows. So C++ row 0 is
        // (M11, M21, M31). Nifly's own NiNode.TransformToParent builds the transpose; not used here.
        public static Xform From(Vector3 translation, Matrix33 m, float scale) => new(translation,
            new Vector3(m.M11, m.M21, m.M31), new Vector3(m.M12, m.M22, m.M32), new Vector3(m.M13, m.M23, m.M33), scale);
    }

    public static Xform TransformToParent(INiShape shape) => Xform.From(shape.Translation, shape.Rotation, shape.Scale);
    public static Xform TransformToParent(NiAVObject obj) => Xform.From(obj.Translation, obj.Rotation, obj.Scale);

    private static Xform TransformToParent(INiObject obj) => obj switch
    {
        INiShape shape => TransformToParent(shape),
        NiAVObject av => TransformToParent(av),
        _ => Xform.Identity,
    };

    /// <summary>The object's transform to the file root, composed parent by parent.</summary>
    public static Xform TransformToGlobal(NifFile nif, INiObject obj)
    {
        var xform = TransformToParent(obj);
        for (var parent = ParentNode(nif, obj); parent != null; parent = ParentNode(nif, parent))
            xform = TransformToParent(parent).Compose(xform);
        return xform;
    }

    /// <summary>The first node named <paramref name="nodeName"/>, in block order, to the root
    /// (C++ <c>NifFile::GetNodeTransformToGlobal</c>).</summary>
    public static bool NodeTransformToGlobal(NifFile nif, string nodeName, out Xform xform)
    {
        foreach (var block in nif.Blocks)
        {
            if (block is NiNode node && Name(node) == nodeName)
            {
                xform = TransformToGlobal(nif, node);
                return true;
            }
        }
        xform = Xform.Identity;
        return false;
    }

    // ---- Geometry (C++ NifFile::Get*ForShape) ----

    private static NiGeometryData? GeomData(NifFile nif, INiShape shape) =>
        shape is BSTriShape ? null : Block<NiGeometryData>(nif, shape.DataRef);

    /// <summary>Vertex positions; false (and empty) for a shape with neither layout. For a
    /// BSDynamicTriShape this is the static vertex data, as in C++, not the dynamic block.</summary>
    public static bool Vertices(NifFile nif, INiShape shape, out Vector3[] verts)
    {
        if (shape is BSTriShape bs)
        {
            verts = bs.VertexPositions.ToArray();
            return true;
        }
        if (GeomData(nif, shape) is { } data)
        {
            verts = data.Vertices?.ToArray() ?? Array.Empty<Vector3>();
            return true;
        }
        verts = Array.Empty<Vector3>();
        return false;
    }

    /// <summary>Null when the shape has no normals (C++ returns nullptr).</summary>
    public static Vector3[]? Normals(NifFile nif, INiShape shape)
    {
        if (!shape.HasNormals) return null;
        if (shape is BSTriShape bs) return bs.Normals.ToArray();
        return GeomData(nif, shape)?.Normals?.ToArray();
    }

    public static Vector3[]? Tangents(NifFile nif, INiShape shape)
    {
        if (!shape.HasTangents) return null;
        if (shape is BSTriShape bs) return bs.Tangents.ToArray();
        return GeomData(nif, shape)?.Tangents?.ToArray();
    }

    public static Vector3[]? Bitangents(NifFile nif, INiShape shape)
    {
        if (!shape.HasTangents) return null;
        if (shape is BSTriShape bs) return bs.Bitangents.ToArray();
        return GeomData(nif, shape)?.Bitangents?.ToArray();
    }

    /// <summary>The first UV set; null when there is none.</summary>
    public static Vector2[]? Uvs(NifFile nif, INiShape shape)
    {
        if (shape is BSTriShape bs)
            return bs.HasUVs ? bs.UVs.Select(t => new Vector2(t.U, t.V)).ToArray() : null;
        if (GeomData(nif, shape) is { UVSets: { Count: > 0 } sets } data)
            return sets.Take(data.NumVertices).Select(t => new Vector2(t.U, t.V)).ToArray();
        return null;
    }

    /// <summary>Vertex colours as RGBA floats; null when the layout has none.</summary>
    public static Vector4[]? Colors(NifFile nif, INiShape shape)
    {
        if (shape is BSTriShape bs)
            return bs.HasVertexColors ? bs.VertexColors.Select(c => new Vector4(c.R, c.G, c.B, c.A)).ToArray() : null;
        return GeomData(nif, shape)?.VertexColors?.Select(c => new Vector4(c.R, c.G, c.B, c.A)).ToArray();
    }

    /// <summary>Triangles; false when the shape reports none (C++ <c>NiShape::GetTriangles</c>;
    /// <c>BSTriShape::GetTriangles</c> returns true even for an empty list).</summary>
    public static bool Triangles(INiShape shape, out List<Triangle> tris)
    {
        tris = shape.Triangles ?? new List<Triangle>();
        return shape is BSTriShape || shape.Triangles != null;
    }

    // ---- Textures ----

    /// <summary>The shader of a shape, as its concrete block (the INiShader view hides fields).</summary>
    public static INiObject? Shader(NifFile nif, INiShape shape) => Block<INiObject>(nif, shape.ShaderPropertyRef);

    /// <summary>Texture slot <paramref name="index"/> of the shape's shader: the texture set's entry,
    /// or for an effect shader without a set, its named textures (C++ <c>NifFile::GetTextureSlot</c>).</summary>
    public static string TexturePath(NifFile nif, INiShape shape, int index)
    {
        var shader = Shader(nif, shape);
        if (shader is BSLightingShaderProperty lighting && Block<BSShaderTextureSet>(nif, lighting.TextureSetRef) is { } set)
            return set.Textures != null && index < set.Textures.Count ? TrimTexturePath(set.Textures[index]?.Content ?? "") : "";
        if (shader is BSEffectShaderProperty effect)
        {
            return index switch
            {
                0 => effect.SourceTexture?.Content ?? "",
                1 => effect.NormalTexture?.Content ?? "",
                3 => effect.GreyscaleTexture?.Content ?? "",
                4 => effect.EnvMapTexture?.Content ?? "",
                5 => effect.EnvMaskTexture?.Content ?? "",
                _ => "",
            };
        }
        return "";
    }

    // MSVC's std::regex (which built niflysharp) matches ^ at every line start, so a path with an
    // embedded newline is trimmed line by line; Multiline reproduces that.
    private const System.Text.RegularExpressions.RegexOptions Lines = System.Text.RegularExpressions.RegexOptions.Multiline;
    private const System.Text.RegularExpressions.RegexOptions LinesIgnoreCase = Lines | System.Text.RegularExpressions.RegexOptions.IgnoreCase;
    private static readonly System.Text.RegularExpressions.Regex Slashes = new(@"/+|\\+");
    private static readonly System.Text.RegularExpressions.Regex BeforeTextures = new(@"^(.*?)\\textures\\", LinesIgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex LeadingBackslashes = new(@"^\\+", Lines);
    private static readonly System.Text.RegularExpressions.Regex NoTexturesPrefix = new(@"^(?!textures\\)", LinesIgnoreCase);

    /// <summary>The path C++ nifly stores after loading (<c>NifFile::TrimTexturePaths</c>, run on
    /// every texture set at load): one backslash between parts, nothing before the first
    /// <c>\textures\</c>, and a <c>textures\</c> prefix. Effect shaders' own texture fields are
    /// left as written, as C++ leaves them for a shader without a texture set.</summary>
    internal static string TrimTexturePath(string tex)
    {
        if (tex.Length == 0) return tex;
        tex = Slashes.Replace(tex, "\\");
        tex = BeforeTextures.Replace(tex, "");
        tex = LeadingBackslashes.Replace(tex, "");
        return NoTexturesPrefix.Replace(tex, "textures\\");
    }

    // ---- Skin (C++ GetShapeBoneList / GetShapeTransformSkinToBone / GetShapePartitions) ----

    public static NiSkinInstance? SkinInstance(NifFile nif, INiShape shape) => Block<NiSkinInstance>(nif, shape.SkinInstanceRef);

    /// <summary>Names of the skin's bones that resolve to nodes, in bone order.</summary>
    public static List<string> BoneNames(NifFile nif, INiShape shape)
    {
        var names = new List<string>();
        if (SkinInstance(nif, shape)?.Bones is not { } bones) return names;
        foreach (var index in bones.Indices)
        {
            if (index >= 0 && nif.GetBlock<NiNode>(index) is { } node)
                names.Add(Name(node) ?? "");
        }
        return names;
    }

    /// <summary>NiSkinData's transform for bone <paramref name="boneIndex"/>.</summary>
    public static bool SkinToBone(NifFile nif, INiShape shape, int boneIndex, out Xform xform)
    {
        xform = Xform.Identity;
        if (SkinInstance(nif, shape) is not { } skin || Block<NiSkinData>(nif, skin.Data) is not { } data) return false;
        if (data.BoneList == null || boneIndex < 0 || boneIndex >= data.NumBones || boneIndex >= data.BoneList.Count) return false;
        var t = data.BoneList[boneIndex].SkinTransform;
        xform = Xform.From(t.Translation, t.Rotation, t.Scale);
        return true;
    }

    /// <summary>The dismember partitions' body-part ids (padded as C++ pads them) and, per shape
    /// triangle, the index of the partition that holds it. False without a skin partition.</summary>
    public static bool Partitions(NifFile nif, INiShape shape, int userVersion,
        out List<ushort> partIds, out List<int> triParts)
    {
        partIds = new List<ushort>();
        triParts = new List<int>();
        var skin = SkinInstance(nif, shape);
        if (skin is BSDismemberSkinInstance dismember && dismember.Partitions != null)
            partIds.AddRange(dismember.Partitions.Select(p => (ushort)p.BodyPart));
        if (skin == null || Block<NiSkinPartition>(nif, skin.SkinPartition) is not { } partition) return false;

        Triangles(shape, out var tris);
        triParts = TriParts(partition, tris, builtFromPartitions: nif.Header.Version.IsSSE() && shape is BSTriShape);
        int count = partition.Partitions?.Count ?? 0;
        while (partIds.Count < count) partIds.Add((ushort)(userVersion >= 12 ? 32 : 0));
        return true;
    }

    /// <summary>Per shape triangle, the index of the partition holding it (C++
    /// <c>NiSkinPartition::PrepareTriParts</c>, from public data only).</summary>
    private static List<int> TriParts(NiSkinPartition partition, List<Triangle> tris, bool builtFromPartitions)
    {
        var partitions = partition.Partitions ?? new List<SkinPartition>();

        // An SSE skinned shape's triangles were built at load by concatenating each partition's
        // TrianglesCopy in order (Nifly's NifFile.PrepareData), filling triParts as it went; C++
        // PrepareTriParts returns that list untouched when its length matches.
        if (builtFromPartitions)
        {
            var concatenated = new List<int>();
            for (int p = 0; p < partitions.Count; p++)
                concatenated.AddRange(Enumerable.Repeat(p, partitions[p].TrianglesCopy?.Count ?? 0));
            if (concatenated.Count == tris.Count) return concatenated;
        }

        // Otherwise match each partition triangle to a shape triangle, both rotated so their
        // smallest index comes first (GenerateTriPartsFromTrueTriangles); unmatched stay 0.
        partition.PrepareTrueTriangles();
        partitions = partition.Partitions ?? partitions;
        var triParts = new List<int>(new int[tris.Count]);
        var shapeTriIndex = new Dictionary<(ushort, ushort, ushort), int>();
        for (int i = 0; i < tris.Count; i++)
        {
            var t = tris[i];
            t.Rotate();
            shapeTriIndex[(t.V1, t.V2, t.V3)] = i;
        }
        for (int p = 0; p < partitions.Count; p++)
        {
            foreach (var pt in partitions[p].TrianglesCopy ?? new List<Triangle>())
            {
                var t = pt;
                t.Rotate();
                if (shapeTriIndex.TryGetValue((t.V1, t.V2, t.V3), out int index)) triParts[index] = p;
            }
        }
        return triParts;
    }

    // ---- Header ----

    public static string BlockTypeName(NifFile nif, int id) => nif.Header.GetBlockTypeNameById(id) ?? "";
    public static int BlockCount(NifFile nif) => nif.Header.BlockCount;
}
