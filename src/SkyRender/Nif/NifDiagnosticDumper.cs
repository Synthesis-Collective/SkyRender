using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NiflySharp;
using NiflySharp.Blocks;

namespace SkyRender;

/// <summary>
/// NifSkope-style diagnostic text dump of a loaded NIF.
///
/// Emits the file header, every block in the file, the scene-graph hierarchy,
/// and DDS metadata (size, format, mip count) for every referenced texture —
/// formatted to resemble NifSkope's block tree so a NifSkope user can
/// cross-check values quickly.
///
/// Output flows through <see cref="IRenderLogger.LogMessage"/>, the
/// same channel as the rest of SkyRender's verbose logging.
///
/// Gated by both <see cref="FULL_LOGGING"/> (compile-time, developer toggle)
/// and <see cref="RenderLogGate.Verbose"/> (runtime checkbox). With
/// <c>FULL_LOGGING = false</c> the dumper emits nothing regardless of the
/// runtime flag.
/// </summary>
internal static partial class NifDiagnosticDumper
{
    /// <summary>
    /// Master toggle for the full block dump. Set to <c>true</c> while
    /// debugging shader / texture / skinning issues; flip back to <c>false</c>
    /// for normal runs. Kept as a const so the JIT can drop the dump path
    /// entirely when off.
    /// </summary>
    internal const bool FULL_LOGGING = true;

    /// <summary>
    /// Emits a full text dump of the given NIF if both <see cref="FULL_LOGGING"/>
    /// and runtime verbose logging are enabled. Catches all exceptions — a
    /// malformed NIF must never break mesh loading.
    /// </summary>
    public static void DumpIfEnabled(
        NifFile nif,
        string nifPath,
        RenderLogGate? gate,
        IRenderLogger? logger,
        GameAssetResolver? assetResolver)
    {
        // Compile-time const gate — flipping FULL_LOGGING off lets the JIT
        // strip the entire dump path. CS0162 is expected when const is true.
#pragma warning disable CS0162
        if (!FULL_LOGGING) return;
#pragma warning restore CS0162
        if (gate == null || !gate.Verbose) return;
        if (logger == null || nif == null) return;

        try
        {
            Dump(nif, nifPath, logger, assetResolver);
        }
        catch (Exception ex)
        {
            logger.LogMessage($"[NifDump] FATAL during dump: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Internal (not private) so NifDumpApi can drive the dump gate-free for
    // standalone tooling.
    internal static void Dump(NifFile nif, string nifPath, IRenderLogger logger, GameAssetResolver? assetResolver)
    {
        var sb = new StringBuilder(8192);
        sb.AppendLine("════════════════════════════════════════════════════════════════════════");
        sb.AppendLine($" NIF DUMP: {nifPath}");
        sb.AppendLine("════════════════════════════════════════════════════════════════════════");

        DumpHeader(sb, nifPath, nif, assetResolver);
        FlushFlush(logger, sb);

        var texturePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        DumpBlockIndex(sb, nif, logger);
        FlushFlush(logger, sb);

        DumpSceneGraph(sb, nif);
        FlushFlush(logger, sb);

        DumpAllBlockDetails(sb, nif, texturePaths, logger);
        FlushFlush(logger, sb);

        if (assetResolver != null)
        {
            DumpTextures(sb, texturePaths, assetResolver, logger);
            FlushFlush(logger, sb);
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("=== Textures ===  (no GameAssetResolver supplied — paths only)");
            foreach (var p in SortedSet(texturePaths))
                sb.AppendLine($"  {p}");
            FlushFlush(logger, sb);
        }

        sb.AppendLine("════════════════════════════════════════════════════════════════════════");
        sb.AppendLine($" END NIF DUMP: {nifPath}");
        sb.AppendLine("════════════════════════════════════════════════════════════════════════");
        FlushFlush(logger, sb);
    }

    // Keep the in-app log panel responsive: emit each section as a single
    // multi-line LogMessage rather than one call per line.
    private static void FlushFlush(IRenderLogger logger, StringBuilder sb)
    {
        if (sb.Length == 0) return;
        // Trim a single trailing newline so the log panel doesn't double-space.
        if (sb.Length > 0 && sb[sb.Length - 1] == '\n') sb.Length--;
        if (sb.Length > 0 && sb[sb.Length - 1] == '\r') sb.Length--;
        logger.LogMessage(sb.ToString());
        sb.Clear();
    }

    private static IEnumerable<string> SortedSet(HashSet<string> set)
    {
        var list = new List<string>(set);
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    // ════════════════════════════════════════════════════════════════════
    //  Section 1 — Header
    // ════════════════════════════════════════════════════════════════════

    private static void DumpHeader(StringBuilder sb, string nifPath, NifFile nif, GameAssetResolver? assetResolver)
    {
        sb.AppendLine();
        sb.AppendLine("=== Header ===");
        long fileBytes = assetResolver?.GetResolvedLength(nifPath)
            ?? (File.Exists(nifPath) ? new FileInfo(nifPath).Length : -1);
        sb.AppendLine($"  Path:           {nifPath}");
        sb.AppendLine($"  File size:      {(fileBytes >= 0 ? fileBytes.ToString("N0") + " bytes" : "<unknown>")}");

        var header = nif.Header;
        try
        {
            var version = header.Version;
            sb.AppendLine($"  Version string: {SafeStr(version.GetVersionInfo)}");
            sb.AppendLine($"  File version:   {SafeStr(() => version.FileVersion.ToString())}");
            sb.AppendLine($"  User version:   {SafeStr(() => version.UserVersion.ToString())}");
            sb.AppendLine($"  Stream version: {SafeStr(() => version.StreamVersion.ToString())}");
            string game = "?";
            try
            {
                if (version.IsSSE()) game = "Skyrim SE";
                else if (version.IsSK()) game = "Skyrim LE";
                else if (version.IsFO4()) game = "Fallout 4";
                else if (version.IsFO76()) game = "Fallout 76";
                else if (version.IsFO3()) game = "Fallout 3 / NV";
                else if (version.IsOB()) game = "Oblivion";
                else if (version.IsBethesda()) game = "Bethesda (other)";
            }
            catch { }
            sb.AppendLine($"  Game:           {game}");
        }
        catch (Exception ex) { sb.AppendLine($"  <version read failed: {ex.Message}>"); }

        sb.AppendLine($"  Creator:        {SafeStr(() => header.Creator?.Content)}");
        sb.AppendLine($"  Export info:    {SafeStr(() => string.Join(" | ", new[] { header.ExportInfo1?.Content, header.ExportInfo2?.Content, header.ExportInfo3?.Content }.Where(s => !string.IsNullOrEmpty(s))))}");
        sb.AppendLine($"  Block count:    {SafeStr(() => header.BlockCount.ToString())}");
        sb.AppendLine($"  Unknown blocks: {SafeStr(() => nif.HasUnknownBlocks ? "yes (kept as written, shown by their real type)" : "none")}");
    }

    // ════════════════════════════════════════════════════════════════════
    //  Section 2 — Flat block index
    // ════════════════════════════════════════════════════════════════════

    private static void DumpBlockIndex(StringBuilder sb, NifFile nif, IRenderLogger logger)
    {
        sb.AppendLine();
        sb.AppendLine("=== Block Index ===");
        for (int i = 0; i < nif.Blocks.Count; i++)
        {
            string typeName = BlockTypeName(nif, i);
            string raw = SafeStr(() => NifAccess.Name(nif.Blocks[i]));
            string name = !string.IsNullOrEmpty(raw) ? $"  \"{raw}\"" : "";
            sb.Append("  [").Append(i.ToString().PadLeft(4)).Append("] ").Append(typeName).AppendLine(name);

            // Flush every 256 lines so very large NIFs don't build a 200KB string
            if (sb.Length > 32768) FlushFlush(logger, sb);
        }
    }

    /// <summary>The block's on-disk type from the header's table: an unknown block's C# type is
    /// NiUnknown whatever the file says it is.</summary>
    private static string BlockTypeName(NifFile nif, int id) => SafeStr(() => NifAccess.BlockTypeName(nif, id));

    // ════════════════════════════════════════════════════════════════════
    //  Section 3 — Scene graph
    // ════════════════════════════════════════════════════════════════════

    private static void DumpSceneGraph(StringBuilder sb, NifFile nif)
    {
        sb.AppendLine();
        sb.AppendLine("=== Scene Graph ===");

        NiNode? root = null;
        try { root = nif.GetRootNode(); } catch { }
        if (root == null)
        {
            sb.AppendLine("  <no root node>");
            return;
        }

        var visited = new HashSet<int>();
        DumpNodeRecursive(sb, nif, root, indent: 0, visited);
    }

    private const int MaxSceneDepth = 16;

    private static void DumpNodeRecursive(StringBuilder sb, NifFile nif, INiObject obj, int indent, HashSet<int> visited)
    {
        if (obj == null) return;
        if (indent > MaxSceneDepth)
        {
            sb.Append(new string(' ', indent * 2)).AppendLine("… <max depth reached>");
            return;
        }

        int id = NifAccess.BlockId(nif, obj);
        if (id >= 0 && !visited.Add(id))
        {
            sb.Append(new string(' ', indent * 2)).AppendLine($"… <cycle to block [{id}]>");
            return;
        }

        string typeName = id >= 0 ? BlockTypeName(nif, id) : "?";
        string name = SafeStr(() => NifAccess.Name(obj));

        var line = new StringBuilder();
        line.Append(new string(' ', indent * 2));
        line.Append('[').Append(id < 0 ? "?" : id.ToString()).Append("] ");
        line.Append(typeName);
        if (!string.IsNullOrEmpty(name)) line.Append(" \"").Append(name).Append('"');

        try
        {
            var t = obj switch
            {
                INiShape s => NifAccess.TransformToParent(s),
                NiAVObject av => NifAccess.TransformToParent(av),
                _ => (NifAccess.Xform?)null,
            };
            if (t is { } x)
            {
                line.AppendFormat("  T=({0:F2},{1:F2},{2:F2})", x.T.X, x.T.Y, x.T.Z);
                line.AppendFormat("  S={0:F3}", x.S);
            }
        }
        catch { }

        if (obj is INiShape shape)
        {
            static void Ref(StringBuilder line, string label, INiRef? r)
            {
                if (r != null && !r.IsEmpty()) line.Append("  ").Append(label).Append("→[").Append(r.Index).Append(']');
            }
            try { Ref(line, "shader", shape.ShaderPropertyRef); } catch { }
            try { Ref(line, "alpha", shape.AlphaPropertyRef); } catch { }
            try { Ref(line, "skin", shape.SkinInstanceRef); } catch { }
        }

        sb.AppendLine(line.ToString());

        if (obj is NiNode node && node.Children != null)
        {
            foreach (int childId in node.Children.Indices)
            {
                if (childId < 0) continue;
                INiObject? child = null;
                try { child = nif.GetBlock(childId); } catch { }
                if (child is NiAVObject or INiShape) DumpNodeRecursive(sb, nif, child, indent + 1, visited);
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Section 4 — Per-block details (see NifDiagnosticDumper.Blocks.cs)
    // ════════════════════════════════════════════════════════════════════

    private static void DumpAllBlockDetails(StringBuilder sb, NifFile nif, HashSet<string> texturePaths, IRenderLogger logger)
    {
        sb.AppendLine();
        sb.AppendLine("=== Block Details ===");
        for (int i = 0; i < nif.Blocks.Count; i++)
        {
            try { DumpOneBlock(sb, nif, i, texturePaths); }
            catch (Exception ex) { sb.AppendLine($"  <block [{i}] dump failed: {ex.Message}>"); }

            if (sb.Length > 32768) FlushFlush(logger, sb);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════════════════════════════════

    private static string SafeStr(Func<string?> f)
    {
        try { return f() ?? ""; }
        catch { return "<err>"; }
    }
}
