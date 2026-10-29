# Changelog

## Unreleased

- Projected materials (`GlMesh.HasProjectedMaterial`, `GlMesh.ProjectedParamsFor`): the engine's
  single-pass `Projected_UV` path, covering a shape per pixel against its normal-mapped normal with
  a tiling noise, so snow on rock follows the rock's relief instead of smearing across faces. Off
  unless a host sets it; characters are unaffected.
- Effect shaders (`GlMesh.IsEffectShader`, `NifMaterialBinder.ApplyEffectMaterial`,
  `MaterialBindOptions.ShadeEffectShaders`): fire, smoke, mist, light shafts and glows on a
  `BSEffectShaderProperty` are shaded with the engine's static effect path. It covers the source
  texture times base colour, the greyscale palette for colour and alpha, `baseColorScale`,
  view-angle falloff, soft depth fade (with the ambient-occlusion prepass), texture clamp, depth
  test and write flags, lighting influence and `NiAlphaProperty` blending. Effect shapes cast no
  shadow. Off unless a host opts in, so characters are unaffected.
- `BuiltMesh` reads the rest of the effect material: `EffectShaderFlags1/2`,
  `EffectGreyscaleTexture`, `EffectFalloff`, `EffectSoftFalloffDepth`, `EffectLightingInfluence`,
  `EffectUvScale/Offset` and `EffectTextureClampMode`. For any shape it also reports
  `BillboardMode`, the mode of its nearest `NiBillboardNode`. Effect billboards are drawn
  double-sided and their falloff is evaluated as if facing the camera.
- Water follows the engine's water shader: the reflection is mixed over the lit body colour with
  `lerp(1, fresnel, distanceFactor)` (Fresnel near the camera, a mirror beyond the water type's
  noise falloff). New `WaterMaterial.ReflectsSky` (reflect `GlRenderer.SkyColor`, as the game does
  with water reflections on), `NoiseFalloff` and `DepthFog` (`WaterDepthFog`: the above-water fog's
  depth colour and refraction weight, so shallow water shows the ground). Without the new
  properties the reflection is the water type's reflection colour times reflectivity; the earlier
  ad hoc `fresnel x reflectivity x 4` mix toward sky x reflection colour is gone.
- Distance fog (`GlRenderer.Fog`, `DistanceFog`): the engine's fog over every lit surface, from
  host-supplied colours and distances. Off by default, so characters are unaffected.
- Docs: a null `TerrainQuadrant.Base` draws white; the default landscape texture is a host fallback.
- Docs: glow maps (`SLSF2_Glow_Map`) were already applied by `NifMaterialBinder`; the flag table
  and Stage 5 now say so.

## 3.0.0

First public release.

- Two packages: `SkyRender` (core, plain `net8.0`) and `SkyRender.Character` (NPC assembly and
  the WPF viewer).
- Pluggable offscreen scene builders (`IOffscreenSceneBuilder`), host overlays (`IRenderOverlay`),
  a whole-array shape-deform hook (`IShapeDeformer`), `NifMaterialBinder` / `GlMeshFactory`,
  per-mesh `ModelTransform`, `ShadowBounds`, `AssetCache`, public cache telemetry.
- Native terrain and water (`SkyRender.World`): LAND heights, normals, vertex colours and up to
  eight painted layers per quadrant with their normal maps, blended in the lit shader; water
  surfaces with depth-based colour and live-camera Fresnel. Blend and tiling matched against
  in-game captures.
- Trees keep their foliage: on `SLSF2_Tree_Anim` shapes the vertex alpha is the wind weight, not
  opacity.
- Built-in PNG encoder; no image-library dependency.
- Usage guide (`docs/USAGE.md`), pipeline references for the core and the character tier, a
  minimal sample viewer with a log pane (`samples/SkyRender.Demo`), and authorship and
  acknowledgements (`NOTICE.md`).
