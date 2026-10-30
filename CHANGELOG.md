# Changelog

## 3.0.0

First public release. SkyRender is the renderer previously built from source as
CharacterViewer.Rendering, split into two packages and extended from single models to whole
cells. The sections below describe the release as a whole.

### Packages and core

- Two packages that share one version: `SkyRender` (plain `net8.0`: NIF parsing, Bethesda
  materials and shaders, DDS textures, CPU skinning, the OpenGL renderer, offscreen rendering) and
  `SkyRender.Character` (`net8.0-windows10.0.19041.0`, WPF: NPC assembly and the interactive
  viewer). Neither depends on Mutagen or another game-record library; a host supplies the Data
  folder, archive access and logging through small interfaces (`IDataFolderProvider`,
  `IBsaArchiveProvider`, `IRenderLogger`, `IRenderSettings`).
- `SkyRenderInfo.Version` reads the assembly version, so hosts can check what they loaded.
- Pluggable offscreen scenes (`IOffscreenSceneBuilder`, `OffscreenRendererFactory`), rendering to
  PNG or raw BGRA32 pixels, with a built-in PNG encoder and no image-library dependency.
- Building blocks for a host's own scenes: `GlMeshFactory`, `NifMaterialBinder` with
  `MaterialBindOptions`, per-mesh `ModelTransform`, `ShadowBounds`, the `AssetCache` with public
  cache telemetry, host overlays (`IRenderOverlay` and the renderer's overlay primitives), and
  `GlMesh.HostData` for a host's own per-mesh data.
- DDS textures upload their authored mip chains compressed by default (see
  `DdsMipPolicy` and RENDERING_PIPELINE.md, "DDS upload policy"); `decoded` opts out.
- GLSL shaders are embedded in the assembly and also copied beside it, so a `PackageReference`
  needs no setup and a local build can hot-edit a shader.
- The packages carry the README, the licence text and the third-party notices. The character
  tier's build-time weavers (PropertyChanged.Fody, ReactiveUI.Fody) are private to its build and
  are not package dependencies.

### Character tier

- NPC assembly from host-resolved paths (`ResolvedNpcMeshPaths`): FaceGen heads and face tints,
  body parts, worn meshes with biped-slot occupancy (`MeshOverride`), replacement textures, and a
  skeleton.
- NPC portraits offscreen (`NpcOffscreenRendererFactory`, `NpcRenderRequest`, `CameraFraming`,
  `PrewarmAsync`).
- The interactive viewer view model `VM_CharacterViewer` with two WPF panels
  (`UC_CharacterViewerLightingPanel`, `UC_CharacterViewerRenderPanel`), lighting layouts and
  presets, and a whole-array mesh-deform hook (`IShapeDeformer`,
  `VM_CharacterViewer.ApplyShapeDeformer`).
- Characters use `RenderProfile.Character` (the studio rig, ACES tone map and bloom), so portraits
  keep their look while the scene defaults below change.

### World: terrain, water, grass and distant LOD (`SkyRender.World`)

- Terrain from LAND data: heights, normals, vertex colours and up to eight painted layers per
  quadrant with their normal maps, blended as the engine does. A null `TerrainQuadrant.Base` draws
  white; the game's default landscape texture is the host's fallback.
- Water surfaces following the engine's water shader: the reflection mixed over the lit body by
  `lerp(1, fresnel, distanceFactor)`, sky reflection (`WaterMaterial.ReflectsSky`) along the
  reflected ray from a host-supplied `SkyGradient`, depth fog and refraction weight
  (`WaterDepthFog`), per-pixel view-ray depth against a depth prepass
  (`WaterMaterial.PerPixelDepth`, on by default), and the water type's sun specular.
- Near-field grass: `TerrainTexture.Grasses` (the landscape texture's grass types),
  `GrassPlacer.Place` (GL-free, by the engine's placement rules with repeatable seeded positions),
  and `GlWorldFactory.CreateGrass`, one instanced mesh per grass type and model shape, shaded as the
  engine's grass (one directional light, the sun's plain colour, distance fade before the alpha
  test; receives sun shadows, casts none).
- Distant LOD: `DistantLodLoader.Load` reads a worldspace's terrain, object and tree LOD and its LOD
  water around a camera, cut away under the host's loaded cells; `GlWorldFactory.CreateDistantLod`
  uploads it. Terrain LOD has the engine's noise term and far depth bias, object LOD honours its
  vertex colours, tree LOD draws as crossed planes.
- `ReferencePlacement`: a placed reference's `ModelTransform` from its position, rotation and
  scale with the engine's rotation order, without the model's root-node transform.

### Materials

- Effect shaders (`BSEffectShaderProperty`): the engine's static effect shading, including the
  greyscale palette, base colour scale, falloff, soft depth fade, clamp, depth flags, lighting
  influence and alpha blending (`MaterialBindOptions.ShadeEffectShaders`, on by default).
- Billboards (`GlRenderer.Billboards`, on by default): shapes under an `NiBillboardNode` turn
  toward the camera each draw, per billboard mode.
- Projected materials (`GlMesh.HasProjectedMaterial`): the engine's single-pass projected-UV
  covering (snow on rock) evaluated per pixel against the normal-mapped normal.
- Environment maps looked up in the game's Z-up axes, scaled by the light reaching the surface,
  with glossiness as the mask on shapes without one.
- Tree foliage keeps its leaves: on `SLSF2_Tree_Anim` shapes the vertex alpha is the wind weight,
  not opacity. Glow maps and own-emit colours are applied by the binder.

### Lighting and scene defaults

- `RenderProfile`: `GlRenderer` defaults to `Scene`, which starts from
  `SceneEnvironment.VanillaClearDay` (the vanilla clear-day weather, its image space and the noon
  sun of the default climate): sky gradient, sun, ambient, distance fog and image space, one
  directional light with no fill light, no rim light and no daylight boost. `ApplyEnvironment`
  sets a host's own `SceneEnvironment`; `SkyGradient.FromSkyDome` and
  `SceneEnvironment.SunDirection` build one from a weather and a climate.
- Distance fog (`GlRenderer.Fog`, `DistanceFog`) in the engine's form and parameter packing.
- Image space (`GlRenderer.ImageSpace`, `ImageSpaceFinish`): the engine's HDR tone map and
  cinematic grade in place of the ACES finish.
- Point lights (`GlRenderer.PointLights`): up to 32, the engine's falloff, cube-map shadows for up
  to four.
- RENDERING_PIPELINE.md records each choice and the in-game comparisons behind it, including what
  is not yet matched (bloom, eye adaptation, particles).

### Samples

- `samples/SkyRender.Demo`: a minimal WPF viewer for one NIF with a log pane. It draws with the
  `Character` profile, which suits a single model; **Game daylight** switches to `Scene`.
- `samples/SkyRender.CellDemo`: a WPF viewer for any interior or exterior cell of the installed
  game (terrain, water, grass, weather or cell lighting, statics, effects, placed lights, optional
  distant LOD and neighbouring cells). It reads the game through Mutagen, a dependency of the
  sample only. Its 3D view fills its panel and follows the window's size.
- `samples/SkyRender.CharacterDemo`: a WPF viewer for any NPC of the installed game, drawn with
  their default outfit by the character tier, with a small Mutagen-backed `INpcMeshDataSource`.

### Breaking changes from CharacterViewer.Rendering 2.x

- Namespaces: core types are in `SkyRender` and `SkyRender.Offscreen`; NPC types, the viewer and
  its views are in `SkyRender.Character`, `SkyRender.Character.Offscreen` and
  `SkyRender.Character.Views`.
- Renamed: `CharacterViewerRendering.Version` to `SkyRenderInfo.Version`,
  `CharacterViewerLogGate` to `RenderLogGate`, `ICharacterViewerLogger` to `IRenderLogger`, the
  lighting layout, colour scheme and preset types to `LightingLayout`, `LightingColorScheme` and
  `LightingPresets`, the NPC gender enum to `NpcGender` (`ResolvedNpcMeshPaths.Gender`),
  `GlMesh.HiddenBySlotOccupancy` to `GlMesh.IsHiddenByHost`, and
  `GlRenderer.MeasurementLineSegment` to `OverlayLineSegment`. Slot data moved to `mesh.Npc()`
  (`NpcMeshInfo`, attached through `GlMesh.HostData`). The environment variables and the default
  extraction folder use the `SKYRENDER` / `SkyRenderCache` names; the old variable names are still
  read.
- `ICharacterViewerSettings` keeps its property names; its cache members moved to the base
  interface `IRenderSettings`.
- `VM_CharacterViewer`, `NpcSceneBuilder` and the NPC offscreen factory no longer take mesh
  deformer or morph-file parser arguments; shape deformation is the host's, through
  `IShapeDeformer`. The NPC factory is `NpcOffscreenRendererFactory.Create(previewCache, assets,
  settings, logGate, logger)`.
- `OffscreenRenderRequest` holds only generic settings; NPC renders use `NpcRenderRequest`, which
  carries the mesh paths, overrides, camera framing, `BeforeDraw` and the character tuning
  toggles. `Morphs` / `MorphWeight` are removed.
- Morph-file parsing, mesh deformation and measurement overlays are no longer part of the
  library; hosts implement them on `IShapeDeformer` and `IRenderOverlay`.
- `new GlRenderer()` now draws with the `Scene` profile. A host that relied on the old studio
  look passes `RenderProfile.Character`.
