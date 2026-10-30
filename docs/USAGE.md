# Using SkyRender

This guide walks through the common tasks, from "show one NIF" to NPC portraits. The runnable
version of sections 2-3 is the sample app in [`samples/SkyRender.Demo`](../samples/SkyRender.Demo);
[`samples/SkyRender.CellDemo`](../samples/SkyRender.CellDemo) puts sections 7-8 together into a
whole cell read from the game's plugins, and
[`samples/SkyRender.CharacterDemo`](../samples/SkyRender.CharacterDemo) does sections 5-6 for any
NPC of the game, with a Mutagen-backed `INpcMeshDataSource`.

- [1. Install](#1-install)
- [2. Connect SkyRender to the game files](#2-connect-skyrender-to-the-game-files)
- [3. Show a NIF in a WPF window](#3-show-a-nif-in-a-wpf-window)
- [4. Render your own scene to PNG (offscreen)](#4-render-your-own-scene-to-png-offscreen)
- [5. Render an NPC portrait](#5-render-an-npc-portrait)
- [6. The interactive character viewer](#6-the-interactive-character-viewer)
- [7. Extending a scene](#7-extending-a-scene)
- [8. Terrain and water](#8-terrain-and-water)
- [9. Configuration](#9-configuration)
- [10. Threading and requirements](#10-threading-and-requirements)
- [11. Troubleshooting](#11-troubleshooting)

## 1. Install

```xml
<!-- Scenes, NIFs, offscreen rendering (net8.0) -->
<PackageReference Include="SkyRender" Version="3.0.0" />
<!-- NPCs, portraits and the WPF character viewer (net8.0-windows10.0.19041.0); includes SkyRender -->
<PackageReference Include="SkyRender.Character" Version="3.0.0" />
```

To show the renderer inside a WPF window you also need a GL host control, for example
`OpenTK.GLWpfControl` (4.3.3 is what SkyRender is tested with).

## 2. Connect SkyRender to the game files

SkyRender does not read plugins or know where the game is installed. You give it three small
objects (see [`Hosting.cs`](../samples/SkyRender.Demo/Hosting.cs) for complete implementations):

| Interface | What you provide |
|---|---|
| `IDataFolderProvider` | The game's `Data` folder (loose files resolve against it) and a token that changes when the load order does. |
| `IBsaArchiveProvider` | Lookup and extraction of files inside BSA archives. The sample builds it on Mutagen's archive reader. |
| `IRenderLogger` | Where log lines go. |

Then build the asset pipeline once and share it:

```csharp
var logger   = new MyLogger();
var logGate  = new RenderLogGate();               // logGate.Verbose = true for detailed logs
var resolver = new GameAssetResolver(new MyDataFolder(dataPath), new MyBsaProvider(dataPath), logGate, logger);
var assets   = new AssetCache(resolver, logger, logGate);   // decoded textures + parsed NIFs, RAM-budgeted
```

`GameAssetResolver` answers "where is `textures\foo.dds` really": loose file, archive (extracted
on demand into a cache folder), or missing. Game-relative paths are used everywhere.

## 3. Show a NIF in a WPF window

The core building blocks, in the order a scene is built:

1. **Parse** (no GL needed, safe on a worker thread):
   `List<NifMeshBuilder.BuiltMesh> shapes = assets.MeshBuilder.BuildFromFile(absoluteNifPath);`
2. **Upload and bind materials** (GL thread, context current):

   ```csharp
   var textures = new GlTextureManager(assets, logger);
   textures.Initialize();
   foreach (var shape in shapes)
   {
       var mesh = GlMeshFactory.Create(shape);                       // VBO + CPU copies
       NifMaterialBinder.Bind(textures, resolver, mesh, shape,       // textures, shader flags,
           shape.TexturePaths, new MaterialBindOptions());           // alpha, specular, ...
       renderer.AddMesh(mesh);
   }
   ```
3. **Draw** every frame with a `GlRenderer` and an `OrbitCamera`:

   ```csharp
   // once, on the GL thread
   var renderer = new GlRenderer();
   renderer.Initialize(ModuleResourceLocator.ShaderDirectory);   // shaders are embedded too
   // per frame, into the currently bound framebuffer
   renderer.Render(camera, widthPx, heightPx);
   ```

   `new GlRenderer()` draws with the `Scene` profile: the game's clear noon daylight, fog and image
   space, made for whole scenes. A single model usually reads better under the studio rig the
   character tier uses, `new GlRenderer(RenderProfile.Character)`; the sample starts that way and
   its **Game daylight** box switches profiles (a renderer's profile is fixed when it is created,
   so it makes a new renderer and uploads the model again).

With OpenTK.GLWpfControl, start the control with an OpenGL 3.3 context and do steps 2-3 in its
`Render` callback:

```csharp
GlControl.Start(new GLWpfControlSettings { MajorVersion = 3, MinorVersion = 3, RenderContinuously = true });

// GLWpfControl 4.x hooks its render loop up in IsVisibleChanged, which never fires for a control
// that is already visible when Start() runs. Toggle visibility once, or no frame is drawn until
// something else forces a redraw.
GlControl.Visibility = Visibility.Collapsed;
GlControl.Visibility = Visibility.Visible;
```

Forward mouse input to the camera with `camera.OnMouseDown(x, y, leftButton, middleButton)`,
`OnMouseMove`, `OnMouseUp` and `OnMouseWheel(delta)` (left drag orbits, middle drag pans).

**Framing.** Point `camera.Target` at the scene's centre and set `camera.Distance` so the
bounding sphere fits the field of view: `radius / sin(FieldOfView / 2)` plus a margin. NIF models
face **-Z**, so `Azimuth = 180` looks at the front. For scenes much larger or smaller than a
character, also set `renderer.ShadowBounds = (centre, radius)` so the shadow map covers them.

**Placing several objects.** Give each mesh a `GlMesh.ModelTransform` (an OpenTK `Matrix4`:
scale, rotation, translation) instead of moving its vertices. For a placed reference (REFR),
`SkyRender.World.ReferencePlacement.ModelTransform(position, angles, scale,
ReferencePlacement.ReadRootTransform(nifPath))` builds it from the record's values in Skyrim units,
with the engine's rotation order and without the model's root transform, as the game places it.

**Cleaning up.** `renderer.ClearMeshes()` disposes the meshes; dispose the `GlTextureManager` and
the `GlRenderer` on the GL thread when you are done.

## 4. Render your own scene to PNG (offscreen)

The offscreen renderer owns a hidden GL context and a render thread. You describe what to draw
with an `IOffscreenSceneBuilder`:

```csharp
sealed class NifScene(List<NifMeshBuilder.BuiltMesh> shapes, GameAssetResolver resolver, AssetCache assets)
    : IOffscreenSceneBuilder
{
    public IOffscreenScene Build(OffscreenSceneContext ctx, OffscreenRenderRequest request)
    {
        var renderer = new GlRenderer();
        renderer.Initialize(ModuleResourceLocator.ShaderDirectory);
        var textures = new GlTextureManager(assets, new MyLogger(), ctx.ResidentTextures);
        textures.Initialize();
        foreach (var s in shapes)
        {
            var mesh = GlMeshFactory.Create(s);
            NifMaterialBinder.Bind(textures, resolver, mesh, s, s.TexturePaths, new MaterialBindOptions());
            renderer.AddMesh(mesh);
        }
        var camera = new OrbitCamera();
        // ... frame the camera as in section 3 ...
        return new Scene(renderer, camera, textures);
    }

    sealed class Scene(GlRenderer r, OrbitCamera c, GlTextureManager t) : IOffscreenScene
    {
        public GlRenderer Renderer => r;
        public OrbitCamera Camera => c;
        public void Dispose() { r.ClearMeshes(); t.Dispose(); r.Dispose(); }
    }
}

// Create the renderer once (on the main thread) and reuse it for many renders.
await using var offscreen = OffscreenRendererFactory.Create(new NifScene(shapes, resolver, assets),
    assets, resolver, logGate, logger);
byte[] png = await offscreen.RenderToPngAsync(new OffscreenRenderRequest { Width = 1024, Height = 768 });
```

`OffscreenRenderRequest` also carries the background colour, lighting layout and colour scheme,
post-processing toggles (tone mapping, shadows, ambient occlusion, bloom, vignette, exposure),
cancellation, timing output and lists that report missing meshes and textures.
`RenderToBgra32Async` returns raw pixels instead of a PNG.

## 5. Render an NPC portrait

`SkyRender.Character` adds an NPC scene builder. Describe the NPC with a `ResolvedNpcMeshPaths`
(game-relative paths your host resolved from the NPC record) and render an `NpcRenderRequest`:

```csharp
var previewCache = new CharacterPreviewCache(myNpcMeshDataSource, resolver, logger, logGate, mySettings);
await using var portraits = NpcOffscreenRendererFactory.Create(previewCache, resolver, mySettings, logGate, logger);

var paths = new ResolvedNpcMeshPaths
{
    HeadMeshPath  = @"meshes\actors\character\facegendata\facegeom\skyrim.esm\00013255.nif",
    FaceTintPath  = @"textures\actors\character\facegendata\facetint\skyrim.esm\00013255.dds",
    BodyMeshPath  = @"meshes\actors\character\character assets\malebody_1.nif",
    HandsMeshPath = @"meshes\actors\character\character assets\malehands_1.nif",
    FeetMeshPath  = @"meshes\actors\character\character assets\malefeet_1.nif",
    SkeletonPath  = @"meshes\actors\character\character assets\skeleton.nif",
    Gender        = NpcGender.Male,
};

byte[] png = await portraits.RenderToPngAsync(new NpcRenderRequest
{
    MeshPaths = paths,
    Width = 512, Height = 512,
    Camera = CameraFraming.PortraitDefault,      // or OrbitState / Fixed / MeshAware
    EnableToneMapping = true, EnableShadows = true, EnableAmbientOcclusion = true,
});
```

- `INpcMeshDataSource` resolves an `NpcIdentity` to `ResolvedNpcMeshPaths` for the viewer's
  identity-based loading; a host that always passes paths directly can return `null`.
- `ICharacterViewerSettings` persists the viewer's lighting and render choices. Everything has a
  default except the lighting layout / colour-scheme names, the two user preset lists, the
  verbose-log flag and `PropertyChanged`, so a minimal implementation is a few lines.
- Outfits, headgear and other worn meshes go in `NpcRenderRequest.MeshOverrides`
  (`MeshOverride`, with biped slots so armor hides the body and headgear hides hair);
  replacement textures in `TextureOverrides`.
- `PrewarmAsync(request)` parses and decodes on a worker thread ahead of the render, which keeps
  the GL thread busy only with uploads when you render many NPCs.

## 6. The interactive character viewer

`VM_CharacterViewer` is a view model you host in your own GL control. It is built on ReactiveUI,
which (from version 20) the application must initialize once, before the first viewer is created;
otherwise the constructor throws "ReactiveUI has not been initialized":

```csharp
// at startup, for example in App.OnStartup
RxAppBuilder.CreateReactiveUIBuilder().WithWpf().BuildApp();   // namespace ReactiveUI.Builder
```


```csharp
var viewer = new VM_CharacterViewer(resolver, mySettings, previewCache, logGate, logger);
await viewer.LoadAsync(new NpcIdentity(cacheKey, displayName), paths);
```

In the GL control's render callback:

```csharp
if (!viewer.IsGlInitialized) viewer.InitializeGl(ModuleResourceLocator.ShaderDirectory);
viewer.ProcessPendingScene();                       // installs a loaded scene in slices
viewer.Renderer.Render(viewer.Camera, widthPx, heightPx);
```

Forward mouse input to `viewer.Camera` as in section 3. The two WPF panels
`UC_CharacterViewerLightingPanel` and `UC_CharacterViewerRenderPanel`
(`SkyRender.Character.Views`) bind to the view model and expose its lighting and render settings.
`ApplyMeshOverrides` and `ApplyTextureOverrides` change the loaded scene in place.

## 7. Extending a scene

- **Overlays.** Implement `IRenderOverlay` and add it to `renderer.Overlays`. Its `Draw` runs
  every frame after the scene and can use the renderer's overlay primitives
  (`BeginOverlaySurfacePass` / `BeginOverlayLinePass`, `DrawOverlayMarkers`, `DrawOverlayLines`,
  `DrawOverlaySolidTriangles`, `DrawOverlayTranslucentTint`, `DrawOverlayVertexColorTriangles`) for
  markers, lines and highlighted surfaces.
- **Deforming body shapes.** Implement `IShapeDeformer` and call
  `viewer.ApplyShapeDeformer(deformer, weight)`. The viewer calls `Deform` once per body shape with
  the whole vertex array (after the engine's weight blend, before normals and skinning), queues it
  until a scene is ready, and re-applies it after later loads.
- **Your own data on a mesh or viewer.** `GlMesh.HostData` and `VM_CharacterViewer.HostData` hold
  any object you like; the renderer never reads them.
- **Hiding meshes.** `GlMesh.IsHiddenByHost` hides a mesh without disturbing the renderer's own
  missing-texture handling. `GlMesh.HiddenPartitionSlots` hides only the triangles of the given
  dismember-partition slots (a `1 << (slot - 30)` mask), for shapes that carry
  `TrianglePartitionSlots`; the character viewer sets it from the worn pieces.

## 8. Terrain and water

SkyRender draws landscape and water from plain data your host reads out of the LAND, LTEX/TXST and
WATR records (`SkyRender.World`; Skyrim Z-up world units). Build on the GL thread:

```csharp
using SkyRender.World;

// One LAND record: decoded heights, optional normals / colours, and each quadrant's textures.
var cell = new TerrainCell(gridX, gridY, LandHeightDecoder.Decode(vhgtOffset, vhgtGradients))
{
    Normals = TerrainCell.DecodeNormals(vnmlBytes),   // optional (signed bytes)
    Colors  = TerrainCell.DecodeColors(vclrBytes),    // optional
};
cell.Quadrants[(int)TerrainQuadrantIndex.BottomLeft] = new TerrainQuadrant
{
    Base = new TerrainTexture(@"textures\landscape\dirt02.dds", @"textures\landscape\dirt02_n.dds"),
    Layers = new[] { new TerrainLayer(new TerrainTexture(@"textures\landscape\fieldgrass02.dds"), opacity17x17) },
};

var terrain = GlWorldFactory.CreateTerrain(cell, textures);   // four GlMesh, one per quadrant
terrain.AddTo(renderer);

var water = GlWorldFactory.CreateWater(
    new WaterSurface(waterHeight, x0, y0, x1, y1,
        new WaterMaterial(shallowRgb, deepRgb, reflectionRgb, reflectivity, fresnel)
        {
            NoiseTexturePath = @"textures\water\...",
            ReflectsSky = true,                          // reflect renderer.SkyColor, as the game does
            NoiseFalloff = noiseFalloff,                 // WATR noise falloff: near Fresnel, far mirror
            DepthFog = new WaterDepthFog(fogNear, fogFar, fogAmount),  // WATR above-water fog
            PerPixelDepth = true,                        // depth along each view ray (one depth prepass)
            SunSpecularPower = sunSpecPower,             // WATR sun specular power / magnitude and
            SunSpecularMagnitude = sunSpecMagnitude,     //   depth specular lighting
            DepthSpecularLighting = depthSpecular,
        }),
    new TerrainHeightField(new[] { cell }), textures);
renderer.AddMesh(water);

// Optional sky by elevation for what sky-reflecting water reflects, per pixel (for a Skyrim
// exterior: the weather's horizon / sky-lower / sky-upper colours mixed by the sky dome's masks).
renderer.SkyGradient = new SkyGradient(new[]
{
    new SkyGradientKey(0f, horizonRgb), new SkyGradientKey(18f, skyLowerRgb), new SkyGradientKey(90f, skyUpperRgb),
});

// Optional point lights (for a Skyrim cell, its placed LIGH references): linear colour already
// times the fade, in the scene's mesh space; shadow-casting lights get a cube shadow map.
renderer.PointLights.Add(new PointLight(lightPos, lightRgbTimesFade, radius) { CastsShadow = castsShadow });

// Optional distance fog over every lit surface (for an exterior, the weather's fog).
renderer.Fog = new DistanceFog(fogNearRgb, fogFarRgb, near, far, power, max);

// Optional engine image space (for an exterior, the weather's IMGS for the time of day). The
// engine also scales the sun's colour by the image space's sunlight scale: do that on the key light.
renderer.SetKeyLight(azimuthDeg, elevationDeg, intensity01: sunlightScale, sunRgb);
renderer.ImageSpace = new ImageSpaceFinish(white, saturation, brightness, contrast,
                                           tintRgb, tintAmount, AdaptedLuminance: 0.367f);
```

Defaults match the game (blend and tiling were checked against in-game screenshots); see
`TerrainBuildOptions` and [RENDERING_PIPELINE.md, Part 6](RENDERING_PIPELINE.md#part-6--terrain-and-water).
A host that keeps its own mesh cache can build the data off-thread with `WorldMeshBuilder` and use
`GlWorldFactory.UploadTerrain` / `ApplyTerrainMaterial` (and the water equivalents) on meshes it owns.
Dispose a `GlTerrainCell` and the water mesh on the GL thread.

**Projected materials.** Snow, ash or moss that a static's material object lays over a mesh is a
per-mesh setting, drawn inside the mesh's own shader:

```csharp
mesh.HasProjectedMaterial = true;
mesh.ProjectedParams = GlMesh.ProjectedParamsFor(falloffScale, falloffBias, noiseUvScale, maxAngleDegrees);
mesh.ProjectedColor = singlePassColor;                    // 0..1
mesh.ProjectedDiffuseTiling = 0.2f;                       // fProjectedUVDiffuseNormalTilingScale
mesh.ProjectedNoiseTexture = textures.LoadTexture(@"textures\effects\projectednoise.dds");
mesh.ProjectedDiffuseTexture = textures.LoadTexture(@"textures\effects\projecteddiffuse.dds");
```

See [RENDERING_PIPELINE.md, Stage 2b](RENDERING_PIPELINE.md#stage-2b-projected-material).

**Effect shapes.** Fire, smoke, mist and light shafts sit on a `BSEffectShaderProperty`
(`BuiltMesh.HasEffectShader`). Opt in to the engine's effect shading when binding:

```csharp
NifMaterialBinder.Bind(textures, assets, mesh, built, built.TexturePaths,
    new MaterialBindOptions { ShadeEffectShaders = true });
```

A host with its own texture cache calls `NifMaterialBinder.ApplyEffectMaterial(mesh, built,
sourceTexture, hasSourceTexture, greyscaleTexture)` instead. The shading is static: animation
controllers and particle systems are not drawn. See
[RENDERING_PIPELINE.md, Effect shaders](RENDERING_PIPELINE.md#effect-shaders-bseffectshaderproperty).

**Grass.** The grass the game draws in loaded exterior cells: the GRAS records each landscape
texture lists (LTEX GNAM), grown on your `TerrainCell`s and drawn instanced:

```csharp
using SkyRender.World;

// Each base and alpha layer names the grasses of its LTEX (GRAS fields as read from the record).
var fieldGrass = new GrassType
{
    Id = grasFormId, ModelPath = @"meshes\landscape\grass\fieldgrass01.nif",
    Density = 64, MinSlope = 0, MaxSlope = 45, PositionRange = 30, HeightRange = 0.38f, ColorRange = 0.2f,
    Flags = GrassFlags.UniformScaling | GrassFlags.FitToSlope,
};
var tex = new TerrainTexture(@"textures\landscape\fieldgrass02.dds") { Grasses = new[] { fieldGrass } };

// Off the GL thread if you like. Settings are the [Grass] INI values (defaults: the engine's).
var placement = GrassPlacer.Place(cells, new GrassSettings { StartFadeDistance = 7000f },
    waterHeight: cell => cellWaterHeightOrNull, camera: eyeZUp);   // the camera skips cells beyond the fade

// On the GL thread: for each grass type, fresh meshes for its model's shapes in the model's own space
// (as NifMeshBuilder emits them), uploaded and given their diffuse and alpha threshold.
var grass = GlWorldFactory.CreateGrass(placement, type => LoadGrassShapes(type.ModelPath));
grass.AddTo(renderer);
// placement.InstanceCount / Types say what was placed; grass.MissingModels what could not be drawn.
```

The placement follows the GRAS parameters (density, spacing, slope, water, scale, brightness) with
SkyRender's own seeded random numbers: the counts and spread match the engine's rules, the exact
positions are not the game's. Grass is lit by the sun (under its shadows) and the ambient, fades out
at the INI's fade distance, and casts no shadow. Dispose `GlGrass` on the GL thread. See
[RENDERING_PIPELINE.md, Grass](RENDERING_PIPELINE.md#grass).

**Distant LOD.** A worldspace's terrain, object, tree and water LOD, drawn beyond the cells you load:

```csharp
using SkyRender.World;

// Off the GL thread if you like: reads lodsettings\<w>.lod, picks the blocks the engine would draw
// for this camera, reads them and builds the meshes, cut away under the loaded cells.
var lod = DistantLodLoader.Load(new DistantLodRequest
{
    Worldspace = "Tamriel",                          // the WRLD EditorID the LOD files are named after
    Eye = cameraPosition,                            // Skyrim world units, Z up
    LoadedCells = new LoadedCells(loadedCellXYs),    // the cells you draw at full detail
    MaxDistance = 409600f,                           // your far plane; push it out for LOD
    // Kinds = LodKinds.Terrain | LodKinds.Objects,  // default: all four (terrain, objects, trees, water)
    // Distances = new LodDistances { ... },         // [TerrainManager] values; defaults are the prefs'
}, assets.ResolveAssetPath);                         // game-relative path -> file on disk, or null

// On the GL thread. LOD water needs the worldspace's LOD water type (WRLD NAM3) as a WaterMaterial,
// built like a cell's water from its WATR; without one the water parts are skipped.
var lodWater = new WaterMaterial(shallow, deep, reflection, reflectivity, fresnel)
{
    NoiseTexturePath = noiseLayerOne, NoiseUvScale = noiseUvScale, ReflectsSky = true,
    NoiseFalloff = noiseFalloff, SunSpecularPower = sunPower, SunSpecularMagnitude = sunMagnitude,
};
var glLod = GlWorldFactory.CreateDistantLod(lod, textures, lodWater);
glLod.AddTo(renderer);
// lod.BlocksSelected / BlocksFound / Parts / Errors say what went in.
```

Terrain LOD draws its block's diffuse and model-space normal texture with the engine's LOD noise
term; object LOD its atlas (times its vertex colours, where the shape has them); tree LOD crossed
planes from the tree atlas; LOD water the terrain blocks' water quads, at their own heights, as the
engine's LOD water technique (sky reflection, sun specular, fog; no depth or refraction). Under the
loaded cells the terrain is lowered and covered triangles dropped, as the engine does, and the
objects, trees and LOD water of those cells are left out (draw the cells' own water). Grass LOD is
not a kind of its own: where a LOD generator bakes it (DynDOLOD, from a grass cache), it is part of
the object LOD files and draws with them; vanilla Skyrim has none. Reload when the camera moves to another cell. Dispose `GlDistantLod` on
the GL thread. See [RENDERING_PIPELINE.md, Distant LOD](RENDERING_PIPELINE.md#distant-lod).

## 9. Configuration

| Setting | Where |
|---|---|
| Verbose logging | `RenderLogGate.Verbose` |
| RAM budget for decoded textures and parsed NIFs | `IRenderSettings.CacheMode` / `FixedCacheBudgetBytes` / `FreeRamCachePercent` |
| Archive extraction folder | `new GameAssetResolver(..., extractionDirectory: path)` (default: `SkyRenderCache` beside the application) |
| Compressed texture uploads | environment variable `SKYRENDER_DDS_MAX_FINAL_MIP_DIMENSION` (`decoded` disables them; see `DdsMipPolicy`) |
| Texture upload diagnostics | environment variable `SKYRENDER_DDS_DIAGNOSTICS=1` |
| Shader hot-editing | edit the `Shaders\*.vert/.frag` copied beside the assembly; they take precedence over the embedded copies |

### Defaults

`new GlRenderer()` draws a scene at full detail; the character tier creates its renderer with
`new GlRenderer(RenderProfile.Character)` and keeps the studio look. Without weather or cell data a
scene renderer uses vanilla clear-day values (`SceneEnvironment.VanillaClearDay`, from Update.esm's
SkyrimClear and ISSkyrimClearDAY; values in RENDERING_PIPELINE.md, "Scene defaults"). Anything a
host sets wins.

| Feature | Default | Opt out |
|---|---|---|
| Sky gradient, sun, ambient (vanilla clear day) | on (`Scene`) | set `ClearColor`, `SkyGradient = null`, `SetKeyLight`, ... or use `RenderProfile.Character` |
| Distance fog | on (`Scene`) | `renderer.Fog = null` |
| Image space (engine tone map and grade) | on (`Scene`) | `renderer.ImageSpace = null` (back to ACES and bloom) |
| Your own scene values | - | `renderer.ApplyEnvironment(new SceneEnvironment(...))` or set the properties |
| Effect-shader shading (fire, smoke, glows) | on | `new MaterialBindOptions { ShadeEffectShaders = false }` |
| Billboards turned toward the camera | on | `renderer.Billboards = false` |
| Per-pixel water depth and sun specular | on (water with `DepthFog`; the sun specular needs the water type's values) | `WaterMaterial { PerPixelDepth = false }` |
| Point lights | drawn when added | leave `renderer.PointLights` empty |
| Sun shadows, ambient occlusion | off | `EnableShadows`, `EnableAmbientOcclusion` to turn on |
| Character portraits and bodies | studio rig, ACES, bloom | (unchanged) |

## 10. Threading and requirements

- Windows with an OpenGL 3.3 capable GPU (the offscreen renderer requests a 4.0 core context).
- `SkyRender` targets `net8.0`; `SkyRender.Character` targets `net8.0-windows10.0.19041.0`
  (WPF). Newer .NET versions consume both. A project that uses `SkyRender.Character` needs a
  Windows platform version of at least 10.0.19041 in its target framework (for example
  `net8.0-windows10.0.19041.0`), because its ReactiveUI.WPF dependency ships its modern .NET
  build only for that platform; see [Troubleshooting](#11-troubleshooting).
- All GL work happens on the thread that owns the context: your GL control's render callback, or
  the offscreen renderer's own thread. Parsing (`NifMeshBuilder`) and decoding (`AssetCache`) are
  GL-free and thread-safe.
- Create the offscreen renderer on the application's main thread (its window library installs a
  hook on the first thread that touches it), then call it from anywhere.
- The coordinate system is Y-up, as converted from the NIF's Z-up; models face -Z.

See [RENDERING_PIPELINE.md](RENDERING_PIPELINE.md) for how the renderer works inside, and
[CHARACTER_PIPELINE.md](CHARACTER_PIPELINE.md) for what the character tier adds.

## 11. Troubleshooting

### NU1202 when installing SkyRender.Character

`Package SkyRender.Character is not compatible with net8.0-windows7.0` (or another plain
`-windows` target, which is what the WPF project template writes) means the project's Windows
platform version is below 10.0.19041. Raise it in the project file:

```xml
<TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
```

The requirement comes from ReactiveUI.WPF 23.x, which ships its .NET 8/9/10 builds only for
`windows10.0.19041`. Under a plain `-windows` target NuGet would fall back to its .NET Framework
build (warning NU1701), so the character tier pins the platform to get the real one. The core
`SkyRender` package has no such requirement.

### Warning MSB3246: "Resolved file has a bad image, no metadata"

Every project that references SkyRender, directly or through SkyRender.Character, reports this
once. It comes from the niflysharp 1.3.0 package (SkyRender's NIF library): its `lib/net5.0`
folder holds `niflycpp.dll`, a native DLL, next to the managed `nifly.dll`, so the build offers
the native one to the compiler as a reference and warns that it has no .NET metadata. It is
harmless: `niflycpp.dll` is still copied to the output folder, where `nifly.dll` loads it at run
time.

To silence it, either suppress the code:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);MSB3246</NoWarn>
</PropertyGroup>
```

or, to keep MSB3246 for every other reference, drop just that file from the compile references
(it stays in the output folder):

```xml
<Target Name="DropNiflyNativeReference" AfterTargets="ResolveLockFileReferences"
        BeforeTargets="ResolveAssemblyReferences">
  <ItemGroup>
    <Reference Remove="@(Reference)"
               Condition="'%(Reference.NuGetPackageId)' == 'niflysharp' and '%(Reference.Filename)%(Reference.Extension)' == 'niflycpp.dll'" />
  </ItemGroup>
</Target>
```
