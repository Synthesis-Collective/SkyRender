# Changelog

## Unreleased

- Point lights (`GlRenderer.PointLights`, `PointLight`): up to 32 point lights over every lit
  surface, Lambert diffuse with the engine's `1 - saturate(d / radius)^2` falloff. Lights with
  `CastsShadow` (up to four per frame) cast shadows through a depth cube map each. Empty by
  default, so characters and existing scenes are unchanged. The shader now asks for
  `GL_ARB_texture_cube_map_array` (core in GL 4.0).
- Sky gradient (`GlRenderer.SkyGradient`, `SkyGradient`, `SkyGradientKey`): a host-supplied sky
  colour by elevation. Water that reflects the sky reflects the gradient at the elevation of the
  reflected view ray, per pixel, instead of one sky colour, as the engine's water samples its sky
  reflection along the reflected ray. Null by default (unchanged output); characters are unaffected.
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
- Water per pixel (`WaterMaterial.PerPixelDepth`, `GlMesh.WaterSceneDepth`): the water depth is
  measured along each pixel's view ray against a depth prepass of the opaque scene, and drives the
  body colour, the engine's refraction weight and how much of the bed shows; far away the surface
  is all reflection, as in the game. Adds the engine's sun specular (`SunSpecularPower`,
  `SunSpecularMagnitude`, `DepthSpecularLighting`). Opt-in; one extra depth prepass per frame while
  such water is drawn. Characters are unaffected.
- Distance fog (`GlRenderer.Fog`, `DistanceFog`): the engine's fog over every lit surface, from
  host-supplied colours and distances. Off by default, so characters are unaffected.
- Image space (`GlRenderer.ImageSpace`, `ImageSpaceFinish`): the engine's HDR tone map and
  cinematic grade (Reinhard curve on luminance, saturation, tint, brightness and contrast about the
  adapted luminance) in place of the ACES finish, from host-supplied image-space values. Off by
  default, so characters are unaffected. Docs: the fog parameter packing is now sourced.
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
