using System.Numerics;
using nifly;
using SkyRender;
using Xform = SkyRender.NifAccess.Xform;
using Vector3 = System.Numerics.Vector3;

namespace SkyRender.NifSnapshot;

/// <summary>
/// Loads one NIF with niflysharp (the old library) and with Nifly through NifAccess (the new one),
/// and compares every helper SkyRender reads meshes with: shape order and names, block types,
/// geometry, triangles, texture slots, transforms to the root, bones, skin-to-bone transforms,
/// bone world transforms and partitions. Floats compare bit for bit.
/// </summary>
internal static class AdapterDiff
{
    public static (string Status, List<string> Diffs, int Shapes) Compare(string path, byte[]? bytes, GameAssetResolver resolver)
    {
        var diffs = new List<string>();
        using var oldNif = NifFileLoader.TryLoad(path, resolver);
        var loaded = bytes != null ? NifAccess.Load(bytes) : null;
        if (oldNif == null && loaded == null) return ("bothfail", diffs, 0);
        if (oldNif == null) return ("oldfail", diffs, 0);
        if (loaded == null) return ("newfail", diffs, 0);
        var nif = loaded.File;

        void Diff(string what) { if (diffs.Count < 40) diffs.Add(what); }

        // Header
        var header = oldNif.GetHeader();
        uint oldBlocks = header.GetNumBlocks();
        if (oldBlocks != NifAccess.BlockCount(nif)) Diff($"blockCount {oldBlocks} vs {NifAccess.BlockCount(nif)}");
        for (uint b = 0; b < Math.Min(oldBlocks, (uint)NifAccess.BlockCount(nif)); b++)
        {
            string o = header.GetBlockTypeStringById(b) ?? "", n = NifAccess.BlockTypeName(nif, (int)b);
            if (o != n) Diff($"blockType[{b}] '{o}' vs '{n}'");
        }

        using var oldShapes = oldNif.GetShapes();
        var newShapes = NifAccess.Shapes(nif);
        if (oldShapes.Count != newShapes.Count) Diff($"shapeCount {oldShapes.Count} vs {newShapes.Count}");
        int count = Math.Min(oldShapes.Count, newShapes.Count);
        uint userVersion = header.GetVersion().User();

        for (int i = 0; i < count; i++)
        {
            var os = oldShapes[i];
            var ns = newShapes[i];
            string tag = $"shape[{i}]";
            string oName = os.name?.get() ?? "", nName = NifAccess.Name(ns) ?? "";
            if (oName != nName) Diff($"{tag} name '{oName}' vs '{nName}'");
            tag += $" '{oName}'";

            // Parent node
            string oParent = oldNif.GetParentNode(os)?.name?.get() ?? "<none>";
            string nParent = NifAccess.Name(NifAccess.ParentNode(nif, ns)) ?? "<none>";
            if (oParent != nParent) Diff($"{tag} parent '{oParent}' vs '{nParent}'");

            // Geometry
            using (var ov = oldNif.GetVertsForShape(os))
            {
                bool nHas = NifAccess.Vertices(nif, ns, out var nv);
                CompareVec3(tag + " verts", ov == null ? null : ov.Select(v => new Vector3(v.x, v.y, v.z)).ToArray(), nHas || nv.Length > 0 ? nv : null, Diff);
            }
            using (var o = oldNif.GetNormalsForShape(os)) CompareVec3(tag + " normals", Vec3(o), NifAccess.Normals(nif, ns), Diff);
            using (var o = oldNif.GetTangentsForShape(os)) CompareVec3(tag + " tangents", Vec3(o), NifAccess.Tangents(nif, ns), Diff);
            using (var o = oldNif.GetBitangentsForShape(os)) CompareVec3(tag + " bitangents", Vec3(o), NifAccess.Bitangents(nif, ns), Diff);
            using (var o = oldNif.GetUvsForShape(os))
                CompareFloats(tag + " uvs", o?.SelectMany(u => new[] { u.u, u.v }).ToArray(),
                    NifAccess.Uvs(nif, ns)?.SelectMany(u => new[] { u.X, u.Y }).ToArray(), Diff);
            using (var oc = new vectorColor4())
            {
                bool oHas = oldNif.GetColorsForShape(os, oc);
                CompareFloats(tag + " colors", oHas ? oc.SelectMany(c => new[] { c.r, c.g, c.b, c.a }).ToArray() : null,
                    NifAccess.Colors(nif, ns)?.SelectMany(c => new[] { c.X, c.Y, c.Z, c.W }).ToArray(), Diff);
            }
            using (var ot = new vectorTriangle())
            {
                bool oHas = os.GetTriangles(ot);
                bool nHas = NifAccess.Triangles(ns, out var nt);
                if (oHas != nHas) Diff($"{tag} hasTriangles {oHas} vs {nHas}");
                var oa = ot.SelectMany(t => new[] { (int)t.p1, (int)t.p2, (int)t.p3 }).ToArray();
                var na = nt.SelectMany(t => new[] { (int)t.V1, (int)t.V2, (int)t.V3 }).ToArray();
                if (!oa.SequenceEqual(na)) Diff($"{tag} triangles {oa.Length / 3} vs {na.Length / 3}{FirstMismatch(oa, na)}");
            }

            // Texture slots
            for (int slot = 0; slot < 9; slot++)
            {
                string o = oldNif.GetTexturePathByIndex(os, (uint)slot) ?? "", n = NifAccess.TexturePath(nif, ns, slot);
                if (o != n) Diff($"{tag} texture[{slot}] '{o}' vs '{n}'");
            }

            // Transform to the root
            CompareXform(tag + " toGlobal", OldToGlobal(oldNif, os), NifAccess.TransformToGlobal(nif, ns), Diff);

            // Skin
            using var oldBones = new vectorstring();
            uint boneCount = oldNif.GetShapeBoneList(os, oldBones);
            var newBones = NifAccess.BoneNames(nif, ns);
            if (!oldBones.SequenceEqual(newBones)) Diff($"{tag} bones [{oldBones.Count}] vs [{newBones.Count}]");
            for (int b = 0; b < boneCount; b++)
            {
                using var ob = new MatTransform();
                bool oHas = oldNif.GetShapeBoneTransform(os, (uint)b, ob);
                bool nHas = NifAccess.SkinToBone(nif, ns, b, out var nb);
                if (oHas != nHas) Diff($"{tag} skinToBone[{b}] has {oHas} vs {nHas}");
                else if (oHas) CompareXform($"{tag} skinToBone[{b}]", ob, nb, Diff);

                string bone = oldBones[b];
                using var ow = new MatTransform();
                bool oFound = oldNif.GetNodeTransformToGlobal(bone, ow);
                bool nFound = NifAccess.NodeTransformToGlobal(nif, bone, out var nw);
                if (oFound != nFound) Diff($"{tag} boneGlobal '{bone}' found {oFound} vs {nFound}");
                else if (oFound) CompareXform($"{tag} boneGlobal '{bone}'", ow, nw, Diff);
            }
            using (var info = new NiVectorBSDismemberSkinInstancePartitionInfo())
            using (var triParts = new vectorint())
            {
                bool oHas;
                try { oHas = oldNif.GetShapePartitions(os, info, triParts); } catch { oHas = false; }
                bool nHas = NifAccess.Partitions(nif, ns, (int)userVersion, out var nIds, out var nTri);
                if (oHas != nHas) Diff($"{tag} partitions has {oHas} vs {nHas}");
                else if (oHas)
                {
                    using var items = info.items();
                    var oIds = items.Select(p => p.partID).ToList();
                    if (!oIds.SequenceEqual(nIds)) Diff($"{tag} partIds [{string.Join(",", oIds)}] vs [{string.Join(",", nIds)}]");
                    var oTri = triParts.ToArray();
                    if (!oTri.SequenceEqual(nTri)) Diff($"{tag} triParts {oTri.Length} vs {nTri.Count}{FirstMismatch(oTri, nTri.ToArray())}");
                }
            }
        }
        return (diffs.Count == 0 ? "same" : "diff", diffs, count);
    }

    private static MatTransform OldToGlobal(NifFile nif, NiAVObject obj)
    {
        var result = obj.GetTransformToParent();
        for (var parent = nif.GetParentNode(obj); parent != null; parent = nif.GetParentNode(parent))
            result = parent.GetTransformToParent().ComposeTransforms(result);
        return result;
    }

    private static Vector3[]? Vec3(vectorVector3? v) => v?.Select(p => new Vector3(p.x, p.y, p.z)).ToArray();

    private static void CompareXform(string what, MatTransform o, Xform n, Action<string> diff)
    {
        using var ex = new nifly.Vector3 { x = 1 };
        using var ey = new nifly.Vector3 { y = 1 };
        using var ez = new nifly.Vector3 { z = 1 };
        using var c0 = o.rotation.opMult(ex);
        using var c1 = o.rotation.opMult(ey);
        using var c2 = o.rotation.opMult(ez);
        var nc0 = n.Rotate(Vector3.UnitX); var nc1 = n.Rotate(Vector3.UnitY); var nc2 = n.Rotate(Vector3.UnitZ);
        var of = new[] { o.translation.x, o.translation.y, o.translation.z, c0.x, c0.y, c0.z, c1.x, c1.y, c1.z, c2.x, c2.y, c2.z, o.scale };
        var nf = new[] { n.T.X, n.T.Y, n.T.Z, nc0.X, nc0.Y, nc0.Z, nc1.X, nc1.Y, nc1.Z, nc2.X, nc2.Y, nc2.Z, n.S };
        CompareFloats(what, of, nf, diff);
    }

    private static void CompareVec3(string what, Vector3[]? o, Vector3[]? n, Action<string> diff) =>
        CompareFloats(what, o?.SelectMany(v => new[] { v.X, v.Y, v.Z }).ToArray(), n?.SelectMany(v => new[] { v.X, v.Y, v.Z }).ToArray(), diff);

    private static void CompareFloats(string what, float[]? o, float[]? n, Action<string> diff)
    {
        // Null and empty read the same to NifMeshBuilder (it checks the count against the vertices).
        if ((o == null || o.Length == 0) && (n == null || n.Length == 0)) return;
        if (o == null || n == null) { diff($"{what} null {o == null} vs {n == null}"); return; }
        if (o.Length != n.Length) { diff($"{what} length {o.Length} vs {n.Length}"); return; }
        int bad = 0, first = -1; float maxAbs = 0;
        for (int i = 0; i < o.Length; i++)
        {
            if (BitConverter.SingleToInt32Bits(o[i]) == BitConverter.SingleToInt32Bits(n[i])) continue;
            if (float.IsNaN(o[i]) && float.IsNaN(n[i])) continue;   // payloads differ; any NaN renders alike
            if (first < 0) first = i;
            bad++;
            maxAbs = MathF.Max(maxAbs, MathF.Abs(o[i] - n[i]));
        }
        if (bad > 0) diff($"{what} {bad}/{o.Length} differ, max |d| {maxAbs:G4}, first [{first}] {o[first]:R} vs {n[first]:R}");
    }

    private static string FirstMismatch(int[] a, int[] b)
    {
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (a[i] != b[i]) return $", first [{i}] {a[i]} vs {b[i]}";
        return "";
    }
}
