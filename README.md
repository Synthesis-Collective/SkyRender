# SkyRender

OpenGL rendering for Skyrim NIF scenes, in two packages:

- **SkyRender** (`net8.0`): NIF parsing, Bethesda materials and shaders, DDS textures (decoded or
  authored-mip compressed uploads), CPU skinning, the GL renderer with overlays, and an offscreen
  pipeline that renders a scene to PNG or raw BGRA32 pixels. No WPF and no game-library dependency.
- **SkyRender.Character** (`net8.0-windows10.0.19041.0`, WPF): character assembly on top of the
  core: NPC mesh-path inputs, texture and mesh overrides, FaceGen heads, slot occupancy, an NPC
  offscreen scene builder for portraits, and an interactive viewer view model with WPF panels.

The libraries take no dependency on Mutagen.Bethesda or other game-record libraries. A host
implements a few small abstraction interfaces so the renderer can pull the data it needs:

- `IBsaArchiveProvider` — finds and extracts files inside BSA archives.
- `IDataFolderProvider` — supplies the game Data folder and a cache token.
- `IRenderLogger`, `IRenderSettings` — logging and cache sizing.
- Character tier: `INpcMeshDataSource` (resolves an NPC to its mesh/texture paths) and
  `ICharacterViewerSettings` (extends `IRenderSettings` with the viewer's persisted settings).

## Quick start

```csharp
var resolver = new GameAssetResolver(myDataFolder, myBsaProvider, logGate, logger);
var assets   = new AssetCache(resolver, logger, logGate);
var shapes   = assets.MeshBuilder.BuildFromFile(@"C:\path\to\model.nif");   // any thread

// On the GL thread:
var renderer = new GlRenderer();
renderer.Initialize(ModuleResourceLocator.ShaderDirectory);
var textures = new GlTextureManager(assets, logger);
textures.Initialize();
foreach (var shape in shapes)
{
    var mesh = GlMeshFactory.Create(shape);
    NifMaterialBinder.Bind(textures, resolver, mesh, shape, shape.TexturePaths, new MaterialBindOptions());
    renderer.AddMesh(mesh);
}
renderer.Render(camera, width, height);   // every frame
```

Offscreen rendering, NPC portraits and the interactive character viewer are covered in the
[usage guide](https://github.com/Synthesis-Collective/SkyRender/blob/main/docs/USAGE.md).

## Demo

[`samples/SkyRender.Demo`](https://github.com/Synthesis-Collective/SkyRender/tree/main/samples/SkyRender.Demo)
is a minimal WPF app: choose **Open NIF…**, and it renders the mesh with its textures (resolved from
the game's Data folder and archives), framed automatically. Drag to orbit, middle-drag to pan, use
the wheel to zoom.

```bash
dotnet run --project samples/SkyRender.Demo
```

[`samples/SkyRender.CellDemo`](https://github.com/Synthesis-Collective/SkyRender/tree/main/samples/SkyRender.CellDemo) draws a whole interior or exterior cell of the installed game.

## Platform

- Windows with an OpenGL 3.3 capable GPU (the offscreen renderer requests a 4.0 core context).
- The core is plain `net8.0`; the character tier needs WPF.
- GLSL shaders ship embedded in the assembly (and are also copied beside it for local builds), so
  the package works as a `PackageReference` with no extra setup.

## Documentation

- [Usage guide](https://github.com/Synthesis-Collective/SkyRender/blob/main/docs/USAGE.md)
- [Rendering pipeline](https://github.com/Synthesis-Collective/SkyRender/blob/main/docs/RENDERING_PIPELINE.md):
  how NIF data flows through parsing, materials, lighting and post-processing.
- [Character pipeline](https://github.com/Synthesis-Collective/SkyRender/blob/main/docs/CHARACTER_PIPELINE.md):
  what SkyRender.Character adds: NPC inputs, overrides, the interactive viewer.
- [Changelog](https://github.com/Synthesis-Collective/SkyRender/blob/main/CHANGELOG.md)

## Authorship

SkyRender was created by **Piranha91** together with **Claude**, Anthropic's AI model. Piranha91
directed the design, set the requirements, validated the results against the game and reviewed
every change; Claude wrote the code. No code in the initial release was written by hand.

## Acknowledgements

SkyRender builds on [nifly](https://github.com/ousnius/nifly) by **ousnius**,
[NiflySharp](https://github.com/SteveTownsend/niflyswig) by **SteveTownsend**,
[Mutagen](https://github.com/Mutagen-Modding/Mutagen) by **Noggog**, and
[Community Shaders](https://github.com/community-shaders/skyrim-community-shaders) by **doodlum** and
contributors, whose HLSL lighting code was partly ported to GLSL and made character skin tint match
the game. Details and the other libraries used are in
[NOTICE.md](https://github.com/Synthesis-Collective/SkyRender/blob/main/NOTICE.md).

## License

GPL-3.0-only.
