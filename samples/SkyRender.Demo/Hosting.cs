using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;

namespace SkyRender.Demo;

// The three small interfaces a host implements so SkyRender can find game files and log.

/// <summary>Writes renderer log lines to the debugger output.</summary>
internal sealed class DebugLogger : IRenderLogger
{
    public void LogMessage(string message) => Debug.WriteLine(message);
    public void LogError(string message) => Debug.WriteLine("ERROR " + message);
    public void LogError(string message, Exception ex) => Debug.WriteLine("ERROR " + message + ": " + ex);
}

/// <summary>A fixed game Data folder (loose files are resolved against it).</summary>
internal sealed class FixedDataFolder(string path) : IDataFolderProvider
{
    public string DataFolderPath => path;
    public object? CurrentLoadOrderToken => path;
}

/// <summary>
/// Indexes the BSA archives that belong to the plugins in a Data folder (the same archive-to-plugin
/// rule the game uses), so textures packed in archives resolve. Later plugins win, alphabetically
/// within masters and then plugins, which is close enough for a viewer.
/// </summary>
internal sealed class FolderBsaProvider : IBsaArchiveProvider
{
    private readonly List<(string Path, Dictionary<string, IArchiveFile> Files)> _archives = new();

    public FolderBsaProvider(string dataFolder, GameRelease release = GameRelease.SkyrimSE)
    {
        if (!Directory.Exists(dataFolder)) return;
        var plugins = Directory.EnumerateFiles(dataFolder)
            .Where(f => f.EndsWith(".esm", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in plugins)
        {
            ModKey modKey;
            try { modKey = ModKey.FromNameAndExtension(Path.GetFileName(plugin)); }
            catch { continue; }

            foreach (var archivePath in Archive.GetApplicableArchivePaths(release, dataFolder, modKey))
            {
                if (!seen.Add(archivePath.Path)) continue;
                try
                {
                    var reader = Archive.CreateReader(release, archivePath);
                    var files = new Dictionary<string, IArchiveFile>(StringComparer.OrdinalIgnoreCase);
                    foreach (var file in reader.Files) files[file.Path.Replace('/', '\\')] = file;
                    _archives.Add((archivePath.Path, files));
                }
                catch
                {
                    // An unreadable archive just means its files are not found.
                }
            }
        }
    }

    public int ArchiveCount => _archives.Count;

    public void EnsureAllArchivesOpened() { }

    public bool TryLocateInBsa(string subpath, out string? containingBsaPath)
    {
        var key = subpath.Replace('/', '\\');
        for (int i = _archives.Count - 1; i >= 0; i--)
        {
            if (_archives[i].Files.ContainsKey(key))
            {
                containingBsaPath = _archives[i].Path;
                return true;
            }
        }
        containingBsaPath = null;
        return false;
    }

    public bool TryLocateInScopedBsa(string subpath, string folderPath, IReadOnlyList<string> modKeyFileNames,
        out string? containingBsaPath)
    {
        containingBsaPath = null;
        return false;
    }

    public bool TryExtractToDisk(string containingBsaPath, string subpath, string destPath, out string? error)
    {
        var archive = _archives.FirstOrDefault(a => string.Equals(a.Path, containingBsaPath, StringComparison.OrdinalIgnoreCase));
        if (archive.Files == null || !archive.Files.TryGetValue(subpath.Replace('/', '\\'), out var file))
        {
            error = "not found: " + subpath;
            return false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            using var source = file.AsStream();
            using var dest = File.Create(destPath);
            source.CopyTo(dest);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
