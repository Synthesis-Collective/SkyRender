using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using SkyRender;
using SkyRender.NifSnapshot;

// NifSnapshot: canonical per-mesh hashes of everything NifMeshBuilder builds from a corpus.
//
//   list-mods --mods DIR
//       Mod folders under DIR with their mesh counts (pick the NSFW exclusions from this).
//   capture --data DIR [--mods DIR] [--exclude FILE] [--filter TEXT] [--limit N] [--threads N] --out FILE.jsonl
//       Every .nif/.btr/.bto in the Data folder's BSAs, and every one in each mod folder not named in
//       --exclude (one folder name per line). One JSON line per file, sorted by id.
//   dump --data DIR [--mods DIR] --ids FILE --out DIR
//       Full canonical text of each property of each mesh, for the ids listed in FILE.
//   diff --a FILE.jsonl --b FILE.jsonl [--out FILE]
//       Files whose outcome or meshes differ, and which properties differ.

var opts = Options.Parse(args.Skip(1));
switch (args.FirstOrDefault())
{
    case "list-mods": return ListMods(opts);
    case "capture": return Capture(opts, dumpIds: null);
    case "dump": return Capture(opts, dumpIds: File.ReadAllLines(opts.Require("ids")).Where(l => l.Length > 0).ToHashSet());
    case "diff": return Diff(opts);
    case "inspect": return Inspect(opts);
    default:
        Console.Error.WriteLine("usage: NifSnapshot list-mods|capture|dump|diff ... (see Program.cs)");
        return 2;
}

static bool IsMesh(string path) =>
    path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)
    || path.EndsWith(".btr", StringComparison.OrdinalIgnoreCase)
    || path.EndsWith(".bto", StringComparison.OrdinalIgnoreCase);

static int ListMods(Options o)
{
    foreach (var dir in Directory.EnumerateDirectories(o.Require("mods")).Order(StringComparer.OrdinalIgnoreCase))
    {
        int n = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count(IsMesh);
        Console.WriteLine($"{n,7}  {Path.GetFileName(dir)}");
    }
    return 0;
}

static int Capture(Options o, HashSet<string>? dumpIds)
{
    string data = o.Require("data");
    string? mods = o.Get("mods");
    var excluded = o.Get("exclude") is { } ex
        ? File.ReadAllLines(ex).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet(StringComparer.OrdinalIgnoreCase)
        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    string? filter = o.Get("filter");
    int limit = int.Parse(o.Get("limit") ?? "0");
    int threads = int.Parse(o.Get("threads") ?? Math.Max(1, Environment.ProcessorCount / 2).ToString());
    try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

    var logger = new NullLogger();
    var gate = new RenderLogGate();
    var archives = new AllArchivesProvider(Path.GetFullPath(data));
    var resolver = new GameAssetResolver(new FixedDataFolder(Path.GetFullPath(data)), archives, gate, logger,
        Path.Combine(Path.GetTempPath(), "NifSnapshotCache"));

    // The corpus: (id, game-relative mesh path, loose file path or null for an archived one).
    var items = new List<(string Id, string GamePath, string? LoosePath)>();
    foreach (var (archivePath, files) in archives.Archives)
        foreach (var key in files.Keys.Where(IsMesh))
            items.Add(("bsa:" + Path.GetFileName(archivePath) + "|" + key.ToLowerInvariant(), key, null));
    if (mods != null)
    {
        foreach (var dir in Directory.EnumerateDirectories(mods))
        {
            string mod = Path.GetFileName(dir);
            if (excluded.Contains(mod)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(IsMesh))
            {
                string rel = Path.GetRelativePath(dir, file);
                items.Add(("mod:" + mod + "|" + rel.ToLowerInvariant(), rel, file));
            }
        }
    }
    if (filter != null) items = items.Where(i => i.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    if (dumpIds != null) items = items.Where(i => dumpIds.Contains(i.Id)).ToList();
    if (o.Get("only") is { } onlyFile)
    {
        var only = File.ReadAllLines(onlyFile).Where(l => l.Length > 0).ToHashSet();
        items = items.Where(i => only.Contains(i.Id)).ToList();
    }
    items.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
    if (limit > 0) items = items.Take(limit).ToList();
    Console.Error.WriteLine($"{items.Count} mesh files, {archives.Archives.Count} archives, {threads} threads, SkyRender {SkyRenderInfo.Version}");

    var results = new ConcurrentDictionary<string, string>();
    var dumpDir = dumpIds != null ? Directory.CreateDirectory(o.Require("out")).FullName : null;
    var clock = Stopwatch.StartNew();
    int done = 0;
    Parallel.ForEach(items, new ParallelOptions { MaxDegreeOfParallelism = threads },
        () => new Worker(resolver, logger, gate),
        (item, _, worker) =>
        {
            results[item.Id] = worker.Run(item.Id, item.GamePath, item.LoosePath, dumpDir);
            int n = Interlocked.Increment(ref done);
            if (n % 2000 == 0) Console.Error.WriteLine($"  {n}/{items.Count} in {clock.Elapsed:hh\\:mm\\:ss}");
            return worker;
        },
        worker => worker.Dispose());

    if (dumpIds == null)
    {
        using var w = new StreamWriter(o.Require("out"));
        w.WriteLine(JsonSerializer.Serialize(new
        {
            header = true,
            skyRender = SkyRenderInfo.Version,
            nifLibrary = Worker.NifLibrary,
            properties = Worker.PropertyNames,
            files = items.Count,
            seconds = Math.Round(clock.Elapsed.TotalSeconds, 1),
        }));
        foreach (var id in results.Keys.Order(StringComparer.Ordinal)) w.WriteLine(results[id]);
    }
    Console.Error.WriteLine($"done in {clock.Elapsed:hh\\:mm\\:ss}");
    return 0;
}

// inspect --data DIR --path GAMEPATH: what Nifly holds for one archived mesh's shapes and skin.
static int Inspect(Options o)
{
    string data = Path.GetFullPath(o.Require("data"));
    var archives = new AllArchivesProvider(data);
    var resolver = new GameAssetResolver(new FixedDataFolder(data), archives, new RenderLogGate(), new NullLogger(),
        Path.Combine(Path.GetTempPath(), "NifSnapshotCache"));
    string path = resolver.ResolveAssetSource(o.Require("path")).ResolvedDiskPath!;
    var nif = NifAccess.Load(resolver.ReadResolvedBytes(path))!.File;
    var v = nif.Header.Version;
    Console.WriteLine($"version {v.FileVersion} user {v.UserVersion} stream {v.StreamVersion} IsSSE {v.IsSSE()}");
    foreach (var shape in NifAccess.Shapes(nif))
    {
        Console.WriteLine($"{shape.GetType().Name} '{NifAccess.Name(shape)}' VertexCount {shape.VertexCount} skinned {shape.IsSkinned}");
        if (shape is NiflySharp.Blocks.BSTriShape bs)
            Console.WriteLine($"  VertexDataSSE {bs.VertexDataSSE?.Count} VertexData {bs.VertexData?.Count} DataSize {bs.DataSize}");
        var skin = NifAccess.SkinInstance(nif, shape);
        Console.WriteLine($"  skin {skin?.GetType().Name} partitionRef {skin?.SkinPartition?.Index}");
        if (skin != null && NifAccess.Block<NiflySharp.Blocks.NiSkinPartition>(nif, skin.SkinPartition) is { } part)
            Console.WriteLine($"  partition VertexData {part.VertexData?.Count} DataSize {part.DataSize} partitions {part.Partitions?.Count}");
    }
    return 0;
}

static int Diff(Options o)
{
    static (JsonElement Header, Dictionary<string, JsonElement> Rows) Load(string path)
    {
        var lines = File.ReadLines(path).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        return (lines[0], lines.Skip(1).ToDictionary(e => e.GetProperty("id").GetString()!));
    }
    var (ha, a) = Load(o.Require("a"));
    var (hb, b) = Load(o.Require("b"));
    var propsA = ha.GetProperty("properties").EnumerateArray().Select(p => p.GetString()!).ToArray();
    var propsB = hb.GetProperty("properties").EnumerateArray().Select(p => p.GetString()!).ToArray();
    using var w = o.Get("out") is { } outPath ? new StreamWriter(outPath) : new StreamWriter(Console.OpenStandardOutput());
    w.WriteLine($"A: SkyRender {ha.GetProperty("skyRender")} on {ha.GetProperty("nifLibrary")}, {a.Count} files");
    w.WriteLine($"B: SkyRender {hb.GetProperty("skyRender")} on {hb.GetProperty("nifLibrary")}, {b.Count} files");

    var byProperty = new Dictionary<string, int>();
    int onlyA = 0, onlyB = 0, statusChanged = 0, meshesChanged = 0, same = 0;
    foreach (var id in a.Keys.Union(b.Keys).Order(StringComparer.Ordinal))
    {
        if (!b.TryGetValue(id, out var rb)) { onlyA++; w.WriteLine("ONLY-A " + id); continue; }
        if (!a.TryGetValue(id, out var ra)) { onlyB++; w.WriteLine("ONLY-B " + id); continue; }
        string sa = ra.GetProperty("status").GetString()!, sb = rb.GetProperty("status").GetString()!;
        if (sa != sb) { statusChanged++; w.WriteLine($"STATUS {id}: {sa} -> {sb} {Error(rb) ?? Error(ra)}"); continue; }
        var ma = ra.GetProperty("meshes").EnumerateArray().ToList();
        var mb = rb.GetProperty("meshes").EnumerateArray().ToList();
        if (ma.Count != mb.Count) { meshesChanged++; w.WriteLine($"COUNT {id}: {ma.Count} -> {mb.Count} meshes"); continue; }
        var changed = new SortedSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < ma.Count; i++)
        {
            if (ma[i].GetProperty("hash").GetString() == mb[i].GetProperty("hash").GetString()) continue;
            var pa = PropHashes(ma[i], propsA);
            var pb = PropHashes(mb[i], propsB);
            foreach (var name in pa.Keys.Union(pb.Keys))
                if (!pa.TryGetValue(name, out var x) || !pb.TryGetValue(name, out var y) || x != y) changed.Add(name);
        }
        if (changed.Count == 0) { same++; continue; }
        meshesChanged++;
        foreach (var name in changed) byProperty[name] = byProperty.GetValueOrDefault(name) + 1;
        w.WriteLine($"MESH {id}: {string.Join(", ", changed)}");
    }
    w.WriteLine();
    w.WriteLine($"same {same}, mesh differences {meshesChanged}, status changes {statusChanged}, only in A {onlyA}, only in B {onlyB}");
    foreach (var (name, n) in byProperty.OrderByDescending(kv => kv.Value)) w.WriteLine($"  {name}: {n} files");
    return meshesChanged + statusChanged + onlyA + onlyB == 0 ? 0 : 1;

    static string? Error(JsonElement r) => r.TryGetProperty("error", out var e) ? e.GetString() : null;
    static Dictionary<string, string> PropHashes(JsonElement mesh, string[] names)
    {
        var hashes = mesh.GetProperty("props").GetString()!.Split(',');
        return names.Zip(hashes).ToDictionary(p => p.First, p => p.Second);
    }
}

/// <summary>One thread's builder and skeleton cache (parsed NIFs are not shared across threads).</summary>
internal sealed class Worker : IDisposable
{
    private const string HumanSkeleton = @"meshes\actors\character\character assets\skeleton.nif";

    public static readonly string[] PropertyNames =
        Canonical.Members(typeof(NifMeshBuilder.BuiltMesh)).Select(m => m.Name).ToArray();
    private static readonly Func<object, object?>[] Getters =
        Canonical.Members(typeof(NifMeshBuilder.BuiltMesh)).Select(m => m.Get).ToArray();

    /// <summary>The NIF library SkyRender was built against, from its referenced assemblies.</summary>
    public static string NifLibrary { get; } = string.Join(", ",
        typeof(NifMeshBuilder).Assembly.GetReferencedAssemblies()
            .Where(a => a.Name!.Contains("nifly", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Name + " " + a.Version));

    private readonly GameAssetResolver _resolver;
    private readonly NifMeshBuilder _builder;
    // Skeletons by game path, typed object so this file compiles against both APIs: TryLoad returns
    // nifly.NifFile on 3.x and the SkyRender-owned wrapper on 4.x, and Cached<T> infers whichever.
    private readonly Dictionary<string, object?> _skeletons = new(StringComparer.OrdinalIgnoreCase);

    public Worker(GameAssetResolver resolver, IRenderLogger logger, RenderLogGate gate)
    {
        _resolver = resolver;
        _builder = new NifMeshBuilder(logger, gate, resolver);
    }

    public string Run(string id, string gamePath, string? loosePath, string? dumpDir)
    {
        var clock = Stopwatch.StartNew();
        string path = loosePath ?? _resolver.ResolveAssetSource(gamePath).ResolvedDiskPath ?? gamePath;
        string? skeletonGamePath = SkeletonFor(gamePath);
        try
        {
            var meshes = skeletonGamePath != null
                ? _builder.BuildFromFile(path, skeletonPath: skeletonGamePath,
                    skeletonProvider: Cached(() =>
                    {
                        var source = _resolver.ResolveAssetSource(skeletonGamePath);
                        return source.ResolvedDiskPath != null && _resolver.AssetFileExists(source.ResolvedDiskPath)
                            ? NifFileLoader.TryLoad(source.ResolvedDiskPath, _resolver)
                            : null;
                    }, skeletonGamePath))
                : _builder.BuildFromFile(path);
            _builder.ClearCache();
            var rows = new List<object>();
            for (int m = 0; m < meshes.Count; m++)
            {
                var props = Getters.Select(g =>
                {
                    object? v;
                    try { v = g(meshes[m]); } catch (Exception ex) { v = "<threw " + ex.GetType().Name + ">"; }
                    return v;
                }).ToArray();
                var hashes = props.Select(v => Canonical.Hash(v)[..12]).ToArray();
                if (dumpDir != null)
                {
                    string file = Path.Combine(dumpDir, Sanitize(id) + $".{m}.txt");
                    File.WriteAllLines(file, PropertyNames.Zip(props, (n, v) => n + " = " + Canonical.Text(v)));
                }
                rows.Add(new { name = meshes[m].ShapeName, hash = Canonical.Hash(string.Join(",", hashes)), props = string.Join(",", hashes) });
            }
            return JsonSerializer.Serialize(new { id, status = meshes.Count == 0 ? "empty" : "ok", ms = clock.ElapsedMilliseconds, meshes = rows });
        }
        catch (Exception ex)
        {
            _builder.ClearCache();
            return JsonSerializer.Serialize(new { id, status = "threw", error = ex.GetType().Name + ": " + ex.Message, ms = clock.ElapsedMilliseconds, meshes = Array.Empty<object>() });
        }
    }

    /// <summary>Humanoid meshes (bodies, armour, hair, FaceGen) use the vanilla human skeleton;
    /// a creature's meshes use the skeleton in its own folder when there is one.</summary>
    private static string? SkeletonFor(string gamePath)
    {
        string p = gamePath.Replace('/', '\\').ToLowerInvariant();
        int i = p.IndexOf(@"meshes\actors\", StringComparison.Ordinal);
        if (i < 0) return null;
        string rest = p[(i + @"meshes\actors\".Length)..];
        int slash = rest.IndexOf('\\');
        if (slash < 0) return null;
        string folder = rest[..slash];
        if (folder == "character") return HumanSkeleton;
        return @"meshes\actors\" + folder + @"\character assets\skeleton.nif";
    }

    private Func<T?> Cached<T>(Func<T?> load, string key) where T : class => () =>
    {
        if (!_skeletons.TryGetValue(key, out var cached))
        {
            cached = load();
            _skeletons[key] = cached;
        }
        return (T?)cached;
    };

    private static string Sanitize(string id) =>
        string.Concat(id.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '|' ? '_' : c));

    public void Dispose()
    {
        foreach (var s in _skeletons.Values) (s as IDisposable)?.Dispose();
        _skeletons.Clear();
    }
}

internal sealed class Options
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static Options Parse(IEnumerable<string> args)
    {
        var o = new Options();
        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            if (!list[i].StartsWith("--")) throw new ArgumentException("unexpected argument " + list[i]);
            o._values[list[i][2..]] = i + 1 < list.Count ? list[++i] : throw new ArgumentException("missing value after " + list[i]);
        }
        return o;
    }

    public string? Get(string name) => _values.TryGetValue(name, out var v) ? v : null;
    public string Require(string name) => Get(name) ?? throw new ArgumentException("--" + name + " is required");
}
