namespace SkyRender;

/// <summary>
/// A parsed NIF, as <see cref="NifFileLoader"/> hands it out. Opaque on purpose: hosts load a NIF
/// (typically a skeleton) and pass it back into SkyRender, so the NIF library underneath can change
/// without changing SkyRender's API. Disposable so a <c>using</c> around one keeps compiling; the
/// parsed file is managed memory and needs no release.
/// </summary>
public sealed class LoadedNif : IDisposable
{
    internal LoadedNif(NiflySharp.NifFile file) => File = file;

    /// <summary>The parsed file. Internal: SkyRender (and its character tier) read it through NifAccess.</summary>
    internal NiflySharp.NifFile File { get; }

    public void Dispose() { }
}
