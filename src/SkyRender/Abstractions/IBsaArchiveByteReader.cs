namespace SkyRender;

/// <summary>
/// Optional companion to <see cref="IBsaArchiveProvider"/>: a host that can hand over an archived
/// file's bytes implements this on the same object, and <see cref="GameAssetResolver"/> then serves
/// BSA assets from memory instead of extracting them to its cache directory (unless the host sets
/// <see cref="GameAssetResolver.ExtractBsaAssetsToDisk"/>). Hosts that don't implement it keep the
/// disk-extraction behavior unchanged.
///
/// <para>The resolved path of an in-memory asset (<see cref="AssetSource.ResolvedDiskPath"/>) still
/// looks like a path, so caches and logs key on it as before, but no file exists there
/// (<see cref="AssetSource.IsInMemory"/>). Host code that reads such a path itself must go through
/// <see cref="GameAssetResolver.OpenResolvedRead"/> or <see cref="GameAssetResolver.ReadResolvedBytes"/>.</para>
/// </summary>
public interface IBsaArchiveByteReader
{
    /// <summary>Reads <paramref name="subpath"/> from the BSA identified by
    /// <paramref name="containingBsaPath"/>. Same contract as
    /// <see cref="IBsaArchiveProvider.TryExtractToDisk"/>: the caller has already located the file
    /// and implementations MUST read from that exact archive. <paramref name="bytes"/> receives the
    /// whole decompressed file; <paramref name="error"/> a diagnostic when the result is
    /// <c>false</c>. May be called concurrently from several threads.</summary>
    bool TryReadBytes(string containingBsaPath, string subpath, out byte[]? bytes, out string? error);
}
