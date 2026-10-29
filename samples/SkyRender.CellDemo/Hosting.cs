using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;

namespace SkyRender.CellDemo;

// The small interfaces a host implements so SkyRender can find game files (see USAGE.md, section 2).

/// <summary>A fixed game Data folder (loose files are resolved against it).</summary>
internal sealed class FixedDataFolder(string path) : IDataFolderProvider
{
    public string DataFolderPath => path;
    public object? CurrentLoadOrderToken => path;
}

/// <summary>
/// The BSA archives of the plugins in the load order, in load order: a plugin's archives are the
/// ones named after it (Mutagen's <c>Archive.GetApplicableArchivePaths</c>, the game's rule), and a
/// later plugin's archive wins a path an earlier one also holds. Loose files still win over every
/// archive (GameAssetResolver checks the Data folder first).
/// </summary>
internal sealed class LoadOrderBsaProvider : IBsaArchiveProvider
{
    private readonly List<(string Path, Dictionary<string, IArchiveFile> Files)> _archives = new();

    public LoadOrderBsaProvider(string dataFolder, IEnumerable<ModKey> loadOrder, GameRelease release = GameRelease.SkyrimSE)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var modKey in loadOrder)
        {
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
                catch (Exception ex)
                {
                    // An unreadable archive just means its files are not found.
                    CellLog.Instance.LogError("Could not read archive " + archivePath.Path, ex);
                }
            }
        }
    }

    public IReadOnlyList<string> ArchivePaths => _archives.Select(a => a.Path).ToList();

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
            // Several loader threads can ask for the same file: write to a private name, then move.
            string temp = destPath + "." + Guid.NewGuid().ToString("N") + ".part";
            using (var source = file.AsStream())
            using (var dest = File.Create(temp))
                source.CopyTo(dest);
            try { File.Move(temp, destPath, overwrite: true); }
            catch (IOException) when (File.Exists(destPath)) { File.Delete(temp); }
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
