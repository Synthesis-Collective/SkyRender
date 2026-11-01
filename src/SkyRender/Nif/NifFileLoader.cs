using System.IO;

namespace SkyRender;

/// <summary>
/// Opens a NIF from a resolved path: from the file, or, for an in-memory BSA asset of
/// <paramref name="resolver"/> (see <see cref="IBsaArchiveByteReader"/>), from its bytes. Either
/// way the bytes are parsed by Nifly (ousnius's managed NIF library) into a <see cref="LoadedNif"/>.
/// </summary>
public static class NifFileLoader
{
    /// <summary>Loads the NIF at <paramref name="path"/>, or returns null when it is missing,
    /// unreadable or not a valid NIF.</summary>
    public static LoadedNif? TryLoad(string path, GameAssetResolver? resolver)
    {
        // The parse only reads the bytes, so an in-memory asset's shared buffer needs no copy.
        byte[]? bytes = Read(path, resolver, shared: true);
        return bytes == null ? null : NifAccess.Load(bytes);
    }

    /// <summary>The bytes of the file at <paramref name="path"/>, read through
    /// <paramref name="resolver"/> when it is one of its in-memory archive paths; null when it
    /// cannot be read. For hosts that parse NIFs with their own library but need SkyRender's
    /// resolved paths, which may name archive files that exist only in memory. The array is the
    /// caller's own.</summary>
    public static byte[]? TryReadBytes(string path, GameAssetResolver? resolver) => Read(path, resolver, shared: false);

    private static byte[]? Read(string path, GameAssetResolver? resolver, bool shared)
    {
        try
        {
            if (resolver != null && resolver.IsInMemoryPath(path))
                return shared ? resolver.ReadResolvedBytesShared(path) : resolver.ReadResolvedBytes(path);
            return File.ReadAllBytes(path);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
    }
}
