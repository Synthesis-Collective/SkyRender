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

namespace SkyRender.CharacterDemo;

/// <summary>One NPC as the list shows it: the winning record's EditorID and name, and the plugin
/// that wins it (the last one in the load order with a copy of the record).</summary>
internal sealed record NpcEntry(FormKey Key, string? EditorId, string? Name, string WinningPlugin)
{
    /// <summary>The list's first line.</summary>
    public string Title => (EditorId ?? Key.ToString()) +
        (string.IsNullOrWhiteSpace(Name) || Name == EditorId ? "" : "  '" + Name + "'");

    /// <summary>The list's second line: the FormKey and the winning plugin.</summary>
    public string Detail => Key + "  [" + WinningPlugin + "]";

    /// <summary>Whether the search box text matches: EditorID, name, FormKey or plugin.</summary>
    public bool Matches(string filter)
    {
        if (filter.Length == 0) return true;
        return EditorId?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true
            || Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true
            || Key.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)
            || WinningPlugin.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The game's data through Mutagen: the load order (each plugin opened read-only as a binary
/// overlay), a link cache for looking records up by FormKey (it returns the winning override),
/// and the list of every NPC_ record's winner.
/// </summary>
internal sealed class GameData : IDisposable
{
    private readonly IGameEnvironment<ISkyrimMod, ISkyrimModGetter> _env;

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

    /// <summary>Every NPC_ record of the load order (its winning copy), sorted by EditorID.</summary>
    public List<NpcEntry> Npcs { get; } = new();

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
            log.LogMessage($"Indexed {data.Npcs.Count} NPC record(s), {clock.ElapsedMilliseconds} ms.");
            return data;
        }
        catch
        {
            env.Dispose();
            throw;
        }
    }

    /// <summary>Lists each NPC once, from its winning copy: the plugins are walked last to first, so
    /// the first copy of a FormKey met is the winner.</summary>
    private void BuildIndex(CancellationToken ct)
    {
        var seen = new HashSet<FormKey>();
        for (int m = Mods.Count - 1; m >= 0; m--)
        {
            ct.ThrowIfCancellationRequested();
            var mod = Mods[m];
            foreach (var npc in mod.Npcs.Records)
            {
                if (!seen.Add(npc.FormKey)) continue;
                Npcs.Add(new NpcEntry(npc.FormKey, npc.EditorID, SafeName(npc.Name), mod.ModKey.FileName));
            }
        }
        Npcs.Sort((a, b) => string.Compare(a.EditorId ?? a.Key.ToString(), b.EditorId ?? b.Key.ToString(),
            StringComparison.OrdinalIgnoreCase));
    }

    internal static string? SafeName(Mutagen.Bethesda.Strings.ITranslatedStringGetter? name)
    {
        try { return name?.String; }
        catch { return null; }   // a missing strings file leaves the name out
    }

    /// <summary>The winning record of a FormKey, as <typeparamref name="T"/>, or null.</summary>
    public T? Resolve<T>(FormKey key) where T : class, Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter
        => !key.IsNull && Links.TryResolve<T>(key, out var rec) ? rec : null;

    public void Dispose() => _env.Dispose();
}
