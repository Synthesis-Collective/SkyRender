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

Three sample apps show the library at work:

- [`samples/SkyRender.Demo`](https://github.com/Synthesis-Collective/SkyRender/tree/main/samples/SkyRender.Demo) is a minimal WPF viewer: choose **Open NIF…**,
  and it renders the mesh with its textures (resolved from the game's Data folder and archives),
  framed automatically, under the studio lighting the character tier uses (**Game daylight**
  switches to the scene look). Drag to orbit, middle-drag to pan, use the wheel to zoom.
- [`samples/SkyRender.CellDemo`](https://github.com/Synthesis-Collective/SkyRender/tree/main/samples/SkyRender.CellDemo) draws a whole interior or exterior cell of
  the installed game: terrain, water, grass, statics, effects, weather or cell lighting, and
  optionally distant LOD.
- [`samples/SkyRender.CharacterDemo`](https://github.com/Synthesis-Collective/SkyRender/tree/main/samples/SkyRender.CharacterDemo) lists the NPCs of the installed game and
  draws the one you pick with their default outfit, in the interactive character viewer.

```bash
dotnet run --project samples/SkyRender.Demo
dotnet run --project samples/SkyRender.CellDemo -c Release
dotnet run --project samples/SkyRender.CharacterDemo -c Release
```

The comparisons below put a SkyRender frame beside the game's own screenshot from the same camera
position, drawn from the base game's data. The screenshots are of The Elder Scrolls V: Skyrim Special
Edition, a game by Bethesda Game Studios, captured for illustration. Exteriors are under the
vanilla clear weather at noon.

Riverwood from the air: terrain with its painted layers, the river, buildings and trees of the
cells in view.

![SkyRender and the game, Riverwood from above](https://raw.githubusercontent.com/Synthesis-Collective/SkyRender/main/docs/images/riverwood_aerial.jpg)

The view north from above Riverwood, with distant terrain, object and tree LOD beyond the loaded
cells.

![SkyRender and the game, the view north with distant LOD](https://raw.githubusercontent.com/Synthesis-Collective/SkyRender/main/docs/images/view_north_lod.jpg)

A meadow near Riverwood at eye level, with grass placed by the engine's rules (the exact tufts
differ: the game's placement is not repeatable).

![SkyRender and the game, grass in a meadow](https://raw.githubusercontent.com/Synthesis-Collective/SkyRender/main/docs/images/meadow_grass.jpg)

The Bannered Mare's kitchen hearth: the cell's own lighting, its placed lights with
shadows, and the fire's effect-shader flames turned toward the camera.

![SkyRender and the game, the Bannered Mare kitchen hearth](https://raw.githubusercontent.com/Synthesis-Collective/SkyRender/main/docs/images/bannered_mare_kitchen.jpg)

Known differences: grass is still brighter than the game's; particles (sparks, smoke, the hearth's
moving flames) and NPCs are not drawn; the game's distance haze is thicker; no bloom or eye
adaptation; and clouds are not drawn. RENDERING_PIPELINE.md records each comparison.

## Platform

- Windows with an OpenGL 3.3 capable GPU (the offscreen renderer requests a 4.0 core context).
- The core is plain `net8.0`; the character tier needs WPF and a Windows platform version of at
  least 10.0.19041 in the host's target framework (for example `net8.0-windows10.0.19041.0`; a
  plain `-windows` target gets NU1202); USAGE.md's Troubleshooting section explains this.
- NIF parsing has no native dependency (the managed Nifly package, since 4.0).
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

SkyRender builds on [NiflySharp](https://github.com/ousnius/NiflySharp) and
[nifly](https://github.com/ousnius/nifly) by **ousnius**, the
[niflysharp bindings](https://github.com/SteveTownsend/niflyswig) by **SteveTownsend** (used up to
3.x),
[Mutagen](https://github.com/Mutagen-Modding/Mutagen) by **Noggog**, and
[Community Shaders](https://github.com/community-shaders/skyrim-community-shaders) by **doodlum** and
contributors, whose HLSL lighting code was partly ported to GLSL and made character skin tint match
the game. Details and the other libraries used are in
[NOTICE.md](https://github.com/Synthesis-Collective/SkyRender/blob/main/NOTICE.md).

## License

GPL-3.0-only.
