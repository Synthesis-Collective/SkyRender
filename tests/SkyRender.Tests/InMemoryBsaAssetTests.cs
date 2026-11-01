using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace SkyRender.Tests;

/// <summary>
/// BSA assets served from memory (<see cref="IBsaArchiveByteReader"/>) instead of extracted:
/// nothing is written, every reader sees the same bytes as from a file, failures stay terminal, and
/// hosts without the reader keep extracting.
/// </summary>
public sealed class InMemoryBsaAssetTests : IDisposable
{
    private const string TexturePath = @"textures\test\diffuse.dds";
    private const string CompressedPath = @"textures\test\compressed.dds";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SkyRenderInMemoryTests", Guid.NewGuid().ToString("N"));
    private readonly string _data, _cache, _bsa;

    public InMemoryBsaAssetTests()
    {
        _data = Directory.CreateDirectory(Path.Combine(_root, "Data")).FullName;
        _cache = Path.Combine(_root, "Cache");
        // A real file so the resolver can read the archive's write time.
        _bsa = Path.Combine(_data, "Test.bsa");
        File.WriteAllBytes(_bsa, new byte[] { 0 });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void ReaderHostServesFromMemoryAndWritesNothing()
    {
        var bsa = Archives(true).With(TexturePath, UncompressedDdsBytes());
        var resolver = Resolver(bsa);

        var source = resolver.ResolveAssetSource(TexturePath);

        Assert.Equal(AssetOriginKind.Bsa, source.Kind);
        Assert.True(source.IsInMemory);
        Assert.True(resolver.ServesBsaAssetsFromMemory);
        Assert.StartsWith(Path.Combine(_cache, "memory") + Path.DirectorySeparatorChar, source.ResolvedDiskPath);
        Assert.False(File.Exists(source.ResolvedDiskPath));
        Assert.False(Directory.Exists(_cache));
        Assert.True(resolver.AssetFileExists(source.ResolvedDiskPath));
        Assert.Equal(UncompressedDdsBytes(), resolver.ReadResolvedBytes(source.ResolvedDiskPath!));
        Assert.Equal(0, bsa.Extractions);
        Assert.Equal(File.GetLastWriteTimeUtc(_bsa).Ticks, resolver.GetResolvedLastWriteTicks(source.ResolvedDiskPath!));
    }

    [Fact]
    public void HostWithoutReaderStillExtracts()
    {
        var bsa = Archives(false).With(TexturePath, UncompressedDdsBytes());
        var resolver = Resolver(bsa);

        var source = resolver.ResolveAssetSource(TexturePath);

        Assert.False(source.IsInMemory);
        Assert.False(resolver.ServesBsaAssetsFromMemory);
        Assert.True(File.Exists(source.ResolvedDiskPath));
        Assert.Equal(UncompressedDdsBytes(), File.ReadAllBytes(source.ResolvedDiskPath!));
        Assert.Equal(1, bsa.Extractions);
    }

    [Fact]
    public void ExtractSwitchForcesDiskEvenWithReader()
    {
        var bsa = Archives(true).With(TexturePath, UncompressedDdsBytes());
        var resolver = Resolver(bsa, extract: true);

        var source = resolver.ResolveAssetSource(TexturePath);

        Assert.False(source.IsInMemory);
        Assert.True(File.Exists(source.ResolvedDiskPath));
        Assert.Equal(1, bsa.Extractions);
        Assert.Equal(0, bsa.Reads);
    }

    [Fact]
    public void ReadFailureAtResolveIsTerminalNotFoundInScopedChain()
    {
        // The mod's archive has the file but can't read it; vanilla also has it. Searching on would
        // substitute vanilla's copy for a file known to exist in the mod, so the result is NotFound,
        // exactly as for a failed extraction.
        var modBsa = Path.Combine(_data, "Mod.bsa");
        File.WriteAllBytes(modBsa, new byte[] { 0 });
        var bsa = Archives(true).With(TexturePath, UncompressedDdsBytes());
        bsa.Add(modBsa, TexturePath, UncompressedDdsBytes());
        bsa.FailReadsFrom = modBsa;
        var resolver = Resolver(bsa);
        var scopes = new[]
        {
            new RenderScope(_data, new[] { "Test.esm" }),
            new RenderScope(_data, new[] { "Mod.esp" }),
        };

        using (resolver.PushScopes(scopes, null, vanillaLooseOverridesBsa: true, vanillaLooseOverridesModLoose: false))
            Assert.Equal(AssetOriginKind.NotFound, resolver.ResolveAssetSource(TexturePath).Kind);
    }

    [Fact]
    public void EvictedFilesAreReReadFromTheArchive()
    {
        var a = Enumerable.Repeat((byte)1, 80).ToArray();
        var b = Enumerable.Repeat((byte)2, 80).ToArray();
        var bsa = Archives(true).With(@"meshes\a.nif", a).With(@"meshes\b.nif", b);
        var resolver = Resolver(bsa, budget: 100);

        string pathA = resolver.ResolveAssetPath(@"meshes\a.nif")!;
        string pathB = resolver.ResolveAssetPath(@"meshes\b.nif")!; // evicts a
        Assert.Equal(2, bsa.Reads);

        Assert.Equal(a, resolver.ReadResolvedBytes(pathA)); // re-read, evicts b
        Assert.Equal(b, resolver.ReadResolvedBytes(pathB)); // re-read
        Assert.Equal(b, resolver.ReadResolvedBytes(pathB)); // store hit

        var stats = resolver.GetBsaAssetStats();
        Assert.Equal(4, bsa.Reads);
        Assert.Equal(2, stats.Materialized);
        Assert.Equal(2, stats.ReReads);
        Assert.Equal(1, stats.StoreHits);
        Assert.True(stats.StoreResidentBytes <= 100);
    }

    [Fact]
    public void ReReadFailureSurfacesAsIOException()
    {
        var bsa = Archives(true).With(@"meshes\a.nif", new byte[80]).With(@"meshes\b.nif", new byte[80]);
        var resolver = Resolver(bsa, budget: 100);
        string pathA = resolver.ResolveAssetPath(@"meshes\a.nif")!;
        resolver.ResolveAssetPath(@"meshes\b.nif");
        bsa.FailReadsFrom = _bsa;

        Assert.Throws<IOException>(() => resolver.OpenResolvedRead(pathA));
        Assert.Null(NifFileLoader.TryLoad(pathA, resolver));
    }

    [Fact]
    public void ClearExtractedFilesForgetsInMemoryAssets()
    {
        var bsa = Archives(true).With(TexturePath, UncompressedDdsBytes());
        var resolver = Resolver(bsa);
        string path = resolver.ResolveAssetPath(TexturePath)!;

        resolver.ClearExtractedFiles();

        Assert.False(resolver.AssetFileExists(path));
        Assert.Equal(path, resolver.ResolveAssetPath(TexturePath)); // same key after re-resolve
        Assert.Equal(2, bsa.Reads);
    }

    [Fact]
    public void DecodedPixelsMatchLooseFile()
    {
        var fromMemory = new AssetCache(Resolver(Archives(true).With(TexturePath, UncompressedDdsBytes())), new NullLogger(), new RenderLogGate());
        var fromLoose = new AssetCache(Resolver(Archives(false)), new NullLogger(), new RenderLogGate());
        Directory.CreateDirectory(Path.Combine(_data, "textures", "test"));
        File.WriteAllBytes(Path.Combine(_data, TexturePath), UncompressedDdsBytes());

        var memoryPixels = fromMemory.GetOrLoadDdsPixels(TexturePath);
        var loosePixels = fromLoose.GetOrLoadDdsPixels(TexturePath);

        Assert.NotNull(memoryPixels);
        Assert.NotNull(loosePixels);
        Assert.Equal(loosePixels!.Value.Width, memoryPixels!.Value.Width);
        Assert.Equal(loosePixels.Value.Height, memoryPixels.Value.Height);
        Assert.Equal(loosePixels.Value.Data, memoryPixels.Value.Data);
        Assert.Equal(fromLoose.IsFullyTransparent(TexturePath), fromMemory.IsFullyTransparent(TexturePath));
    }

    [Fact]
    public void CompressedDdsReadsMatchLooseFile()
    {
        var resolver = Resolver(Archives(true).With(CompressedPath, CompressedDdsBytes()));
        var cache = new AssetCache(resolver, new NullLogger(), new RenderLogGate());
        string memoryPath = resolver.ResolveAssetPath(CompressedPath)!;
        string loosePath = Path.Combine(_root, "loose.dds");
        File.WriteAllBytes(loosePath, CompressedDdsBytes());

        var memoryStatus = cache.ReadCompressedDds(memoryPath, out var memoryDds, out _, 1);
        var looseStatus = CompressedDds.Read(loosePath, out var looseDds, out _);

        Assert.Equal(DdsReadStatus.Eligible, looseStatus);
        Assert.Equal(looseStatus, memoryStatus);
        Assert.Equal(looseDds!.Format, memoryDds!.Format);
        Assert.Equal(looseDds.PayloadBytes, memoryDds.PayloadBytes);
        Assert.Equal(DdsReadStatus.Eligible, cache.InspectCompressedDds(memoryPath, 1, out _));
    }

    [Fact]
    public void ConcurrentResolvesReadEachAssetOnceAndAgree()
    {
        var bsa = Archives(true);
        for (int i = 0; i < 16; i++) bsa.With($@"meshes\m{i}.nif", Enumerable.Repeat((byte)i, 1000 + i).ToArray());
        var resolver = Resolver(bsa);
        var seen = new ConcurrentDictionary<int, string>();

        Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 8 }, n =>
        {
            int i = n % 16;
            string path = resolver.ResolveAssetPath($@"meshes\m{i}.nif")!;
            Assert.Equal(path, seen.GetOrAdd(i, path));
            using var stream = resolver.OpenResolvedRead(path);
            Assert.Equal(1000 + i, stream.Length);
            Assert.Equal(i, stream.ReadByte());
        });

        Assert.Equal(16, bsa.Reads);
        Assert.Equal(16, resolver.GetBsaAssetStats().Materialized);
    }

    [Fact]
    public void TryReadBytesHandsBackTheCallersOwnCopy()
    {
        var bytes = new byte[] { 10, 20, 30, 40 };
        var bsa = Archives(true).With(@"meshes\copy.nif", bytes);
        var resolver = Resolver(bsa);
        string path = resolver.ResolveAssetPath(@"meshes\copy.nif")!;

        var first = NifFileLoader.TryReadBytes(path, resolver)!;
        Assert.Equal(bytes, first);
        first[0] = 99;   // a host scribbling on its copy must not reach the resolver's cache
        Assert.Equal(bytes, NifFileLoader.TryReadBytes(path, resolver));
        Assert.Null(NifFileLoader.TryReadBytes(Path.Combine(_data, "missing.nif"), resolver));
    }

    [Fact]
    public void NifFromMemoryBuildsSameMeshesAsFromFile()
    {
        // Opt-in: the repo ships no NIFs. Set SKYRENDER_TEST_NIF to any mesh NIF.
        string? nifPath = Environment.GetEnvironmentVariable("SKYRENDER_TEST_NIF");
        if (nifPath == null || !File.Exists(nifPath)) return;

        var bsa = Archives(true).With(@"meshes\test.nif", File.ReadAllBytes(nifPath));
        var cache = new AssetCache(Resolver(bsa), new NullLogger(), new RenderLogGate());
        string memoryPath = cache.ResolveAssetPath(@"meshes\test.nif")!;

        var fromMemory = cache.MeshBuilder.BuildFromFile(memoryPath);
        var fromFile = cache.MeshBuilder.BuildFromFile(nifPath);

        // Raw parse (works for shape-less NIFs such as skeletons), then the built meshes.
        using (var rawMemory = NifFileLoader.TryLoad(memoryPath, cache.Resolver))
        using (var rawFile = NifFileLoader.TryLoad(nifPath, null))
        {
            Assert.NotNull(rawFile);
            Assert.NotNull(rawMemory);
            Assert.Equal(rawFile!.File.Header.BlockCount, rawMemory!.File.Header.BlockCount);
        }
        Assert.Equal(fromFile.Count, fromMemory.Count);
        for (int i = 0; i < fromFile.Count; i++)
        {
            Assert.Equal(fromFile[i].Positions, fromMemory[i].Positions);
            Assert.Equal(fromFile[i].Indices, fromMemory[i].Indices);
            Assert.Equal(fromFile[i].TextureCoordinates, fromMemory[i].TextureCoordinates);
        }
        Assert.True(cache.MeshBuilder.SurveyNif(memoryPath).LoadOk);
        Assert.Equal(new FileInfo(nifPath).Length, cache.MeshBuilder.AnalyzeFaceGen(memoryPath, measureGeometry: false).FileSizeBytes);
    }

    [Fact]
    public void InvalidOrEmptyNifBytesLoadAsNull()
    {
        var bsa = Archives(true).With(@"meshes\garbage.nif", new byte[] { 1, 2, 3, 4 }).With(@"meshes\empty.nif", Array.Empty<byte>());
        var resolver = Resolver(bsa);

        Assert.Null(NifFileLoader.TryLoad(resolver.ResolveAssetPath(@"meshes\garbage.nif")!, resolver));
        Assert.Null(NifFileLoader.TryLoad(resolver.ResolveAssetPath(@"meshes\empty.nif")!, resolver));
    }

    private FakeArchives Archives(bool reader) => reader ? new ReadingArchives(_bsa) : new FakeArchives(_bsa);

    private GameAssetResolver Resolver(FakeArchives bsa, bool extract = false, long budget = 128L * 1024 * 1024) =>
        new(new Folder(_data), bsa, new RenderLogGate(), new NullLogger(), _cache)
        {
            ExtractBsaAssetsToDisk = extract,
            InMemoryAssetBudgetBytes = budget,
        };

    /// <summary>4×4 uncompressed A8R8G8B8 DDS with distinct texels and partial alpha.</summary>
    private static byte[] UncompressedDdsBytes()
    {
        var dds = new byte[128 + 4 * 4 * 4];
        foreach (var (offset, value) in new[] { (0, 0x20534444), (4, 124), (8, 0x100F), (12, 4), (16, 4),
                     (20, 16), (28, 1), (76, 32), (80, 0x41), (88, 32), (92, 0xFF0000), (96, 0xFF00), (100, 0xFF),
                     (104, unchecked((int)0xFF000000)) })
            BitConverter.GetBytes(value).CopyTo(dds, offset);
        for (int i = 128; i < dds.Length; i++) dds[i] = (byte)(i * 7);
        return dds;
    }

    /// <summary>The 1×1 DX10 BC3 file <see cref="CompressedDdsTests"/> treats as eligible.</summary>
    private static byte[] CompressedDdsBytes()
    {
        var bytes = new byte[164];
        foreach (var (offset, value) in new[] { (0,0x20534444),(4,124),(12,1),(16,1),(28,1),
            (76,32),(80,4),(84,0x30315844),(128,77),(132,3),(140,1) })
            BitConverter.GetBytes(value).CopyTo(bytes, offset);
        return bytes;
    }

    /// <summary>In-memory archives: broadcast lookup returns the first archive holding a path;
    /// scoped lookup matches archives named after one of the scope's plugins.</summary>
    private class FakeArchives(string defaultBsa) : IBsaArchiveProvider
    {
        private readonly List<(string Bsa, string Path, byte[] Bytes)> _files = new();
        private readonly string _defaultBsa = defaultBsa;
        private int _extractions;
        protected int _reads;

        public string? FailReadsFrom { get; set; }
        public int Extractions => _extractions;
        public int Reads => _reads;

        public FakeArchives With(string path, byte[] bytes) { Add(_defaultBsa, path, bytes); return this; }
        public void Add(string bsa, string path, byte[] bytes) => _files.Add((bsa, path, bytes));

        public void EnsureAllArchivesOpened() { }

        public bool TryLocateInBsa(string subpath, out string? containingBsaPath)
        {
            containingBsaPath = _files.FirstOrDefault(f => Same(f.Path, subpath)).Bsa;
            return containingBsaPath != null;
        }

        public bool TryLocateInScopedBsa(string subpath, string folderPath, IReadOnlyList<string> modKeyFileNames, out string? containingBsaPath)
        {
            containingBsaPath = _files.FirstOrDefault(f => Same(f.Path, subpath) &&
                f.Bsa.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase) &&
                modKeyFileNames.Any(k => Same(Path.GetFileNameWithoutExtension(k), Path.GetFileNameWithoutExtension(f.Bsa)))).Bsa;
            return containingBsaPath != null;
        }

        public bool TryExtractToDisk(string containingBsaPath, string subpath, string destPath, out string? error)
        {
            Interlocked.Increment(ref _extractions);
            if (!Find(containingBsaPath, subpath, out var bytes, out error)) return false;
            File.WriteAllBytes(destPath, bytes!);
            return true;
        }

        protected bool Find(string bsa, string subpath, out byte[]? bytes, out string? error)
        {
            bytes = null;
            if (Same(bsa, FailReadsFrom)) { error = "read failed (test)"; return false; }
            var file = _files.FirstOrDefault(f => Same(f.Bsa, bsa) && Same(f.Path, subpath));
            if (file.Bytes == null) { error = "not found"; return false; }
            bytes = (byte[])file.Bytes.Clone();
            error = null;
            return true;
        }

        private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    }

    private sealed class ReadingArchives(string defaultBsa) : FakeArchives(defaultBsa), IBsaArchiveByteReader
    {
        public bool TryReadBytes(string containingBsaPath, string subpath, out byte[]? bytes, out string? error)
        {
            Interlocked.Increment(ref _reads);
            return Find(containingBsaPath, subpath, out bytes, out error);
        }
    }

    private sealed class NullLogger : IRenderLogger
    {
        public void LogMessage(string message) { }
        public void LogError(string message) { }
        public void LogError(string message, Exception ex) { }
    }

    private sealed class Folder(string path) : IDataFolderProvider
    {
        public string DataFolderPath => path;
        public object? CurrentLoadOrderToken => path;
    }
}
