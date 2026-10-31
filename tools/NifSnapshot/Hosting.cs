using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace SkyRender.NifSnapshot;

/// <summary>Swallows renderer log lines: the snapshot records outcomes, not logs.</summary>
internal sealed class NullLogger : IRenderLogger
{
    public void LogMessage(string message) { }
    public void LogError(string message) { }
    public void LogError(string message, Exception ex) { }
}

internal sealed class FixedDataFolder(string path) : IDataFolderProvider
{
    public string DataFolderPath => path;
    public object? CurrentLoadOrderToken => path;
}

/// <summary>
/// Every BSA in the Data folder, in file-name order (later wins a path an earlier one also holds).
/// Serves archive files from memory, so nothing is extracted. Not load-order aware: the snapshot
/// parses every archived mesh once, so which archive would win in game does not matter.
/// </summary>
internal sealed class AllArchivesProvider : IBsaArchiveProvider, IBsaArchiveByteReader
{
    private readonly List<(string Path, Dictionary<string, IArchiveFile> Files)> _archives = new();

    public AllArchivesProvider(string dataFolder, GameRelease release = GameRelease.SkyrimSE)
    {
        foreach (var path in Directory.EnumerateFiles(dataFolder, "*.bsa").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var reader = Archive.CreateReader(release, new Noggog.FilePath(path));
                var files = new Dictionary<string, IArchiveFile>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in reader.Files) files[file.Path.Replace('/', '\\')] = file;
                _archives.Add((path, files));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Could not read archive " + path + ": " + ex.Message);
            }
        }
    }

    public IReadOnlyList<(string Path, Dictionary<string, IArchiveFile> Files)> Archives => _archives;

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
        error = "the snapshot harness never extracts";
        return false;
    }

    public bool TryReadBytes(string containingBsaPath, string subpath, out byte[]? bytes, out string? error)
    {
        bytes = null;
        var archive = _archives.FirstOrDefault(a => string.Equals(a.Path, containingBsaPath, StringComparison.OrdinalIgnoreCase));
        if (archive.Files == null || !archive.Files.TryGetValue(subpath.Replace('/', '\\'), out var file))
        {
            error = "not found: " + subpath;
            return false;
        }
        try
        {
            using var source = file.AsStream();
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            bytes = buffer.ToArray();
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
