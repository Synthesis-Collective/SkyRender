# Changelog

## 3.0.0

First public release.

- Two packages: `SkyRender` (core, plain `net8.0`) and `SkyRender.Character` (NPC assembly and
  the WPF viewer).
- Pluggable offscreen scene builders (`IOffscreenSceneBuilder`), host overlays (`IRenderOverlay`),
  a whole-array shape-deform hook (`IShapeDeformer`), `NifMaterialBinder` / `GlMeshFactory`,
  per-mesh `ModelTransform`, `ShadowBounds`, `AssetCache`, public cache telemetry.
- Built-in PNG encoder; no image-library dependency.
- Usage guide (`docs/USAGE.md`), pipeline references for the core and the character tier, a
  minimal sample viewer with a log pane (`samples/SkyRender.Demo`), and authorship and
  acknowledgements (`NOTICE.md`).
