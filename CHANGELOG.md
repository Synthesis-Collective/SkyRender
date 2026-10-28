# Changelog

## 3.0.0

First public release. Earlier versions were consumed locally as `CharacterViewer.Rendering`; see
[docs/MIGRATING_FROM_CHARACTERVIEWER_RENDERING.md](docs/MIGRATING_FROM_CHARACTERVIEWER_RENDERING.md).

- Split into `SkyRender` (core, plain `net8.0`) and `SkyRender.Character` (NPC assembly and the WPF
  viewer).
- Pluggable offscreen scene builders (`IOffscreenSceneBuilder`), host overlays (`IRenderOverlay`),
  a whole-array shape-deform hook (`IShapeDeformer`), `NifMaterialBinder` / `GlMeshFactory`,
  per-mesh `ModelTransform`, `ShadowBounds`, `AssetCache`, public cache telemetry.
- Built-in PNG encoder; no image-library dependency.
