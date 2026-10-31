using System.Reflection;
using System.Runtime.InteropServices;
using nifly;

namespace SkyRender;

/// <summary>
/// Opens a NIF from a resolved path: from the file, or, for an in-memory BSA asset of
/// <paramref name="resolver"/> (see <see cref="IBsaArchiveByteReader"/>), from its bytes.
///
/// <para>Both routes reach the same parser: nifly's path <c>Load</c> wraps the file in an
/// <c>ifstream</c>, and its <c>vector&lt;unsigned char&gt;</c> constructor copies the bytes into a
/// <c>stringstream</c>; each then calls <c>Load(istream)</c>. niflysharp has no bulk way to fill a
/// <see cref="vectoruchar"/> (its constructors and <c>AddRange</c> make one interop call per byte,
/// about 28 ms for a 4.9 MB FaceGen NIF), so <see cref="ToNativeVector"/> fills it with a single copy
/// when it can verify the vector's memory layout, and falls back to the per-byte route otherwise.</para>
/// </summary>
public static class NifFileLoader
{
    /// <summary>Loads the NIF at <paramref name="path"/>, or returns null when it is missing,
    /// unreadable or not a valid NIF. The caller owns (disposes) the returned file.</summary>
    public static NifFile? TryLoad(string path, GameAssetResolver? resolver)
    {
        if (resolver != null && resolver.IsInMemoryPath(path))
        {
            byte[] bytes;
            try { bytes = resolver.ReadResolvedBytesShared(path); }
            catch (System.IO.IOException) { return null; }
            // nifly's byte constructor dereferences front() without an emptiness check.
            if (bytes.Length == 0) return null;
            using var vector = ToNativeVector(bytes);
            var fromBytes = new NifFile(vector);
            if (fromBytes.IsValid()) return fromBytes;
            fromBytes.Dispose();
            return null;
        }

        var nif = new NifFile();
        if (nif.Load(path) == 0) return nif;
        nif.Dispose();
        return null;
    }

    private static readonly FieldInfo? SwigPointerField =
        typeof(vectoruchar).GetField("swigCPtr", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>False once a bulk fill failed its layout check; every later call then goes
    /// straight to the per-byte route.</summary>
    private static volatile bool _bulkFillUsable = SwigPointerField?.FieldType == typeof(HandleRef);

    /// <summary>True while <see cref="ToNativeVector"/> fills vectors with a single copy.</summary>
    internal static bool BulkFillUsable => _bulkFillUsable;

    /// <summary>A native <c>std::vector&lt;unsigned char&gt;</c> holding <paramref name="bytes"/>.
    /// Sizes it in one native call (<see cref="vectoruchar.Repeat"/>), then copies the bytes into its
    /// buffer at once. The buffer address comes from the vector's own begin/end pointers (MSVC
    /// release layout: the first two pointer fields); they are trusted only after the length they
    /// span matches and sentinels written through the vector's indexer read back from that memory,
    /// so a different layout falls back to the per-byte constructor instead of writing anywhere.
    /// The caller disposes the result.</summary>
    internal static vectoruchar ToNativeVector(byte[] bytes)
    {
        if (_bulkFillUsable && bytes.Length > 0)
        {
            var vector = vectoruchar.Repeat(0, bytes.Length);
            try
            {
                if (TryGetBuffer(vector, bytes.Length, out IntPtr buffer))
                {
                    Marshal.Copy(bytes, 0, buffer, bytes.Length);
                    return vector;
                }
            }
            catch (Exception)
            {
                // Fall through to the per-byte route.
            }
            _bulkFillUsable = false;
            vector.Dispose();
        }
        return new vectoruchar(bytes);
    }

    private static bool TryGetBuffer(vectoruchar vector, int length, out IntPtr buffer)
    {
        buffer = IntPtr.Zero;
        if (SwigPointerField?.GetValue(vector) is not HandleRef handle || handle.Handle == IntPtr.Zero) return false;
        IntPtr begin = Marshal.ReadIntPtr(handle.Handle);
        IntPtr end = Marshal.ReadIntPtr(handle.Handle, IntPtr.Size);
        if (begin == IntPtr.Zero || (long)end - (long)begin != length || vector.Count != length) return false;

        // Each sentinel is read back before the next write (index 0 and length-1 coincide for a
        // one-byte vector); two values at the end, so a coincidental match can't pass.
        vector[0] = 0xA5;
        if (Marshal.ReadByte(begin) != 0xA5) return false;
        vector[length - 1] = 0x5A;
        if (Marshal.ReadByte(begin, length - 1) != 0x5A) return false;
        vector[length - 1] = 0xC3;
        if (Marshal.ReadByte(begin, length - 1) != 0xC3) return false;
        buffer = begin;
        return true;
    }
}
