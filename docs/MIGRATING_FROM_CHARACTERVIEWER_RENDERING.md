# Migrating from CharacterViewer.Rendering 2.x to SkyRender 3.0

SkyRender 3.0 is the first public release of the renderer previously consumed (by source or a
local package) as `CharacterViewer.Rendering`. The code is the same renderer, reorganised into two
packages so that scene rendering does not drag in character or WPF concerns. Output is unchanged:
the same requests render the same pixels.

## 1. Pick packages

| You use | Reference |
|---|---|
| NIF parsing, GL rendering, textures, offscreen rendering of your own scenes | `SkyRender` |
| NPC assembly, the character viewer VM / WPF panels, NPC portraits | `SkyRender.Character` (brings `SkyRender`) |

```xml
<PackageReference Include="SkyRender.Character" Version="3.0.0" />
```

`SkyRender` targets plain `net8.0` and has no WPF, ReactiveUI or Fody dependency.
`SkyRender.Character` targets `net8.0-windows10.0.19041.0` (WPF).

**Co-developing against source.** A conditional reference keeps a sibling checkout authoritative
when present and falls back to the package otherwise:

```xml
<ItemGroup Condition="Exists('..\..\SkyRender\src\SkyRender.Character\SkyRender.Character.csproj')">
  <ProjectReference Include="..\..\SkyRender\src\SkyRender.Character\SkyRender.Character.csproj" />
</ItemGroup>
<ItemGroup Condition="!Exists('..\..\SkyRender\src\SkyRender.Character\SkyRender.Character.csproj')">
  <PackageReference Include="SkyRender.Character" Version="3.0.0" />
</ItemGroup>
```

## 2. Namespaces

| 2.x | 3.0 |
|---|---|
| `CharacterViewer.Rendering` (core types) | `SkyRender` |
| `CharacterViewer.Rendering.Offscreen` (renderer, request, timings) | `SkyRender.Offscreen` |
| `CharacterViewer.Rendering` (NPC types, VM) | `SkyRender.Character` |
| `CharacterViewer.Rendering.Offscreen` (NPC builder, framing) | `SkyRender.Character.Offscreen` |
| `CharacterViewer.Rendering.Views` | `SkyRender.Character.Views` |

In XAML: `xmlns:cvr="clr-namespace:SkyRender.Character;assembly=SkyRender.Character"` and
`xmlns:cvrv="clr-namespace:SkyRender.Character.Views;assembly=SkyRender.Character"`.

## 3. Renamed types and members

| 2.x | 3.0 |
|---|---|
| `CharacterViewerRendering.Version` | `SkyRenderInfo.Version` (read from the assembly; 3.0.0) |
| `CharacterViewerLogGate` | `RenderLogGate` |
| `ICharacterViewerLogger` | `IRenderLogger` |
| `CharacterViewerLightingLayout` / `...ColorScheme` / `...Presets` | `LightingLayout` / `LightingColorScheme` / `LightingPresets` |
| `enum Sex`, `ResolvedNpcMeshPaths.Sex` | `enum NpcGender`, `ResolvedNpcMeshPaths.Gender` |
| `ViewerMode.BodySlideEdit` | `ViewerMode.ShapeEdit` |
| `GlMesh.HiddenBySlotOccupancy` | `GlMesh.IsHiddenByHost` |
| `GlMesh.BipedSlots` / `HidesSlots` / `SlotDrawPriority` / `OverrideKey` | `mesh.Npc().BipedSlots` … (`NpcMeshInfo`, attached through `GlMesh.HostData`) |
| `GlRenderer.MeasurementLineSegment` | `OverlayLineSegment` |
| `OffscreenRendererFactory.Create(previewCache, deformer, bsd, tri, assets, settings, gate, logger)` | `NpcOffscreenRendererFactory.Create(previewCache, assets, settings, gate, logger)` |
| env `CVR_DDS_MAX_FINAL_MIP_DIMENSION`, `CVR_DDS_DIAGNOSTICS` | `SKYRENDER_DDS_MAX_FINAL_MIP_DIMENSION`, `SKYRENDER_DDS_DIAGNOSTICS` (old names still honoured) |
| default extraction folder `CharacterViewerCache` | `SkyRenderCache` (or pass `extractionDirectory` to `GameAssetResolver`) |
| core log prefix `CharacterViewer:` | `SkyRender:` |

`ICharacterViewerSettings` keeps its `CharacterViewer*` property names, so persisted settings keep
working; its cache members (`CacheMode`, `FixedCacheBudgetBytes`, `FreeRamCachePercent`) moved to the
base interface `IRenderSettings`, which it extends.

## 4. Constructors

`VM_CharacterViewer`, `NpcSceneBuilder` and the NPC offscreen factory no longer take
`BodySlideDeformer`, `BsdFileParser` or `BodyTriFileParser`:

```csharp
// 2.x
new VM_CharacterViewer(deformer, bsdParser, triParser, assets, settings, previewCache, logGate, logger);
// 3.0
new VM_CharacterViewer(assets, settings, previewCache, logGate, logger);
```

Remove those three registrations from your DI container.

## 5. Offscreen requests

`OffscreenRenderRequest` now holds only generic settings. NPC renders use the subclass:

```csharp
var png = await renderer.RenderToPngAsync(new NpcRenderRequest
{
    MeshPaths = paths,          // required
    MeshOverrides = overrides,
    Camera = CameraFraming.PortraitDefault,
    Width = 512, Height = 512,
});
```

Moved to `NpcRenderRequest`: `MeshPaths`, `OverrideHeadMeshAbsolutePath`, `TextureOverrides`,
`MeshOverrides`, `Camera`, `BeforeDraw`, the attire/override warning out-lists, and the
character-tuning toggles (`ExcludeHairShadowCaster`, `SsaoHairGap`, `EnableEyeCatchlight`,
`SkinSaturationBoost`, `TonemapHairRelief`, `HairAlbedoCompensate`). `Morphs` / `MorphWeight` are
removed (see 6).

To render something other than an NPC, implement `IOffscreenSceneBuilder` and create the renderer
with `OffscreenRendererFactory.Create(sceneBuilder, assetCache, assets, logGate, logger)`.

## 6. Removed: body-shape tooling

BodySlide parsing and deformation (`BodySlideDeformer`, `HeadlessBodyDeformer`,
`BodyTriFileParser`, `BsdFileParser`, `MorphSet`, `SliderHeatmapBuilder`), key-vertex / region /
measurement overlays and the box-criterion types are no longer part of the library. In their
place:

- **`IShapeDeformer`** (`Label`, `Prepare(bodyNifDiskPath, weight)`, `Deform(...)`) +
  `VM_CharacterViewer.ApplyShapeDeformer(deformer, weight)`: the viewer calls `Prepare` once
  (return false to skip, e.g. when there is no data), then `Deform(shapeName, baseMesh, positions,
  weight)` once per body shape with the whole
  position array (after the engine weight blend, before normals and skinning), queues it until the
  scene is ready, and re-applies it on later loads. `ShapesDeformed` fires afterwards.
- **`IRenderOverlay`** registered in `GlRenderer.Overlays`, drawing with the renderer's overlay
  primitives (`BeginOverlaySurfacePass`, `DrawOverlayMarkers`, `DrawOverlayLines`,
  `DrawOverlaySolidTriangles`, `DrawOverlayTranslucentTint`, `DrawOverlayVertexColorTriangles`).
- `VM_CharacterViewer.HostData` lets a host attach its own per-viewer state (and bind XAML to it).
- In offscreen renders, apply a deformer or overlays from `NpcRenderRequest.BeforeDraw`.

## 7. Other changes

- `CharacterPreviewCache` composes the new core `AssetCache` (`.Assets`); `GlTextureManager` and
  the core offscreen renderer take an `AssetCache`.
- PNG encoding no longer uses SixLabors.ImageSharp; if your app used ImageSharp through this
  library, reference it directly.
- The library's `RelayCommand` is internal; use your own.
- New, optional: `NifMaterialBinder` and `GlMeshFactory` (build renderable meshes from
  `NifMeshBuilder` output without the viewer), `GlMesh.ModelTransform` (per-mesh placement),
  `GlRenderer.ShadowBounds`, public cache telemetry on `ResidentTextureCache` / `GlTextureManager`,
  `GlMesh.IsDisposed`, `GameAssetResolver.IsScopeLockedPath`.
