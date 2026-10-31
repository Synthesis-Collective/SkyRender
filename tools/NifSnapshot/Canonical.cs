using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace SkyRender.NifSnapshot;

/// <summary>
/// Writes an object graph as canonical text: every public field and readable property of a class,
/// every public field of a struct (struct properties are computed views such as Length or
/// Normalized and would recurse), dictionaries sorted by key, and floats as their exact bit
/// patterns. Two builds that produce the same BuiltMesh produce the same text, so the same hash.
/// </summary>
internal static class Canonical
{
    public static string Hash(object? value)
    {
        var sb = new StringBuilder();
        Write(sb, value, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..32];
    }

    public static string Text(object? value)
    {
        var sb = new StringBuilder();
        Write(sb, value, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);
        return sb.ToString();
    }

    /// <summary>The members <see cref="Write"/> visits on a class, in a stable order.</summary>
    public static IEnumerable<(string Name, Func<object, object?> Get)> Members(Type type)
    {
        foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(f => f.Name, StringComparer.Ordinal))
            yield return (f.Name, o => f.GetValue(o));
        if (type.IsValueType) yield break;
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
            yield return (p.Name, o => p.GetValue(o));
    }

    private static void Write(StringBuilder sb, object? value, HashSet<object> seen, int depth)
    {
        if (depth > 32) { sb.Append("<deep>"); return; }
        switch (value)
        {
            case null: sb.Append("null"); return;
            case float f: sb.Append("f").Append(BitConverter.SingleToInt32Bits(f).ToString("X8")); return;
            case double d: sb.Append("d").Append(BitConverter.DoubleToInt64Bits(d).ToString("X16")); return;
            case string s: sb.Append('"').Append(s.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"'); return;
            case bool b: sb.Append(b ? "T" : "F"); return;
            case Enum e: sb.Append(Convert.ToInt64(e, CultureInfo.InvariantCulture)); return;
        }
        var type = value.GetType();
        if (type.IsPrimitive || value is decimal)
        {
            sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
            return;
        }
        if (!type.IsValueType && !seen.Add(value)) { sb.Append("<cycle>"); return; }
        try
        {
            if (value is IDictionary dict)
            {
                var entries = new List<(string Key, object? Value)>();
                foreach (DictionaryEntry de in dict) entries.Add((Text(de.Key), de.Value));
                sb.Append('{');
                foreach (var (key, v) in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
                {
                    sb.Append(key).Append(':');
                    Write(sb, v, seen, depth + 1);
                    sb.Append(',');
                }
                sb.Append('}');
                return;
            }
            if (value is IEnumerable seq)
            {
                sb.Append('[');
                int n = 0;
                foreach (var item in seq)
                {
                    Write(sb, item, seen, depth + 1);
                    sb.Append(',');
                    n++;
                }
                sb.Append(']').Append(n);
                return;
            }
            sb.Append(type.Name).Append('(');
            foreach (var (name, get) in Members(type))
            {
                object? member;
                try { member = get(value); }
                catch (Exception ex) { member = "<threw " + ex.GetType().Name + ">"; }
                sb.Append(name).Append('=');
                Write(sb, member, seen, depth + 1);
                sb.Append(';');
            }
            sb.Append(')');
        }
        finally
        {
            if (!type.IsValueType) seen.Remove(value);
        }
    }
}
