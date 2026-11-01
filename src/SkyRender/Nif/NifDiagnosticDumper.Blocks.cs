using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using NiflySharp;
using NiflySharp.Blocks;

namespace SkyRender;

internal static partial class NifDiagnosticDumper
{
    // Conventional Bethesda texture-set slot meanings. The interpretation
    // depends on shader flags, but these are the names NifSkope uses in its UI.
    private static readonly string[] TextureSlotNames =
    {
        "Diffuse",         // 0
        "Normal",          // 1
        "Glow/SkinTint",   // 2
        "Height/Parallax", // 3
        "Environment",     // 4
        "EnvironmentMask", // 5
        "Tint/Inner",      // 6
        "Backlight/Specular", // 7
        "Slot8",           // 8 (rarely used)
    };

    // Plumbing every Nifly block exposes for its own reference bookkeeping, not file content.
    private static readonly HashSet<string> SkippedProperties =
        new(StringComparer.Ordinal) { "Pointers", "References", "ReferenceArrays", "StringRefs" };

    private const int MaxListItems = 8;

    // ════════════════════════════════════════════════════════════════════
    //  Per-block dump
    //
    //  Every public property of the block, read off the concrete type (the
    //  generated fields nif.xml defines plus Nifly's accessors), so each block
    //  type shows everything Nifly reads without a hand-written list. Flag
    //  words print as their enum names; long lists print their count and
    //  first entries.
    // ════════════════════════════════════════════════════════════════════

    private static void DumpOneBlock(StringBuilder sb, NifFile nif, int id, HashSet<string> texturePaths)
    {
        string typeName = BlockTypeName(nif, id);
        var blk = nif.Blocks[id];
        if (blk == null)
        {
            sb.AppendLine($"--- Block [{id}] {typeName} ---");
            sb.AppendLine("  <unresolved>");
            return;
        }

        string name = SafeStr(() => NifAccess.Name(blk));
        sb.Append("--- Block [").Append(id).Append("] ").Append(typeName);
        if (!string.IsNullOrEmpty(name)) sb.Append(" \"").Append(name).Append('"');
        sb.AppendLine(" ---");

        if (blk is BSShaderTextureSet texSet)
        {
            DumpTextureSet(sb, texSet, texturePaths);
            return;
        }
        if (blk is BSEffectShaderProperty effect)
        {
            // Effect shaders name their textures in their own fields; C++ nifly never trimmed these.
            foreach (var tex in new[] { effect.SourceTexture, effect.NormalTexture, effect.GreyscaleTexture, effect.EnvMapTexture, effect.EnvMaskTexture })
            {
                string? p = tex?.Content;
                if (!string.IsNullOrWhiteSpace(p)) texturePaths.Add(p);
            }
        }

        foreach (var prop in blk.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !SkippedProperties.Contains(p.Name))
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            string value;
            try { value = Describe(prop.GetValue(blk), depth: 0); }
            catch (Exception ex) { value = "<" + (ex.InnerException ?? ex).GetType().Name + ">"; }
            sb.Append("  ").Append(prop.Name.PadRight(28)).Append(' ').AppendLine(value);
        }
    }

    private static void DumpTextureSet(StringBuilder sb, BSShaderTextureSet t, HashSet<string> texturePaths)
    {
        var textures = t.Textures;
        if (textures == null) { sb.AppendLine("  textures:        <null>"); return; }
        sb.Append("  textures:        ").Append(textures.Count).AppendLine(" slots");
        for (int i = 0; i < textures.Count; i++)
        {
            string slotName = i < TextureSlotNames.Length ? TextureSlotNames[i] : $"Slot{i}";
            // The path the renderer uses: trimmed the way C++ nifly trimmed texture sets at load.
            string path = NifAccess.TrimTexturePath(textures[i]?.Content ?? "");
            sb.Append("    [").Append(i).Append("] ").Append(slotName.PadRight(18)).Append(" : ");
            if (string.IsNullOrEmpty(path)) sb.AppendLine("(empty)");
            else
            {
                sb.Append('"').Append(path).AppendLine("\"");
                texturePaths.Add(path);
            }
        }
    }

    /// <summary>One value as dump text: strings and refs by content, enums by name, lists by
    /// count and leading entries, structs by their public fields.</summary>
    private static string Describe(object? value, int depth)
    {
        switch (value)
        {
            case null: return "<null>";
            case string s: return '"' + s + '"';
            case NiString ns: return '"' + (ns.Content ?? "") + '"';
            case NiStringRef sr: return '"' + (sr.String ?? "") + '"';
            case INiRef r: return r.IsEmpty() ? "→ none" : "→ [" + r.Index + "]";
            case NiRefArray refs: return refs.Count + " refs [" + string.Join(", ", refs.Indices.Take(MaxListItems)) + (refs.Count > MaxListItems ? ", …" : "") + "]";
            case float f: return f.ToString("G6", CultureInfo.InvariantCulture);
            case double d: return d.ToString("G6", CultureInfo.InvariantCulture);
            case Enum e: return e.ToString() + " (0x" + Convert.ToUInt64(e, CultureInfo.InvariantCulture).ToString("X") + ")";
            case bool b: return b ? "true" : "false";
            case IFormattable when value.GetType().IsPrimitive: return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
        var type = value.GetType();
        if (value is IEnumerable items)
        {
            var list = items.Cast<object?>().Take(MaxListItems + 1).ToList();
            int count = value is ICollection c ? c.Count : list.Count;
            if (depth > 0) return count + " items";
            var head = list.Take(MaxListItems).Select(i => Describe(i, depth + 1));
            return count + " items [" + string.Join(", ", head) + (count > MaxListItems ? ", …" : "") + "]";
        }
        if (type.IsValueType && depth < 3)
        {
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            if (fields.Length > 0)
                return "(" + string.Join(", ", fields.Select(f => f.Name + "=" + Describe(f.GetValue(value), depth + 1))) + ")";
        }
        return value.ToString() ?? type.Name;
    }
}
