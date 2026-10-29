# Using SkyRender

This guide walks through the common tasks, from "show one NIF" to NPC portraits. The runnable
version of sections 2-3 is the sample app in [`samples/SkyRender.Demo`](../samples/SkyRender.Demo).

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

## 1. Install

```xml
<!-- Scenes, NIFs, offscreen rendering (net8.0) -->
<PackageReference Include="SkyRender" Version="3.0.0" />
<!-- NPCs, portraits and the WPF character viewer (net8.0-windows); includes SkyRender -->
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
scale, rotation, translation) instead of moving its vertices.

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

`VM_CharacterViewer` is a view model you host in your own GL control:

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
  missing-texture handling.

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
    Layers = new[] { new TerrainLayer(new TerrainTexture(@"textures\landscapeieldgrass02.dds"), opacity17x17) },
};

var terrain = GlWorldFactory.CreateTerrain(cell, textures);   // four GlMesh, one per quadrant
terrain.AddTo(renderer);

var water = GlWorldFactory.CreateWater(
    new WaterSurface(waterHeight, x0, y0, x1, y1,
        new WaterMaterial(shallowRgb, deepRgb, reflectionRgb, reflectivity, fresnel)
        { NoiseTexturePath = @"textures\water\..." }),
    new TerrainHeightField(new[] { cell }), textures);
renderer.AddMesh(water);
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

## 9. Configuration

| Setting | Where |
|---|---|
| Verbose logging | `RenderLogGate.Verbose` |
| RAM budget for decoded textures and parsed NIFs | `IRenderSettings.CacheMode` / `FixedCacheBudgetBytes` / `FreeRamCachePercent` |
| Archive extraction folder | `new GameAssetResolver(..., extractionDirectory: path)` (default: `SkyRenderCache` beside the application) |
| Compressed texture uploads | environment variable `SKYRENDER_DDS_MAX_FINAL_MIP_DIMENSION` (`decoded` disables them; see `DdsMipPolicy`) |
| Texture upload diagnostics | environment variable `SKYRENDER_DDS_DIAGNOSTICS=1` |
| Shader hot-editing | edit the `Shaders\*.vert/.frag` copied beside the assembly; they take precedence over the embedded copies |

## 10. Threading and requirements

- Windows with an OpenGL 3.3 capable GPU (the offscreen renderer requests a 4.0 core context).
- `SkyRender` targets `net8.0`; `SkyRender.Character` targets `net8.0-windows10.0.19041.0`
  (WPF). Newer .NET versions consume both.
- All GL work happens on the thread that owns the context: your GL control's render callback, or
  the offscreen renderer's own thread. Parsing (`NifMeshBuilder`) and decoding (`AssetCache`) are
  GL-free and thread-safe.
- Create the offscreen renderer on the application's main thread (its window library installs a
  hook on the first thread that touches it), then call it from anywhere.
- The coordinate system is Y-up, as converted from the NIF's Z-up; models face -Z.

See [RENDERING_PIPELINE.md](RENDERING_PIPELINE.md) for how the renderer works inside, and
[CHARACTER_PIPELINE.md](CHARACTER_PIPELINE.md) for what the character tier adds.
