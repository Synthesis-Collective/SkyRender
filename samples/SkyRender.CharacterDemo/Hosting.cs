using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using SkyRender.Character;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;

namespace SkyRender.CharacterDemo;

// The small interfaces a host implements so SkyRender can find game files (see USAGE.md, section 2),
// plus the character tier's settings and render-thread hooks (USAGE.md, sections 5 and 6).

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
                    DemoLog.Instance.LogError("Could not read archive " + archivePath.Path, ex);
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

/// <summary>
/// The character viewer's settings. The demo keeps them in memory: the lighting presets are the
/// built-in ones and every render toggle keeps the library default (the interface's default
/// members), so this only holds the values the interface has no default for. The viewer watches
/// <see cref="CharacterViewerVerboseLog"/> through <see cref="INotifyPropertyChanged"/>, so the
/// setters raise it.
/// </summary>
internal sealed class DemoViewerSettings : ICharacterViewerSettings
{
    private string _layout = "";
    private string _scheme = "";
    private bool _verbose;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Empty: the viewer falls back to its default layout.</summary>
    public string CharacterViewerLightingLayout { get => _layout; set => Set(ref _layout, value); }

    /// <summary>Empty: the viewer falls back to its default colour scheme.</summary>
    public string CharacterViewerLightingColorScheme { get => _scheme; set => Set(ref _scheme, value); }

    public IList<LightingLayout> UserLightingLayouts { get; } = new List<LightingLayout>();
    public IList<LightingColorScheme> UserLightingColorSchemes { get; } = new List<LightingColorScheme>();

    public bool CharacterViewerVerboseLog { get => _verbose; set => Set(ref _verbose, value); }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Runs the viewer's GL work on the WPF UI thread, which owns the GL control's context. The demo has
/// one viewer and so one context; a host with several GL controls would also make the right
/// context current here (see <c>VM_CharacterViewer.RenderThreadMarshaller</c>).
/// </summary>
internal sealed class UiThreadMarshaller : IRenderThreadMarshaller
{
    public void Invoke(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }
}
