# Using SkyRender

This guide walks through the common tasks, from "show one NIF" to NPC portraits. The runnable
version of sections 2-3 is the sample app in [`samples/SkyRender.Demo`](../samples/SkyRender.Demo)
(it still hosts a `GLWpfControl` itself);
[`samples/SkyRender.CellDemo`](../samples/SkyRender.CellDemo) puts sections 7-8 together into a
whole cell read from the game's plugins, in the live view of section 3 (`SkyRenderView`), and
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
<PackageReference Include="SkyRender" Version="4.0.0" />
<!-- A live view in a WPF window: paced frames, fly controls, frame-rate readout (net8.0-windows); includes SkyRender -->
<PackageReference Include="SkyRender.Wpf" Version="4.0.0" />
<!-- NPCs, portraits and the WPF character viewer (net8.0-windows10.0.19041.0); includes SkyRender -->
<PackageReference Include="SkyRender.Character" Version="4.0.0" />
```

To show the renderer inside a WPF window, use `SkyRender.Wpf`'s `SkyRenderView`
([section 3](#the-live-view-skyrenderview)). It hosts the GL output and paces the frames, so you
do not need a GL host control of your own.

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

### Archive files from memory

If your BSA provider can hand over a file's bytes, also implement `IBsaArchiveByteReader` on the
same object. The resolver then reads archive files into memory instead of extracting them, so
rendering writes nothing to the cache folder (the CharacterDemo sample does this). A provider
without it keeps extracting, unchanged.

An in-memory asset still has a path-shaped `AssetSource.ResolvedDiskPath` (under
`<cache>\memory\`), so caches and logs work as before, but no file exists there and
`AssetSource.IsInMemory` is true. SkyRender's own readers handle it. Your code must too: wherever
it reads a resolved path itself (for example in an `IShapeDeformer`, or by passing
`ResolvedDiskPath` to your own NIF reader), use `resolver.OpenResolvedRead(path)`,
`ReadResolvedBytes`, `AssetFileExists` or `NifFileLoader.TryLoad(path, resolver)` rather than
`File` APIs. Files *next to* a resolved archive file (a sibling `.tri`, say) are not there in
either mode; resolve them by their own game path.

Recently read files are kept up to `InMemoryAssetBudgetBytes` (default 128 MB) and read from the
archive again after eviction. `GetBsaAssetStats()` reports reads, re-reads, bytes and time in
either mode, for comparing it with extraction (`ExtractBsaAssetsToDisk = true`).

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

### The live view: `SkyRenderView`

`SkyRender.Wpf`'s `SkyRenderView` is a WPF element that shows the scene. Put it in the window and
do steps 2-3 in its events; all three run on the UI thread with the view's GL context current:

```csharp
var view = new SkyRenderView();                    // SkyRenderView.DefaultMode: a native child window
GlRenderer? renderer = null;
view.GlReady     += () => { renderer = new GlRenderer(); renderer.Initialize(ModuleResourceLocator.ShaderDirectory); /* step 2 */ };
view.RenderFrame += frame => renderer!.Render(view.Camera, frame.Width, frame.Height);
view.GlClosing   += () => renderer?.Dispose();     // free GL objects here, while the context still works
HostPanel.Children.Add(view);
// when the window closes: view.Dispose() in Window.Closing (not Closed)
```

What it does for you:

- **Paces the frames.** One frame per display refresh, posted to the UI thread above input, so
  mouse messages cannot hold frames back. By default it draws only while something changed
  (`RenderMode = ViewRenderMode.OnDemand`): input, the camera (`OrbitCamera.ViewChanged`), a held
  fly key, the view's size, or your own `view.Invalidate("why")`. A still view costs nothing.
  Set `KeepDrawing` to a function that returns true while your scene still needs frames (an upload
  running a slice per frame, an animation), or `RenderMode = ViewRenderMode.Continuous` to draw
  at every refresh.
- **Moves the camera** (`view.Camera`, `view.Controls`): left-drag orbits, right-drag looks around
  from where the eye stands, middle-drag pans, the wheel zooms, and W A S D / Q E fly while held,
  Shift faster. Flying moves **by the clock**, a distance for the time since the last frame, not a
  step per key-repeat message, so it is as smooth as the frame rate. `Controls.FlySpeed` is in
  units a second. `ControlsEnabled = false` leaves the camera to you; `view.Input` gives you every
  mouse and key event either way (for picking, or keys of your own).
- **Measures itself.** `ShowReadout = true` shows a one-line readout above the view: frames a
  second, the median frame time, the mean of the slowest 1%, the worst, and the render call.
  `StartFrameLog(path)` / `StopFrameLog()` write one CSV row per frame (`FrameStats.CsvHeader` names
  the columns) and return the summary. `PlayMotion(ViewMotion.Look(...))` and
  `ViewMotion.Fly(...)` drive the view through its real input path with no hand on the mouse, so
  you can log a look-around or a fly and compare builds. If a view feels jittery, log it before
  changing anything: [Troubleshooting](#the-view-is-slow-or-jittery) says how to read the result.

**Two hosts.** `new SkyRenderView(ViewHostMode.NativeWindow)` (the default) draws into a native
child window inside your WPF window and presents with `SwapBuffers`.
`new SkyRenderView(ViewHostMode.WpfTexture)` draws into a texture shared with WPF
(`OpenTK.GLWpfControl`, the `WGL_NV_DX_interop` driver extension) on WPF's own frame tick.

| | `NativeWindow` (default) | `WpfTexture` |
|---|---|---|
| Frame timing | its own pacer: the desktop compositor while that keeps the display's rate, else a high-resolution clock | WPF's frame tick, which follows the desktop compositor |
| WPF content over the view | not possible: a child window sits above everything WPF draws in its rectangle (the readout is a strip above the view for that reason) | yes: it is a WPF element like any other |
| Needs | any OpenGL 3.3 driver | a driver with `WGL_NV_DX_interop` |
| Input | the view's own window; `view.Input` and the built-in controls work the same | WPF events on the element, and `view.Input` |

Choose `WpfTexture` when WPF elements must overlap the view (an overlay, a popup, a transform or
opacity on the view itself). Otherwise keep the default: its frame rate does not depend on the
compositor's (CHANGELOG 4.0.0 has the measurements).

**On your own GL context.** If your scene already lives on a GL context of yours (built on a
hidden window before any view exists), hand that context to the view instead of letting it make
one:

```csharp
var view = new SkyRenderView(ViewGlContext.From(myHiddenWindow));         // an OpenTK NativeWindow or GameWindow
// or, with only the handles: ViewGlContext.FromHandles(hiddenHwnd, hglrc)
view.RenderFrame += frame => myRenderer.Render(view.Camera, frame.Width, frame.Height);
```

- The view is always the `NativeWindow` host. Its child window takes your hidden window's pixel
  format, so multisampling is whatever that format has.
- The view never disposes the context. When the view's window goes, it leaves your context
  current on your hidden window, so your GL calls keep working.
- `GlReady` and `GlClosing` are still raised with the context current. If your GL objects outlive
  the view, leave them alone in `GlClosing`.
- The context must be on the UI thread the view is on, and OpenTK's GL bindings must already be
  loaded (creating any OpenTK window does that).
- `WpfTexture` views always make their own context.

Create the view on the application's main thread. To draw into the view outside a frame, call
`view.MakeCurrent()` first. In either host, mouse positions in `view.Input` are in WPF units from
the view's top-left corner; pass `view.ViewSize.Height` with them to `OrbitCamera.OnMouseMove` if
you drive a camera yourself.

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

### Top-down map tiles

For a map, draw the scene from straight above with no perspective. Three pieces, each usable alone:

```csharp
// 1. The camera: a world rectangle and a height range, in Skyrim units (X east, Y north, Z up).
//    The rectangle fills the image; north is up, east is right.
var camera = new TopDownCamera(west: 20480, south: -12288, east: 28672, north: -4096,
                               floor: -40000, ceiling: 60000);
var (width, height) = camera.ImageSize(unitsPerPixel: 16);   // 512 x 512

// 2. The light: the same for every tile, no weather, no time of day, no fog.
renderer.ApplyEnvironment(SceneEnvironment.TopDownMap);
renderer.EnableShadows = false;

// 3. Nothing behind the scene: alpha 0 where no geometry is, so the tile lays over your own drawing.
byte[] png = await offscreen.RenderToPngAsync(new OffscreenRenderRequest
{
    Width = width, Height = height,
    TransparentBackground = true,
    Scene = camera,            // whatever your builder reads; return the camera from IOffscreenScene.Camera
});
```

- **Joining tiles.** Rectangles that share an edge, drawn at the same scale, join without a seam.
  Keep each side a whole multiple of the scale (a 4096-unit cell at 16 units a pixel is 256 pixels).
- **Height range.** Only what lies between `floor` and `ceiling` is drawn. Lower the ceiling to
  leave out what is above it; geometry it cuts through is left open.
- **Many tiles from one scene.** Loading is the cost, not drawing. Keep the `GlRenderer`, its meshes
  and its textures in your builder between renders, return an `IOffscreenScene` whose `Dispose`
  frees nothing, and give each request only a new camera. `samples/SkyRender.CellDemo/TileRun.cs`
  does this. The renderer draws every mesh of the scene for every tile (it does not cull by the
  camera's rectangle), so load the cells near the view, not a whole worldspace.
- **Load the neighbours.** An object belongs to the cell its origin is in. A building that stands
  over a cell border, or over a hole in the next cell's terrain, is missing from a tile whose scene
  holds only the cells the rectangle touches.
- **Alpha.** The PNG is RGBA and the BGRA32 buffer keeps its alpha; both are straight alpha, not
  premultiplied. Leave `EnableToneMapping` off with a transparent background.
- **Cost.** Four cells of Whiterun (897 objects, 268 textures) at 512 x 512: about 2.1 s for the
  first tile (reading, parsing and uploading the scene), then about 6 ms a tile as BGRA32 or 29 ms
  as PNG, on an RTX 5090 desktop. PNG compression is most of a warm tile.

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
| Archive files from memory | implement `IBsaArchiveByteReader` on the BSA provider; `GameAssetResolver.ExtractBsaAssetsToDisk` forces extraction, `InMemoryAssetBudgetBytes` sizes the byte cache |
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
- `SkyRender` targets `net8.0`; `SkyRender.Wpf` targets `net8.0-windows` (WPF);
  `SkyRender.Character` targets `net8.0-windows10.0.19041.0` (WPF). Newer .NET versions consume both. A project that uses `SkyRender.Character` needs a
  Windows platform version of at least 10.0.19041 in its target framework (for example
  `net8.0-windows10.0.19041.0`), because its ReactiveUI.WPF dependency ships its modern .NET
  build only for that platform; see [Troubleshooting](#11-troubleshooting).
- All GL work happens on the thread that owns the context: the view's `GlReady`, `RenderFrame`
  and `GlClosing` events (the UI thread), or the offscreen renderer's own thread. Parsing (`NifMeshBuilder`) and decoding (`AssetCache`) are
  GL-free and thread-safe.
- Create the offscreen renderer on the application's main thread (its window library installs a
  hook on the first thread that touches it), then call it from anywhere.
- The coordinate system is Y-up, as converted from the NIF's Z-up; models face -Z.

See [RENDERING_PIPELINE.md](RENDERING_PIPELINE.md) for how the renderer works inside, and
[CHARACTER_PIPELINE.md](CHARACTER_PIPELINE.md) for what the character tier adds.

## 11. Troubleshooting

### The view is slow or jittery

Measure before changing anything; the cause is often not the renderer.

1. Turn the readout on (`view.ShowReadout = true`) and read its `render` figure. If the render
   call is a few milliseconds and the frame time is not, the scene is not the problem: the frames
   are being scheduled late.
2. Log a motion: `view.StartFrameLog("frames.csv")`, then
   `view.PlayMotion(ViewMotion.Look(8, 180), _ => Console.WriteLine(view.StopFrameLog()?.Words()))`
   (and the same with `ViewMotion.Fly(8)`). Look at the count of intervals over 25 ms, not the
   mean. In the CSV, `queued_ms` is how long a posted frame waited for the UI thread (your own
   work on that thread shows up here), `reason_age_ms` is how long the change waited for its frame,
   and `render_ms` / `gpu_ms` are the scene's own cost.
3. Check the desktop itself: `CompositorTiming.MeasureCompositionMs()` (off the UI thread)
   returns the time between desktop compositions. It should be the display's refresh period (16.7 ms
   at 60 Hz). When it is not (46 ms and 140 ms have been measured on a 60 Hz display, on a
   desktop whose compositor had gone slow), everything paced by the compositor is slow with it:
   WPF's frame tick, and so `ViewHostMode.WpfTexture` and any `GLWpfControl`, and a loop that
   waits on `DwmFlush`. The default host notices and keeps its own clock; its readout then ends
   with "paced by the view's own clock", and the log's `host` column reads `native/clock`.
4. `SKYRENDER_VIEW_PACER=compositor` or `=clock` (an environment variable) keeps the default host
   to one timing source, to tell the two apart.

If you host the renderer yourself instead of using `SkyRenderView`, the two mistakes that were
measured in a real application are: queuing frames on the dispatcher below input priority
(`DispatcherPriority.Background`: frames waited 50 to 300 ms while the mouse moved; use
`DispatcherPriority.Render`), and moving the camera once per key-repeat message (about 30 steps a
second after a half-second delay, whatever the frame rate; move by elapsed time each frame).

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

SkyRender 3.x and earlier caused this: their NIF library (niflysharp 1.3.0) shipped a native
`niflycpp.dll` in its `lib` folder. SkyRender 4.0 reads NIFs with the managed Nifly package and
has no native dependency, so the warning, and any `NoWarn` or target added to silence it, can go.
