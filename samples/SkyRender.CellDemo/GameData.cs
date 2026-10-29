using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;

namespace SkyRender.CellDemo;

/// <summary>One cell as the picker lists it: its winning EditorID and name, the plugin that wins it,
/// and for an exterior its worldspace and grid square.</summary>
internal sealed record CellEntry(FormKey Key, string? EditorId, string? Name, string WinningPlugin,
    FormKey? World, (int X, int Y)? Grid)
{
    public bool IsInterior => World is null;

    /// <summary>What the list shows.</summary>
    public string Display
    {
        get
        {
            string id = EditorId ?? Key.ToString();
            string name = string.IsNullOrWhiteSpace(Name) || Name == EditorId ? "" : "  '" + Name + "'";
            string grid = Grid is { } g ? $"  ({g.X}, {g.Y})" : "";
            return id + name + grid;
        }
    }

    /// <summary>Whether the search box text matches: EditorID, name, FormKey or "x,y".</summary>
    public bool Matches(string filter)
    {
        if (filter.Length == 0) return true;
        if (EditorId?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true) return true;
        if (Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true) return true;
        if (Key.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;
        if (Grid is { } g)
        {
            var parts = filter.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y))
                return g.X == x && g.Y == y;
        }
        return false;
    }
}

/// <summary>One worldspace and its exterior cells.</summary>
internal sealed class WorldEntry
{
    public required FormKey Key { get; init; }
    public string? EditorId { get; set; }
    public string? Name { get; set; }
    public List<CellEntry> Cells { get; } = new();
    public Dictionary<(int X, int Y), CellEntry> ByGrid { get; } = new();
    public string Display => (EditorId ?? Key.ToString()) + (string.IsNullOrWhiteSpace(Name) ? "" : "  '" + Name + "'")
                             + $"  [{Cells.Count} cells]";
}

/// <summary>
/// The game's data through Mutagen: the load order (each plugin opened read-only as a binary
/// overlay), a link cache for looking records up by FormKey, and an index of every cell's copies.
/// A cell's "copies" are the versions of its record in each plugin that has one, in load order:
/// the last one wins the cell's own fields, and every copy contributes the references filed in it.
/// <para>Mutagen's overlays are read by one thread at a time here: every call after construction
/// comes from the scene loader, which runs one load at a time.</para>
/// </summary>
internal sealed class GameData : IDisposable
{
    private readonly IGameEnvironment<ISkyrimMod, ISkyrimModGetter> _env;
    private readonly Dictionary<FormKey, List<(int Mod, ICellGetter Cell)>> _cellCopies = new();
    private readonly Dictionary<FormKey, List<(int Mod, IWorldspaceGetter World)>> _worldCopies = new();

    private GameData(IGameEnvironment<ISkyrimMod, ISkyrimModGetter> env, List<ISkyrimModGetter> mods)
    {
        _env = env;
        Mods = mods;
        DataFolder = env.DataFolderPath.Path;
    }

    public string DataFolder { get; }

    /// <summary>The plugins that loaded, in load order.</summary>
    public IReadOnlyList<ISkyrimModGetter> Mods { get; }

    public ILinkCache<ISkyrimMod, ISkyrimModGetter> Links => _env.LinkCache;

    public List<CellEntry> Interiors { get; } = new();
    public List<WorldEntry> Worlds { get; } = new();

    /// <summary>
    /// Opens the game. <paramref name="dataFolder"/> null detects the installed Skyrim Special
    /// Edition (Mutagen's game locations); <paramref name="loadOrderFile"/> null uses the game's own
    /// load order (plugins.txt), otherwise the plugins a loadorder.txt lists, in its order. Plugins
    /// that are listed but not in the Data folder are left out.
    /// </summary>
    public static GameData Open(string? dataFolder, string? loadOrderFile, IRenderLogger log, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var builder = GameEnvironment.Typical.Builder<ISkyrimMod, ISkyrimModGetter>(GameRelease.SkyrimSE);
        if (!string.IsNullOrWhiteSpace(dataFolder)) builder = builder.WithTargetDataFolder(dataFolder);
        if (!string.IsNullOrWhiteSpace(loadOrderFile))
        {
            var keys = File.ReadLines(loadOrderFile)
                .Select(l => l.Trim().TrimStart('*'))
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .Select(l => ModKey.TryFromFileName(l, out var k) ? k : (ModKey?)null)
                .Where(k => k is not null).Select(k => k!.Value).Distinct().ToArray();
            builder = builder.WithLoadOrder(keys);
        }
        var env = builder.Build();
        try
        {
            var mods = new List<ISkyrimModGetter>();
            var missing = new List<string>();
            foreach (var listing in env.LoadOrder.ListedOrder)
            {
                if (listing.Mod is { } mod) mods.Add(mod);
                else missing.Add(listing.ModKey.FileName);
            }
            if (mods.Count == 0)
                throw new InvalidOperationException("No plugins were found in " + env.DataFolderPath.Path + ".");
            log.LogMessage($"Load order: {mods.Count} plugin(s) from {env.DataFolderPath.Path}"
                + (missing.Count > 0 ? $"; {missing.Count} listed but not present: {string.Join(", ", missing.Take(8))}" : ""));
            var data = new GameData(env, mods);
            data.BuildIndex(ct);
            log.LogMessage($"Indexed {data.Interiors.Count} interior and {data.Worlds.Sum(w => w.Cells.Count)} exterior cell(s) " +
                $"in {data.Worlds.Count} worldspace(s), {clock.ElapsedMilliseconds} ms.");
            return data;
        }
        catch
        {
            env.Dispose();
            throw;
        }
    }

    private void BuildIndex(CancellationToken ct)
    {
        for (int m = 0; m < Mods.Count; m++)
        {
            ct.ThrowIfCancellationRequested();
            var mod = Mods[m];
            foreach (var block in mod.Cells.Records)
                foreach (var sub in block.SubBlocks)
                    foreach (var cell in sub.Cells)
                        Copies(_cellCopies, cell.FormKey).Add((m, cell));
            foreach (var world in mod.Worldspaces.Records)
            {
                Copies(_worldCopies, world.FormKey).Add((m, world));
                foreach (var block in world.SubCells)
                    foreach (var sub in block.Items)
                        foreach (var cell in sub.Items)
                            Copies(_cellCopies, cell.FormKey).Add((m, cell));
            }
        }

        var worlds = new Dictionary<FormKey, WorldEntry>();
        foreach (var (key, copies) in _worldCopies)
        {
            worlds[key] = new WorldEntry
            {
                Key = key,
                EditorId = Last(copies, c => c.World.EditorID),
                Name = Last(copies, c => SafeName(c.World.Name)),
            };
        }
        // An exterior cell belongs to the worldspace whose group files it (in its winning copy).
        var cellWorld = new Dictionary<FormKey, FormKey>();
        foreach (var (wkey, copies) in _worldCopies)
            foreach (var (_, world) in copies)
                foreach (var block in world.SubCells)
                    foreach (var sub in block.Items)
                        foreach (var cell in sub.Items)
                            cellWorld[cell.FormKey] = wkey;

        foreach (var (key, copies) in _cellCopies)
        {
            var winner = copies[^1];
            var entry = new CellEntry(key, Last(copies, c => c.Cell.EditorID), Last(copies, c => SafeName(c.Cell.Name)),
                Mods[winner.Mod].ModKey.FileName,
                cellWorld.TryGetValue(key, out var wk) ? wk : null,
                winner.Cell.Grid?.Point is { } p && cellWorld.ContainsKey(key) ? (p.X, p.Y) : null);
            if (entry.World is { } w && worlds.TryGetValue(w, out var we))
            {
                we.Cells.Add(entry);
                if (entry.Grid is { } g) we.ByGrid[g] = entry;
            }
            else if (entry.World is null) Interiors.Add(entry);
        }
        Interiors.Sort((a, b) => string.Compare(a.EditorId ?? a.Key.ToString(), b.EditorId ?? b.Key.ToString(), StringComparison.OrdinalIgnoreCase));
        foreach (var w in worlds.Values)
        {
            var sorted = w.Cells.OrderBy(c => c.EditorId is null).ThenBy(c => c.EditorId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Grid?.Y).ThenBy(c => c.Grid?.X).ToList();
            w.Cells.Clear();
            w.Cells.AddRange(sorted);
            if (w.Cells.Count > 0) Worlds.Add(w);
        }
        Worlds.Sort((a, b) => b.Cells.Count.CompareTo(a.Cells.Count));
    }

    private static List<T> Copies<T>(Dictionary<FormKey, List<T>> map, FormKey key)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<T>(1);
        return list;
    }

    /// <summary>The last non-empty value over a record's copies (an override may leave a field out).</summary>
    private static string? Last<T>(List<T> copies, Func<T, string?> get)
    {
        for (int i = copies.Count - 1; i >= 0; i--)
            if (get(copies[i]) is { Length: > 0 } v) return v;
        return null;
    }

    private static string? SafeName(Mutagen.Bethesda.Strings.ITranslatedStringGetter? name)
    {
        try { return name?.String; }
        catch { return null; }   // a missing strings file leaves the name out
    }

    /// <summary>A cell's copies, in load order (empty when the cell is unknown).</summary>
    public IReadOnlyList<(int Mod, ICellGetter Cell)> CellCopies(FormKey cell)
        => _cellCopies.TryGetValue(cell, out var l) ? l : Array.Empty<(int, ICellGetter)>();

    /// <summary>A worldspace's copies, in load order: their TopCell holds the persistent references.</summary>
    public IReadOnlyList<(int Mod, IWorldspaceGetter World)> WorldCopies(FormKey world)
        => _worldCopies.TryGetValue(world, out var l) ? l : Array.Empty<(int, IWorldspaceGetter)>();

    public string PluginName(int mod) => Mods[mod].ModKey.FileName;

    /// <summary>The winning record of a FormKey, as <typeparamref name="T"/>, or null.</summary>
    public T? Resolve<T>(FormKey key) where T : class, Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter
        => !key.IsNull && Links.TryResolve<T>(key, out var rec) ? rec : null;

    public void Dispose() => _env.Dispose();
}
