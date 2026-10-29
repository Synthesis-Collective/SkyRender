# SkyRender Rendering Pipeline

A reference for how SkyRender parses NIF meshes and renders them, with side-by-side notes on how NifSkope and Outfit Studio handle the same problems. SkyRender renders any NIF content: single models, characters, and whole cells of placed objects. The character tier (SkyRender.Character) builds on this pipeline; its additions are in [CHARACTER_PIPELINE.md](CHARACTER_PIPELINE.md).

---

## Table of contents

0. [DDS upload policy](#dds-upload-policy)
1. [Pipeline overview](#pipeline-overview)
2. [Part 1 — NIF parsing](#part-1--nif-parsing)
   - [Geometry](#geometry)
   - [BSLightingShaderProperty fields](#bslightingshaderproperty-fields)
   - [BSShaderTextureSet slots](#bsshadertextureset-slots)
   - [NiAlphaProperty](#nialphaproperty)
   - [Skinning](#skinning)
   - [Parse cost & performance](#parse-cost--performance)
   - [Caches, settings and host seams](#caches-settings-and-host-seams)
   - [Dismember partitions and shape filtering](#dismember-partitions-and-shape-filtering)
   - [Shader flag inventory](#shader-flag-inventory)
3. [Part 2 — Fragment shader pipeline](#part-2--fragment-shader-pipeline)
   - [Vertex shader (brief)](#vertex-shader-brief)
   - [Section clip plane (Clip F / Clip B)](#section-clip-plane-clip-f--clip-b)
   - [Per-slot texture toggles](#per-slot-texture-toggles)
   - [Stage 1: base color & alpha test](#stage-1-base-color--alpha-test)
   - [Stage 1b: tint operations](#stage-1b-tint-operations)
   - [Stage 1c: skin saturation boost](#stage-1c-skin-saturation-boost)
   - [Stage 2: normal calculation](#stage-2-normal-calculation)
   - [Stage 2b: projected material](#stage-2b-projected-material)
   - [Stage 3: dynamic lighting](#stage-3-dynamic-lighting)
   - [Stage 4: environment mapping](#stage-4-environment-mapping)
   - [Stage 5: emissive](#stage-5-emissive)
   - [Stage 6: tone-map, fresnel, vignette](#stage-6-tone-map-fresnel-vignette)
   - [Final: framebuffer alpha](#final-framebuffer-alpha)
4. [Part 3 — Host overlay pass (markers, lines, surface patches)](#part-3--host-overlay-pass-markers-lines-surface-patches)
   - [The debug shader + overlay GL state](#the-debug-shader--overlay-gl-state)
   - [Marker cost](#marker-cost)
   - [Overlays in offscreen renders](#overlays-in-offscreen-renders)
5. [Part 4 — Comparison with NifSkope and Outfit Studio](#part-4--comparison-with-nifskope-and-outfit-studio)
6. [Part 5 — GL context ownership](#part-5--gl-context-ownership)
7. [Part 6 — Terrain and water](#part-6--terrain-and-water)
8. [Appendix A — Shader flag inventory table](#appendix-a--shader-flag-inventory-table)
9. [Appendix B — BSLightingShaderProperty field inventory](#appendix-b--bslightingshaderproperty-field-inventory)
10. [Appendix C — Texture slot inventory](#appendix-c--texture-slot-inventory)

---

## DDS upload policy

The default is authored-mip compressed uploads with a final-mip limit of 4
(`DdsMipPolicy.DefaultMaximumFinalMipDimension`). `decoded` opts out to decoded
BGRA pixels uploaded as RGBA8 with generated mips. Set
`SKYRENDER_DDS_MAX_FINAL_MIP_DIMENSION` (the former `CVR_DDS_MAX_FINAL_MIP_DIMENSION` is still
honoured as a fallback) before constructing `AssetCache` (or SkyRender.Character's `CharacterPreviewCache`, which composes one),
or initialize its nullable `DdsMaximumFinalMipDimension` property (an
initializer overrides the environment). This bounds both dimensions of the
**last supplied mip**, not level-zero resolution:

| Value | Eligibility |
| --- | --- |
| Unset / empty | Default: final mip's maximum dimension at most 4 |
| `decoded` / property `null` | Decoded route (opt-out) |
| `1` | Full chain through 1x1 |
| `2`, `4`, `16`, or any positive integer | Final mip's maximum dimension at most the limit |
| `unlimited` / property `int.MaxValue` | Any valid supported chain, including one level |

Invalid environment values log an error and use decoded textures. Invalid API
values throw. The setting is fixed for a cache lifetime; recreate the cache when
changing it. The former experimental `CVR_DDS_UPLOAD` switch has been removed.

The compressed route **changes minification**: it uses the original DDS mip
chain. It is not an equality-preserving performance switch, so evidence renders
compared against decoded captures must opt out with `decoded`. GPU-generated compressed
mips also recompress filtered values and are not equivalent to the RGBA8 chain.

Why it became the default (2026-10-25): the September validation measured one
light NPC whose CPU decode was ~7% of render time and found no end-to-end gain.
On a host's real modded mugshot workload decode was ~55% of render slot time, and
this route halved busy time (79 tiles: 50.2 s -> 24.8 s; per-NPC 1.7-2.6x)
with a median per-image mean absolute difference of 0.6/255, concentrated on
alpha-tested hair edges. A single-scene benchmark was also faster (34.4 -> 30.3 s)
with private bytes 8.3 -> 3.6 GB.

| DDS | Authored route |
| --- | --- |
| Legacy DXT1/3/5, DX10 BC1/2/3 UNORM and sRGB | S3TC, subject to active context support |
| ATI1 / BC4U / DX10 BC4 UNORM | RGTC red, swizzled to grayscale RGB, alpha one |
| ATI2 / BC5U / DX10 BC5 UNORM | RGTC RG, blue zero and alpha one, matching the decoder |
| DX10 BC7 UNORM and sRGB | BPTC when supported |
| Signed BC4/5, typeless, BC6H, DXT2/4, premultiplied DX10 | Decoded fallback; no reinterpretation |
| Cubes, arrays, volumes, uncompressed images | Existing decoded/layout-specific route |
| Final supplied mip exceeds configured limit | Decoded fallback with generated full chain |
| Malformed header, overflow, truncated declared supported chain | Explicit error and missing texture; never counted as an upload |

All accepted chains include level zero and every declared mip. Dimensions,
layouts, alpha metadata, mip count and each block span use checked arithmetic.
No top levels are dropped or missing levels generated. GL max level clamps
sampling to the final supplied mip. Incomplete chains can alias when minified
beyond that level; a single-level image has no mip filtering. The parameter is
an eligibility limit, not a visual error bound. Linear GL formats remain intentional even for
sRGB labels: shader gamma/tint math is unchanged. Texture wrap, trilinear
filtering and up to 8x anisotropy follow the ordinary path. Driver queries must
confirm compressed storage and the exact block bytes at every level.

The CPU face/hair tint APIs (`LoadTextureWithFaceTint`/`LoadTextureWithHairTint`)
still clone decoded pixels, but nothing calls them: tints are applied in the
shader. Collision-proxy transparency classification (`IsFullyTransparent`) first
asks `DdsAlphaProbe` to prove a non-zero alpha texel from the compressed blocks
(BC1/2/3/7; an opaque diffuse is settled by its first block) and decodes only
when that proof fails, so its verdict is unchanged; the result is still cached by
physical source. Prewarming warms that verdict for diffuse slots and keeps cube
decodes; eligible 2D slots, FaceTint included, inspect their headers and defer
uploading, avoiding an eager decode. Unsupported GL contexts decode lazily on demand. There is no additional
persistent compressed CPU cache: block buffers are temporary upload inputs.

Resident keys include the numeric threshold and distinguish authored requests from decoded requests while
retaining the resolved physical path. Handles remain context-owned; per-viewer
clear cannot delete resident handles. Resident accounting uses queried block
bytes for compressed levels and exact RGBA8 mip dimensions for decoded levels,
including tiny/rectangular images. Explicit cache clear remains required after
on-disk changes. GL allocation/errors refuse the compressed upload, delete its
partial handle and report missing/error diagnostics; OOM also lowers the
resident budget.

`GlTextureManager.UploadDiagnostics` exposes route, fallback reason, dimensions,
mips, bytes, read/parse and CPU submission times. Eligible compressed reads also
split file open/read (`ReadMs`) from header validation/allocation (`ParseMs`).
`DecodeLookupMs` measures the decoded cache lookup and any synchronous decode;
Pfim's existing decode timer includes its own file I/O and format parsing, which
are not separately instrumented. Null stage fields mean unavailable, not zero.
`SKYRENDER_DDS_DIAGNOSTICS=1` (or the legacy `CVR_DDS_DIAGNOSTICS=1`) writes these and the loaded assembly path/SHA256 to the
host logger. Hosts may still filter messages.
Cache counters
`TotalDecodeCalls`, `TotalDecodedBytes`, `CachedDecodedBytes`, and existing decode
timers report real decoded work (including CPU consumers), not inferred savings.
Submission time is not GPU completion time.

## Pipeline overview

```
.nif file on disk
      │
      ▼
[niflysharp] NifFile.Load(path)
      │
      ▼
[Nif/NifMeshBuilder.cs] BuildFromFile / BuildAllShapes / BuildShape
   ├─ extract per-shape geometry (verts, normals, tangents, UVs, tris)
   ├─ extract BSLightingShaderProperty fields
   ├─ extract BSShaderTextureSet slot paths
   ├─ extract NiAlphaProperty flags + threshold
   ├─ skinned shapes: resolve bones via a skeleton NIF, CPU-side skinning
   └─ produce a list<BuiltMesh> POCOs
      │
      ▼
[Gl/GlMeshFactory.cs] Create — upload geometry into a GlMesh
      │
      ▼
[Gl/NifMaterialBinder.cs] Bind — the NIF-authored material rules
   ├─ host inputs as MaterialBindOptions (skin tint colour, missing-texture
   │  handling, detail-map fallback); SkyRender.Character fills these from NPC data
   ├─ load each texture via GlTextureManager (DDS → GL, compressed or decoded)
   └─ set per-shape material state on the GlMesh (maps, flags, alpha, …)
      │
      ▼
[Gl/GlRenderer.cs] DrawScene
   ├─ shadow pass (Shaders/shadow_depth.*)
   ├─ SSAO depth + normal prepass (Shaders/depth_only.*)
   ├─ SSAO pass (Shaders/ssao.frag, ssao_blur.frag)
   ├─ main pass (Shaders/basic.vert + basic.frag)
   ├─ wireframe pass (Shaders/wireframe.*)
   └─ host overlay pass (Shaders/debug.*) — markers, lines, surface
      patches drawn by IRenderOverlay hosts; depth test OFF (always on top)
```

The data path is intentionally one-way: niflysharp → POCO → GlMesh → uniforms → GL. There's no round-trip back to the NIF, no in-memory edit of NIF data; the renderer is read-only with respect to the file.

---

## Part 1 — NIF parsing

All NIF reads go through [Nif/NifMeshBuilder.cs](../src/SkyRender/Nif/NifMeshBuilder.cs), which takes a path or an open `NifFile` and returns a list of `BuiltMesh` POCOs. The POCO is a snapshot — once it exists the `NifFile` can be disposed without affecting the renderer.

### Geometry

Per shape (`BuildShape` in [NifMeshBuilder.cs](../src/SkyRender/Nif/NifMeshBuilder.cs)):

| Data | Source | Notes |
|---|---|---|
| Vertex positions | `nif.GetVertsForShape(shape)` | Vector3 list |
| Normals | `nif.GetNormalsForShape(shape)` | If absent or all-zero, regenerated from triangle face normals |
| UVs | `nif.GetUvsForShape(shape)` | Vector2 list |
| Triangles | `shape.GetTriangles(out vec)` | Indices into the vertex list |
| Tangents | `nif.GetTangentsForShape(shape)` | When absent, MikkTSpace-style derivation from positions+UVs |
| Bitangents | `nif.GetBitangentsForShape(shape)` | Same fallback |
| Vertex colors | per-vertex via the shape's geometry data | Only when SLSF2_Vertex_Colors is set on the BSLSP **and** the shape actually carries vertex-color data |

Coordinate space conversion: the renderer uses Y-up world space with models facing −Z. NIFs are Z-up, so positions go through `(x, z, −y)` and normals/tangents through the same swizzle plus a re-normalization in [BuildShape](../src/SkyRender/Nif/NifMeshBuilder.cs).

### BSLightingShaderProperty fields

Read in `BuildShape` ([NifMeshBuilder.cs](../src/SkyRender/Nif/NifMeshBuilder.cs)) when the shape's `ShaderPropertyRef` resolves to a `BSLightingShaderProperty`. Each field is read in a `try/catch` so version-specific fields not present on older NIFs simply default rather than throwing. See [Appendix B](#appendix-b--bslightingshaderproperty-field-inventory) for the full read/ignore table.

The fields actually consumed downstream are:

- `shaderFlags1`, `shaderFlags2` — propagated to `BuiltMesh.ShaderFlags1/2` and reinterpreted at the GlMesh-binding stage.
- `bslspShaderType` — propagated to `BuiltMesh.ShaderType`. The renderer branches on `4` (BSLSP_FACE), `5` (BSLSP_SKINTINT), `6` (BSLSP_HAIRTINT), and `16` (BSLSP_EYE).
- `glossiness`, `specularStrength`, `specularColor` — Blinn-Phong specular params.
- `subsurfaceRolloff` — drives the SSS wrap term.
- `rimlightPower` — exponent for the silhouette falloff used by hair backlight and skin rim.
- `grayscaleToPaletteScale` — multiplier for the hair-tint shader path.
- `emissiveColor`, `emissiveMultiple` — emissive contribution. Multiplied by `baseColor.rgb` at the shader's emissive stage so the emissive is "absorbed" by the surface color (matching NifSkope's `albedo * (diffuse + emissive)` math); dark regions of the diffuse don't glow.
- `uvScale`, `uvOffset` — per-shape UV transform applied in the vertex shader.
- `environmentMapScale`, `eyeCubemapScale` — env-map intensity (different fields for normal vs eye shader).
- `hairTintColor` — only when `bslspShaderType == BSLSP_HAIRTINT`.

Fields explicitly ignored (read but not used, or not read at all) include `softlighting`, `backlightPower`, `refractionStrength`, all wetness/parallax/subsurface-color fields, and `skinTintColor`/`skinTintAlpha`. The skin tint stored in the NIF is always `(1, 1, 1)` with `alpha=0` for the bodies we've inspected — the engine doesn't pull body tint from there at runtime; it pulls from the NPC record's `TextureLighting` (QNAM). A host passes it as `MaterialBindOptions.SkinTintColor` (SkyRender.Character fills it from `ResolvedNpcMeshPaths.TextureLightingColor`).

### BSEffectShaderProperty fields

A shape whose shader property is a `BSEffectShaderProperty` sets `BuiltMesh.HasEffectShader`, and its material is read into separate `Effect*` fields so that no lighting-shader field moves for existing consumers:

- `baseColor`, `baseColorScale` go to `EffectBaseColor` / `EffectBaseColorAlpha` and `EffectBaseColorScale`.
- `shaderFlags1/2` go to `EffectShaderFlags1/2`. Same SLSF bit layout; `ShaderFlags1/2` stay zero on effect shapes, as before.
- `greyscaleTexture` goes to `EffectGreyscaleTexture`. The source texture is slot 0, which nifly maps to `sourceTexture`.
- `falloffStart/StopAngle`, `falloffStart/StopOpacity` go to `EffectFalloff`.
- `softFalloffDepth` goes to `EffectSoftFalloffDepth`.
- `uvScale`, `uvOffset` go to `EffectUvScale` / `EffectUvOffset`.
- The texture clamp mode and lighting influence go to `EffectTextureClampMode` and `EffectLightingInfluence`. nifly reads the four bytes nif.xml lists after the source texture (clamp mode, lighting influence, env-map min LOD, unused) as one little-endian `textureClampMode` uint32, so they are split out of it.

For any shape, `BillboardMode` is the `billboardMode` of the nearest `NiBillboardNode` ancestor (-1 when none). The parent walk fetches each parent by block id, because `GetParentNode` returns an untyped `NiNode` wrapper. These fields feed the [effect shading path](#effect-shaders-bseffectshaderproperty).

### BSShaderTextureSet slots

Read via `nif.GetTexturePathByIndex(shape, slot)` for slots 0–8 (`BuildShape` in [NifMeshBuilder.cs](../src/SkyRender/Nif/NifMeshBuilder.cs)).

| Slot | Conventional meaning | Used by renderer |
|---|---|---|
| 0 | Diffuse / albedo | Always |
| 1 | Normal map (TSN or MSN) | Always |
| 2 | Glow / SkinTint / detail palette | For skin shapes only (BSLSP_FACE, BSLSP_SKINTINT) — drives SSS mask. For other shader types, slot 2 has different meaning (glow, detail palette) and is intentionally not bound to avoid spurious red SSS. |
| 3 | Height / Parallax / FacegenDetail | For face shapes when SLSF1_Facegen_Detail_Map is set |
| 4 | Environment cubemap | When SLSF1_Environment_Mapping or SLSF1_Eye_Environment_Mapping is set |
| 5 | Environment mask | Paired with slot 4 |
| 6 | Tint / Inner (FaceTint) | For the primary head shape when the NPC has a FaceTint .dds (slot 6 is unused on body shapes) |
| 7 | Backlight / Specular | When present, used as a specular mask (`.r` channel) |
| 8 | (unused) | Never bound |

Per-NPC texture-set overrides (ARMA TXST) are a SkyRender.Character input; see [CHARACTER_PIPELINE.md](CHARACTER_PIPELINE.md#npc-texture-inputs).

### NiAlphaProperty

Per shape that has an `AlphaPropertyRef` (`BuildShape` in [NifMeshBuilder.cs](../src/SkyRender/Nif/NifMeshBuilder.cs)):

- `flags & 0x0200` → `HasAlphaTest` — gates the `discard` in the fragment shader.
- `flags & 0x0001` → `HasAlphaBlend` — host enables `GL_BLEND` and disables depth write for the shape.
- `threshold / 255.0f` → `AlphaThreshold` — drives the `discard` cutoff.

`SrcBlend` (bits 1-4) and `DstBlend` (bits 5-8) enums in the alpha-property flags are honored per-mesh. NifMeshBuilder extracts the indices, BuiltMesh / GlMesh carry them through to the renderer, and Pass 2 calls `glBlendFunc` per-mesh via a Bethesda-enum → OpenTK `BlendingFactor` mapping in [GlRenderer.cs](../src/SkyRender/Gl/GlRenderer.cs). The vast majority of actor alpha-blended shapes use `SRC_ALPHA / INV_SRC_ALPHA` (standard "over" transparency); the notable exception is the replacer-style wet-eye outer cornea, which ships `SRC_ALPHA / ONE` (additive) so a near-black cornea adds nothing to the iris underneath while bright catchlight pixels add brightness. Honoring the per-mesh factors is what removes the need for any special-case shader logic to render the cornea correctly.

#### `HasAlphaTest` is suppressed for `ShaderType == 4` face shapes

`NifMaterialBinder.Bind` overrides `glMesh.UseAlphaTest = false` when `built.ShaderType == 4 && built.HasAlphaTest`. The NIF's `HasAlphaBlend` and `AlphaThreshold` are preserved unchanged; only the GL-side `discard` gate flips off.

**Observed:** Vanilla `MaleHeadKhajiit` (and likely other beast-race face NIFs) carries `NiAlphaProperty.AlphaTest=True, threshold=73` together with diffuse alpha < 1 across the entire face surface and `SLSF1_Vertex_Alpha` with per-vertex alpha varying down to ~0.325 in a ring around the head/body seam (133 of 1356 vertices on Ri'saad's head). The shader's vertex-alpha-into-texture-alpha multiply produces face fragments with α ≈ 0.27–0.28 along that seam — below the 0.286 threshold — and the GL `discard` drops them. At ~750 px portrait resolution each dropped fragment is a pixel-sized hole that lets the cleared background bleed through; the visible result is the head reading as dimmer or smaller than the body. Vanilla in-game Skyrim does **not** produce this discard pattern.

**Inferred but not verified:** The engine-side mechanism that makes vanilla Khajiit faces render solid. Plausible candidates we haven't traced: the engine ignoring the alpha-test bit on `BSLSP_FACE`, an alpha-to-coverage path running on a differently-configured framebuffer (note that this renderer's Pass-1 deliberately disables `SAMPLE_ALPHA_TO_COVERAGE` per the comment in `GlRenderer.cs`), `BSLSP_FACE` being routed through a separate engine draw call that doesn't read `NiAlphaProperty`, or the actual in-game vertex-alpha values differing from what niflysharp reads back from the NIF. No engine source or Community Shaders' replacement-shader review has been done for this specific path.

Suppressing the discard at `ShaderType == 4` produces output that empirically matches vanilla in-game appearance even though the engine-side mechanism for the match remains open. The face's alpha data still reaches `FragColor.a` (the shader writes it honestly); the off-screen pipeline's alpha-strip at readback handles the downstream PNG consequence (see [Part 2 framebuffer alpha](#final-framebuffer-alpha)). Vertex_alpha-driven head/body seam fade could be reintroduced via a soft-blend path later if a use case appears; the seam is invisible at typical portrait viewing angles.

### Skinning

CPU-side, in [TryApplyCpuSkinning](../src/SkyRender/Nif/NifMeshBuilder.cs). For each bone the shape weights to:

1. Read the bone name list (`nif.GetShapeBoneList`).
2. Get the inverse bind-pose transform from the **shape NIF** (`nif.GetShapeBoneTransform(shape, i, ...)`).
3. Get the bone's world transform from the **skeleton NIF** (`skeletonNif.GetNodeTransformToGlobal(boneName)`).
4. Compose `boneWorld * inverseBind` → cached `CachedSkinTransform` per bone.

Per vertex: read up to 4 bone-weight pairs from `nif.GetShapeBoneWeights(...)`, accumulate the weighted bone transform on the vertex position and (with the rotation portion only) on the normal. The result becomes the renderer's `Positions` array; the original NIF positions are kept as `BindPosePositions` so a host can deform the bind pose and re-skin it.

Why CPU-side skinning instead of a GPU vertex shader doing it: host shape deformation is applied to the bind pose and re-skinned on the CPU, and hosts read the skinned positions back (`GlMesh.CpuPositions`) for picking and measuring. Scenes are posed once (there is no animation), so skinning on the CPU costs nothing per frame. Doing skinning in-shader would require re-running the morph at every redraw, multiplying CPU work for no rendering gain.

#### Shape deformation

The core exposes the pieces a host needs to deform skinned geometry and re-skin it (`BindPosePositions`, `NifMeshBuilder.RecalculateNormals` / `ApplySkinning`, public `SkinningInfo.BoneTransforms` and `CachedSkinTransform`). The character tier's whole-array deformation hook, `IShapeDeformer`, is described in [CHARACTER_PIPELINE.md](CHARACTER_PIPELINE.md#shape-deformation-hook-ishapedeformer).

### Parse cost & performance

In batch rendering (for example a gallery of NPC portraits) parsing NIFs is the dominant CPU cost, so the pipeline works hard to hide it. Two mechanisms:

- **Parse cache** ([NifMeshBuilder.cs](../src/SkyRender/Nif/NifMeshBuilder.cs) `BuildFromFile`): keyed on `(nifPath, nifMTime, skeletonPath, skelMTime, bipedBodyPart)`, holds cloned `BuiltMesh` snapshots. Shared body parts (`Body`/`Hands`/`Feet`/`Hair`/`Tail`) are LRU-**protected**; the per-NPC FaceGen head and null-part attire/outfit meshes are evicted first (they're one-shot). Measured hit rate ≈ 81% on a ~94-NPC mod.
- **Prewarm offload** ([GameWindowOffscreenRenderer.cs](../src/SkyRender/Offscreen/GameWindowOffscreenRenderer.cs) `PrewarmAsync` → `IOffscreenSceneBuilder.Prewarm`; for NPC renders `NpcSceneBuilder.Prewarm` → `CharacterPreviewCache.PrewarmNpc`): the GL-free parse + DDS decode run on a thread-pool worker ahead of the single GL render thread, so by the time a tile renders it hits warm caches and the render thread pays only GL upload + draw + readback. After a full prewarm the render thread's own `parseMs` is ≈ 0; the parse cost lives on the workers.

**Where the parse cost goes** (measured, prewarm-worker, ~94-NPC mod; per cache-missed part):

| Phase | Share of parse | What it is |
|---|---|---|
| `NifFile.Load` | ~10% | native file parse — at the libnifly floor |
| **Geometry marshaling** | **~51%** | the per-element `vectorVector3.getitem` + `.x/.y/.z` reads over verts/normals/UVs/tangents/bitangents/colors/triangles |
| Skinning | ~36% | per-vertex bone-weight reads (same per-element marshaling) + per-bone transform setup + the C# skinning math |
| rest | ~3% | shader/texture/alpha reads |

The geometry slice is expensive because niflysharp (a SWIG binding) surfaces each `std::vector<Vector3>` as a wrapper whose indexer does **one managed→native crossing per element**, returning a finalizable `Vector3` whose `.x/.y/.z` are three more crossings — so reading one vertex costs ~4 crossings + one GC-tracked allocation. A high-poly head runs to ~190k crossings + ~48k finalizable objects. There is **no bulk-copy fast path** in the stock binding (`ToArray`/`CopyTo` are per-element), and niflysharp 2.0.x does not add one (and is net10-only, so unusable from this net8 library).

The only lever that removes this is a native bulk-copy helper (`memcpy` from the contiguous `std::vector<Vector3>` into a managed `float[]`), which must live inside the niflyswig binding — a fork + native rebuild. It would roughly halve parse cost; **deferred** as of 2026-06 given the build/maintenance overhead.

**Instrumentation** (gated, production-safe — zero cost unless the trigger file is present): drop `LogNifCacheDiag.txt` next to the exe → `RenderLogs/NifCacheDiag.log` prints a `parse split` line every 25 parses (load/build %, then geom/skin shares of build). Drop `LogRenderTimings.txt` → `RenderLogs/RenderTimings.csv` gets per-NPC `loadMs`/`buildShapesMs` columns (captured on the prewarm worker). Counters: `_threadLoad/Build/Geom/SkinTicks` in [NifMeshBuilder.cs](../src/SkyRender/Nif/NifMeshBuilder.cs).

**In-RAM cache byte budgets** ([SystemMemoryBudget.cs](../src/SkyRender/SystemMemoryBudget.cs)). Three decode caches are byte-budgeted rather than entry-capped (a 4K texture is ~16× a 1K one, so a count cap is meaningless): the **pixel** cache (decoded BGRA32, dominant), the **mesh** parse cache (built geometry), and the **cubemap** cache (envmaps). They hold a fixed **75 : 9 : 1** ratio — fractions `0.75 / 0.09 / 0.01` of free RAM summing to `0.85` (`BaselineFreeRamFraction`). The ratio was calibrated from a 50-NPC prewarm measurement: pixel demand dominated (~2 GB resident), mesh was tiny (~57 MB, and in practice bounded by its 96-entry count cap, not bytes), cubemap negligible (~0.1 MB). The mesh share is set higher than its measured demand (0.09 vs a demand-matched ~0.04) as deliberate headroom for high-poly mesh replacers, where 96 cached entries *can* exceed the byte budget and would otherwise evict below the count cap. Sizing is governed by `IRenderSettings` (which `ICharacterViewerSettings` extends) and re-polled every N inserts (so a host change lands within a few renders), across three modes ([RenderCacheMode](../src/SkyRender/RenderCacheMode.cs)):

- **`PercentFreeRam`** (default) — the caches may collectively use `FreeRamCachePercent`% (default **85**, reproducing the historical fractions) of *reclaimable-free* RAM (OS-free + what the cache already holds, minus a `max(2 GB, 20%)` headroom reserve), split by the ratio. **The same percent applied to total physical RAM is the upper ceiling** — one knob is the single source of truth for both target and cap; there is no separate per-cache ceiling. Raising it caches more aggressively; lowering it frees RAM for the rest of the app.
- **`FixedRam`** — the ratio is applied to a user-set fixed pool (`FixedCacheBudgetBytes`) instead of live free RAM, capped at each cache's ratio share of total physical RAM so an oversized pool can't exceed the machine.
- **`Disabled`** — budget 0; caches retain nothing (a render still holds the pixels it fetched, so this is safe, just non-reusing).

A host can expose these three settings to its users. The 75 : 9 : 1 ratio was validated against a 50-NPC prewarm measurement (diverse NPCs, prewarm path); revisit if the mesh count cap or texture resolutions change materially.

### Caches, settings and host seams

The CPU-side caches and settings are split along the core / character boundary:

- **`AssetCache`** ([AssetCache.cs](../src/SkyRender/Assets/AssetCache.cs)) is the core cache: the shared `NifMeshBuilder` (parsed-NIF LRU), the decoded-DDS pixel and cubemap caches, the fully-transparent verdicts, the decode telemetry (`TotalDecodeMs`, `ThreadDecodeMs`, `DecodeObserver`, `DecodeReason` scopes), `ResolveAssetPath`, `DdsMaximumFinalMipDimension` and `Clear`. It knows nothing about NPCs. `GlTextureManager` and the offscreen renderer (`OffscreenRendererFactory.Create(IOffscreenSceneBuilder, AssetCache, ...)`) take an `AssetCache`.
- **`CharacterPreviewCache`** ([CharacterPreviewCache.cs](../src/SkyRender.Character/Assets/CharacterPreviewCache.cs)) composes one (`Assets`) and adds NPC mesh-path resolution (`GetOrResolveMeshPaths`, invalidated by the data source's token) and the prewarm walk (`PrewarmNpc` and its part / mesh-override / texture helpers). Its asset members forward to `Assets`, so existing callers are unchanged. `IOffscreenRenderer.InvalidateCaches` clears the resident GL textures, then the scene builder's own caches (`IOffscreenSceneBuilder.InvalidateCaches`; the NPC builder drops its resolved mesh paths), then the `AssetCache`, then the resolver's resolve cache -- the same order the combined clear used.
- **`IRenderSettings`** ([IRenderSettings.cs](../src/SkyRender/Abstractions/IRenderSettings.cs)) is what core code reads: the cache sizing (`CacheMode`, `FixedCacheBudgetBytes`, `FreeRamCachePercent`), all default members. `ICharacterViewerSettings` extends it (and `INotifyPropertyChanged`) with the lighting presets and the viewer's persisted render toggles.
- **`GameAssetResolver`** takes an optional `extractionDirectory` (where BSA entries are extracted; default `AppContext.BaseDirectory\SkyRenderCache`) and an init-only `IsScopeLockedPath` predicate: paths it accepts are exempt from every rule that lets the vanilla data folder preempt a mod scope (both vanilla-loose toggles, the engine-order data-folder tiers, scope demotion). The default, `DefaultIsScopeLockedPath`, is the FaceGen tree (NPC-keyed, FormID-named files).
- **Offscreen requests.** `OffscreenRenderRequest` carries only what the renderer itself uses or any scene could (size, background, lighting, scopes, the generic shading toggles, cancellation, out-lists, timings, diagnostics) plus `object? Scene` for a host builder's own input. The NPC builder's request is `NpcRenderRequest : OffscreenRenderRequest`: `MeshPaths` (required), head override, texture / mesh overrides and their out-lists, `Camera` framing, `BeforeDraw`, and the character-tuning toggles (hair shadow caster, SSAO hair gap, eye catchlight, skin saturation, hair relief, hair albedo compensation).
- **Telemetry** for hosts that measure: `ResidentTextureCache` exposes `ProtectedBytes`, `Epoch`, `OomCeilingBytes`, `OverrideBudgetBytes`, `VramQueryAvailable`, `TotalVramBytes`, `RendersSinceRepoll` and a non-promoting `TryPeek(diskPath, out handle)`; `GlTextureManager` exposes `CachedTextureCount`, `CachedTexturePaths`, `OwnedTextureCount`, `OwnedTextureHandles` and `TryPeekCachedTexture`; `GlMesh.IsDisposed`.

### Dismember partitions and shape filtering

This section applies to actor NIFs (bodies, armor, FaceGen heads); other content never carries dismember partitions. NIF shapes that ship with a `BSDismemberSkinInstance` carry a list of dismember-partition IDs — Bethesda's `BIPED_OBJECT` enum (32 = body / torso, 33 = hands, 37 = feet, 30 / 130 / 230 = head, 31 = hair, 40 = tail). NifSkope labels them via the shared body-part enum on each partition entry. The renderer makes two decisions during NIF parse based on these IDs.

**Primary-head election** ([NifMeshBuilder.cs FindAccessoryOffsetAndPrimaryHead](../src/SkyRender/Nif/NifMeshBuilder.cs)). Inside FaceGen NIFs only, the tallest shape (by local Z extent) whose partitions intersect `{30, 130, 230, 1}` is tagged `IsPrimaryHeadShape = true`. Three downstream consumers depend on the flag:

- **MeshAware camera framing** — its `PrimaryHead` selector matches this shape, and `AboveLowerYOfPrimaryHead` filter clips accessory bbox contributions to verts above this shape's chin.
- **FaceTint application** — `ApplyTexturesToGlMesh` blends the per-NPC FaceTint mask onto this shape's slot-0 diffuse.
- **Accessory positioning** — for unskinned shapes whose vertices are authored near the origin, this shape's global transform is used to position the accessory.

The election is gated on the presence of a NIF block named `BSFaceGenNiNodeSkinned` in the file's block table. Vanilla SSE FaceGen NIFs always carry one; body / hands / feet NIFs do not. The scope matters because Bethesda's vanilla child meshes (`ChildFeet.nif`) bundle a placeholder `ChildHead` shape with the same `partitions = [1, 0]` as the FaceGen face mesh. Without the scope, that placeholder would win the election in the body NIF, and the FaceTint-blend branch would then composite the per-NPC FaceTint onto the body-slot TXST diffuse, producing a dark-face artifact.

The partition set is `{30, 130, 230, 1}`. The first three are SSE-modern; partition 1 is the legacy Oblivion-era `BP_HEAD` value. Vanilla child face meshes (`MaleHeadChild`, `ChildHead`) ship with `[1, 0]` and never got migrated to the modern numbering — NifSkope still labels partition 1 as `BP_HEAD` via the body-part enum, so this is the authoritative signal rather than a name heuristic.

**Biped-slot shape filter** ([NifMeshBuilder.cs BuildAllShapes](../src/SkyRender/Nif/NifMeshBuilder.cs)). When a NIF is loaded under one of three body parts:

| Body part label | Required partition |
|---|---|
| `Body` | 32 |
| `Hands` | 33 |
| `Feet` | 37 |

shapes whose dismember partitions don't include that slot are skipped before `BuildShape` runs. Treats each ARMA's WorldModel NIF as the source for one biped slot only. For Dorthe-style child meshes the filter drops `ChildHead`, `EyesChild`, `MouthChild`, `BODY`, `Wrists` from the scene when `ChildFeet.nif` is loaded as Feet — leaving only the actual `Feet` shape (partition 37).

Head / Hair / Tail body parts are intentionally exempt. The reasoning is empirical and discussed under [Survey findings](#survey-findings-shape-filtering) below: dismember partitions are not a reliable indicator of shape role for those NIFs.

**Engine-side mechanism is undocumented.** Skyrim's engine clearly skips these placeholder shapes in-game (no one sees Dorthe with duplicate eyes through her boots), but neither the CK wiki nor any source we have access to describes the precise rule. The most plausible candidate — "engine renders shapes whose partition includes the ARMA's biped slot" — is consistent with the data and with Bethesda's authoring habits, but has not been confirmed against engine source. Treat the biped-slot filter as a working approximation of the engine's behavior, validated empirically (see [Survey findings](#survey-findings-shape-filtering)) but not as a documented contract.

#### Survey findings (shape filtering)

A diagnostic batch run walked one NPC per enabled appearance mod with a non-empty mod folder — 250 NPCs / 2,943 shapes total — and emitted per-shape metadata to CSV. Notable observations:

| Body part | Distinct partition lists observed |
|---|---|
| `Body` | `[32]` (252×), `[38;32;34]` (100×), `[32;38;34]` (99×), `[32;32]` (15×), `[32;34;38]` (3×), `[32;53]` (2×), `[38;32]` (1×), `[32;34]` (1×) |
| `Hands` | `[33]` (233×), `[33;33]` (16×) |
| `Feet` | `[37]` (249×) |

Every Body shape carries partition 32. Every Hands shape carries 33. Every Feet shape carries 37. **The biped-slot filter dropped zero shapes across the corpus** — no false positives across the sampled NPCs.

For Head shapes the partition picture is much messier:

- **58 face accessories carry partition `[32]`** (the body slot) despite being mouth, brows, eyes, lashes, or eyeshadow inside the FaceGen NIF — `FemaleMouthHumanoidDefault` and a number of custom face-accessory shapes. Either an authoring copy-paste or a Bethesda-tools default; either way, partition values do not predict shape role here.
- 5 Hair NIF shapes carry `[32]`. 2 Tail NIF shapes carry `[32]`. 1 Tail shape carries `[37]`.

These are the empirical reason Head / Hair / Tail are exempt from the biped-slot filter — adding them would cull legitimate face accessories.

For primary-head election, all 252 parseable FaceGen NIFs elected exactly one primary head, and none elected outside a `BSFaceGenNiNodeSkinned`-bearing NIF. Four primary-head shapes, all custom follower/overhaul head shapes, have names that do not contain "head".

All four carry partition 230 and were elected via the partition-based path. A name-based detection ("shape name contains 'head'") would have missed all four — confirming partition-based detection is more robust than naming heuristics.

**No shape in the corpus has `flags & 1u` (NiAVObject AppCulled / Hidden) set.** All 2,943 flags values end in `…E` (bits 1/2/3 set, bit 0 clear). Whatever mechanism the engine uses to skip placeholder shapes inside child body NIFs, it is not the AppCulled flag — that's why we can't simply respect it the way [NifSkope's `Node::isHidden`](#NifSkope-comparison-shape-filtering) does and rely on Bethesda's NIFs to carry it.

#### NifSkope comparison (shape filtering)

NifSkope's `Node::isHidden()` only checks `flags.node.hidden` (the AppCulled bit) and walks the parent chain. `Mesh::isHidden()` extends that with "no `TexturingProperty` and no `BSShaderLightingProperty`." There is **zero** partition-based or biped-slot-based filtering in NifSkope's GL pipeline — the only references to `BSDismemberSkinInstance` in NifSkope source are in `spells/skeleton.cpp` for skinning operations, not rendering.

Practical consequence: opening `ChildFeet.nif` directly in NifSkope renders the `ChildHead` / `EyesChild` / `MouthChild` placeholders along with the Feet shape, all visible. NifSkope is a single-file editor with no concept of biped slots or ARMA composition — it has no information that would let it decide a shape "doesn't belong" in a given context. This renderer needs the same information NifSkope lacks (the body-part label the host loaded the NIF under), threaded through `BuildFromFile`'s `bipedBodyPart` parameter.

### Shader flag inventory

What [Appendix A](#appendix-a--shader-flag-inventory-table) catalogs by bit. Summary:

- **Flags we honor directly** (the shader/host branches on them): `Specular`, `Greyscale_To_PaletteColor`, `Environment_Mapping`, `Facegen_Detail_Map`, `Model_Space_Normals`, `Eye_Environment_Mapping`, `Hair_Soft_Lighting`, `Own_Emit`, `Double_Sided`, `Vertex_Colors`, `Glow_Map`, `Soft_Lighting`, `Rim_Lighting`. On effect-shader shapes, when a host opts in ([Effect shaders](#effect-shaders-bseffectshaderproperty)): `Greyscale_To_PaletteColor`, `Greyscale_To_PaletteAlpha`, `Use_Falloff`, `Soft_Effect`, `ZBuffer_Test`, `ZBuffer_Write`, `Double_Sided`, `Effect_Lighting`.
- **Flags we honor *indirectly*** via a different signal that's reliably paired with the flag: `Skinned` (we check niflysharp's bone-list directly), `ZBuffer_Write` (we drive `GL_DEPTH_MASK` from `NiAlphaProperty.HasAlphaBlend`).
- **Flags not yet implemented**: the features exist in the engine and appear in real content, but SkyRender has no code path for them yet.
- **Flags with no rendering effect** for a viewer: local-map and other UI flags, packing hints, reserved bits.

The "not yet implemented" bucket is large. SkyRender was first built for characters and has since grown into cell rendering, so the remaining gaps are mostly the engine paths scenes use: refraction, parallax and multi-layer parallax, decal depth offset, multi-index snow and LOD fade. Landscape and water are drawn natively from host-supplied data ([Part 6](#part-6--terrain-and-water)). Effect-shader shapes (`BSEffectShaderProperty`: fire, smoke, mist, light shafts, glows) have their own static shading path, opt-in per mesh ([Effect shaders](#effect-shaders-bseffectshaderproperty)); their animation controllers and particle systems are not modelled. See Appendix A for the per-flag notes.

### Override channels

Texture, mesh and body-replacement overrides are SkyRender.Character features; see [CHARACTER_PIPELINE.md](CHARACTER_PIPELINE.md#override-channels-textures--meshes).

---

## Part 2 — Fragment shader pipeline

The main fragment shader is [Shaders/basic.frag](../src/SkyRender/Shaders/basic.frag). The vertex shader [Shaders/basic.vert](../src/SkyRender/Shaders/basic.vert) does little beyond standard MVP transforms and TBN construction; everything interesting happens per-fragment.

> **Shader source must be pure ASCII.** No em-dashes, curly quotes or non-breaking spaces in any
> `.vert` / `.frag` file, comments included. NVIDIA's GLSL compiler reports a non-ASCII byte as a
> misleading "unexpected $end at token EOF" error, nowhere near the offending character.

### Vertex shader (brief)

[basic.vert](../src/SkyRender/Shaders/basic.vert) outputs:

- `gl_Position` (clip space) and `v_viewSpacePos` (for view-space lighting math).
- `TexCoords = aTexCoords * u_uvScale + u_uvOffset` — the BSLSP `uvScale`/`uvOffset` get folded in here, not in the fragment shader, so per-fragment tex sampling is just a direct `texture(...)` call.
- `vertexColor` (passed through; only used when `has_vertex_colors`).
- `v_tangentToViewMatrix` — view-space TBN built from per-vertex tangent/bitangent/normal. Used by the tangent-space normal-map path.
- `v_modelToViewNormalMatrix` — used by the model-space normal-map path.
- `v_worldPos` — for shadow-map lookup in the fragment shader.
- `v_worldNormal` — for the `DEBUG_VIZ_WORLD_NORMAL` debug branch.
- `gl_ClipDistance[0]` — the section clip plane; see below.

The shared `u_model` matrix is identity apart from `ModelScale` (the camera orbits a target; scenes are not moved). `DEBUG_VIZ_MSN_NORMAL` relies on that, treating model space and world space as interchangeable, so it is only meaningful for meshes without their own placement.

A mesh may carry its own placement (`GlMesh.ModelTransform`, e.g. a cell reference's position/rotation/scale). Every mesh pass (main, shadow depth, depth prepass, wireframe) then sets `u_model = ModelTransform × shared model` for that draw and restores the shared matrix after it (`ApplyMeshModel`), and the alpha-blend sort uses the same composed matrix. Meshes without a placement cost nothing. The default shadow frustum (centred at Y=85, radius 300) suits one human-sized model; a scene of any other size sets `GlRenderer.ShadowBounds` (centre + radius, pre-ModelScale) to fit it.

### Section clip plane (Clip F / Clip B)

A cutaway: one world-space plane hides either the half of the scene in front of it (**Clip F** — look *into* a closed mesh or building) or the half behind it (**Clip B** — isolate the near surface from the far wall behind it). A host arms it; the character viewer's interactive controls for it are in [CHARACTER_PIPELINE.md](CHARACTER_PIPELINE.md#section-clip-plane-controls).

**Plane representation.** `GlRenderer.ClipPlane` is a `Vector4` packed as `(normal.xyz, -distance)`; a vertex survives when `dot(worldPos, plane) >= 0`. The disabled value is `(0,0,0,1)` — a constant `+1` for every vertex, so the uniform is inert even if a stray draw ever left `GL_CLIP_DISTANCE0` enabled. `GlRenderer.ClipEnabled` is the actual gate.


**Hardware clipping, not `discard`.** The cut is `gl_ClipDistance[0]`, written by **four** vertex shaders — [basic.vert](../src/SkyRender/Shaders/basic.vert), [wireframe.vert](../src/SkyRender/Shaders/wireframe.vert), [depth_only.vert](../src/SkyRender/Shaders/depth_only.vert), [shadow_depth.vert](../src/SkyRender/Shaders/shadow_depth.vert) — with `GL_CLIP_DISTANCE0` scoped around each of the four geometry passes by `BeginClipPass` / `EndClipPass`. A fragment `discard` in `basic.frag` would have been a one-line change but is *wrong here*: the SSAO depth/normal prepass and the shadow depth pass are separate renders of the same geometry, so clipped-away geometry would keep casting shadow onto — and keep ambient-occluding — the surfaces the user just uncovered. `shadow_depth.vert` clips in **world** space even though its `gl_Position` is in the light's clip space, precisely so both passes drop the same triangles.

> **The scoping is load-bearing, not defensive.** While `GL_CLIP_DISTANCE0` is enabled, a vertex shader that leaves `gl_ClipDistance[0]` *unwritten* yields an **undefined** clip result — not an unclipped one. `debug.vert`, `vertexcolor_unlit.vert` and `fullscreen.vert` never write it, so the enable must be cleared before the bloom/SSAO full-screen quads and before the overlay pass. Every `BeginClipPass` scope is verified to contain no early `return`.

**Overlays are never clipped.** Vertex-pick markers, measurement lines, region wireframes and light arrows keep drawing on the hidden side, for the same reason they already draw with depth test off: a pick you made on a surface you have since cut away must stay visible.

**Visualization.** With the **V** toggle on (default), `DrawClipPlaneVisual` draws the plane as a translucent quad with a 6×6 grid and a bright border, centred on the camera target's perpendicular projection onto the plane and sized from camera distance × FOV so it always spans the viewport (no scene bounds needed). The fill is depth-tested but writes **no** depth — otherwise it would z-reject the marker and measurement gizmos drawn after it — while border and grid draw with depth test off so the plane stays locatable behind the model. This required adding a `u_alpha` uniform to `debug.frag`; it defaults to `0` when unset, so **every** debug-shader caller now pushes it explicitly.


### Per-slot texture toggles

A host can switch texture slots on and off per shape (the character viewer does it from its right-click menu). Each `GlMesh` carries nine flags — `DiffuseEnabled`, `NormalEnabled`, `SkinEnabled`, `SpecularEnabled`, `FaceTintEnabled`, `DetailEnabled`, `EnvMapEnabled`, `EmissiveEnabled`, `TintColorEnabled`, all default `true` — pushed per draw as the `u_enable*` uniforms. `basic.frag` ANDs each with the stage's own `has_*` gate, so "off" skips that stage: Diffuse off substitutes flat `vec4(0.8, 0.8, 0.8, 1.0)`, Normal off falls back to the interpolated vertex normal (and loses the normal-alpha specular mask), Skin off drops SSS, and so on. Lighting, shadows and SSAO are geometry-driven and stay on, which is the point — with textures off only the mesh's own relief shows. `TextureSlots` ([Gl/TextureSlots.cs](../src/SkyRender/Gl/TextureSlots.cs)) names the nine as flags; `GlMeshTextureSlotExtensions` reads and writes them, and `PresentTextureSlots` is the set a mesh actually has (a host's slot menu should offer only those). SkyRender.Character adds a lock that keeps the choice across loads ([CHARACTER_PIPELINE.md](CHARACTER_PIPELINE.md#texture-visibility-lock)).

> **Diffuse off writes alpha 1.0.** An alpha-tested shape (hair cards, brows, lashes) therefore draws as solid grey sheets in the main pass, while `depth_only.frag` / `shadow_depth.frag` still cut out from the diffuse alpha regardless of the toggle — so their shadows and ambient occlusion keep the cut-out shape.


### Effect shaders (BSEffectShaderProperty)

[basic.frag](../src/SkyRender/Shaders/basic.frag) (`u_isEffect`, `shadeEffect`). Fire, smoke, mist,
light shafts and magic glows sit on a `BSEffectShaderProperty`, not a lighting shader. A mesh with
`GlMesh.IsEffectShader` set skips every stage below and takes this path end to end. Nothing turns it
on by itself, so characters and other existing renders are unchanged:

- `NifMaterialBinder.Bind` uses it when `MaterialBindOptions.ShadeEffectShaders` is set (off by
  default, which binds effect shapes through the lighting-shader rules as before).
- `NifMaterialBinder.ApplyEffectMaterial(mesh, built, sourceTexture, hasSource, greyscaleTexture)`
  sets it up for a host with its own texture cache.

The math is the static path of the engine's effect pixel shader, ported from Community Shaders'
`Effect.hlsl` with its linear-lighting option off. Static means: not animated, not membrane, not
particles.

```
base    = sourceTexture (slot 0); alpha forced to 1 under greyscale-to-alpha
mul     = baseColor * vertexColor                     (RGBA)
base.a *= falloff                                      (SLSF1_Use_Falloff)
base    = mul * base
base.a *= saturate((sceneDepth - fragDepth) / softFalloffDepth)   (SLSF1_Soft_Effect)
alpha   = greyscale-to-alpha ? palette(source.a, base.a).a : base.a
rgb     = greyscale-to-color ? baseColorScale * palette(source.g, mul.r).rgb : base.rgb
rgb     = mix(rgb, light * rgb, lightingInfluence)    (only with SLSF2_Effect_Lighting)
```

- **`baseColorScale` scales only the palette colour.** A fire's orange lives in its greyscale
  palette, not in `baseColor`. The red channel of `baseColor x vertex colour` picks the palette
  row, and the source texture's green channel picks the column. Vanilla flames sit at about
  0.2-0.3, the dark-red-to-white fire row of `gradfireexplosion.dds`, whose last rows are blue.
- **Palette sampling** clamps to the edge texels and reads mip 0. A palette is a thin gradient
  strip, and a repeating sampler would blend the far end into a coordinate of exactly 1.
- **Falloff**: `lerp(startOpacity, stopOpacity, smoothstep(saturate((abs(N.V) - startAngle) /
  (stopAngle - startAngle))))`, the angles stored as cosines. The engine evaluates it per vertex;
  here it is per pixel.
- **Soft effects** fade where they meet geometry, using the ambient-occlusion depth prepass, which
  holds opaque geometry only. With ambient occlusion off there is no scene depth, so only the
  engine's discard of near-zero alpha applies and the effect stays hard-edged.
- **Lighting influence.** With `Effect_Lighting` the light is the engine's unshadowed ambient plus
  directional light at the object (`DLightColor`), approximated by the scene's summed lights.
  Without the flag the engine multiplies by a per-object `PropertyColor` constant whose CPU-side
  value is not documented. It is treated as white, so a self-lit flame stays self-lit; vanilla
  flames carry `lightingInfluence` 255 and no `Effect_Lighting`, and burn bright in dark rooms.
- **Billboards.** A shape under an `NiBillboardNode` (`BuiltMesh.BillboardMode`) is turned toward
  the camera by the engine every frame. It is drawn here in its authored pose, double-sided, since
  the engine never culls a billboard. Its falloff is evaluated as if turned: `abs(N.V) = 1`, or the
  view direction's horizontal length for the rotate-about-up modes. Without the double-sided rule,
  vanilla `FireplaceWood01Burning.nif` loses both flame sprites to back-face culling from half the
  room.
- **Texture clamp mode** (0-2) clamps the source coordinates per axis, as the engine's sampler does.
- **Blend, test and depth.** Blending (additive `SRC_ALPHA, ONE` for flames, "over" for smoke)
  comes from the `NiAlphaProperty` like any shape, and the alpha test runs on the final alpha.
  Depth write follows `SLSF2_ZBuffer_Write`, and a cleared `SLSF1_ZBuffer_Test` draws without the
  depth test. Effect shapes cast no shadow and stay out of the SSAO prepass.
- **Output.** The output goes through the same ACES tone map and vignette as Stage 6, without the
  fresnel contour, so a flame's `baseColorScale` of 1.5-10 compresses instead of clipping.
- **Texture units.** The greyscale palette rides texture unit 12, the glow unit, since an effect
  shape never has a glow map.
- **Not modelled:**
  - the `BSEffectShaderProperty*Controller`s that scroll UVs and animate alpha and colour (the
    stored block values are the frame drawn);
  - particle systems, which have no particles at rest, so vanilla `Campfire01Burning.nif` shows
    only its ember glow;
  - membrane, blood and projected-UV variants;
  - fog, point lights, and refraction warps (`Plane03`-style heat planes are lighting-shader
    shapes with a normal map in slot 0).
- `DEBUG_VIZ_EFFECT` (0, off) shows the stage inputs: source, palette colour, final alpha, vertex
  alpha, `abs(N.V)`, falloff, flag colours.

Verified against in-game captures of the vanilla Bannered Mare (Whiterun), kitchen hearth and
central fire pit.

### Stage 1: base color & alpha test

[basic.frag](../src/SkyRender/Shaders/basic.frag) (`use_alpha_test`).

```
baseColor = texture(diffuse, TexCoords);
if has_vertex_colors: baseColor.rgb *= vertexColor.rgb; baseColor.a *= vertexColor.a;
if use_alpha_test && baseColor.a < alpha_threshold: discard;
```

**Why vertex colors here, not later**: vertex color in Skyrim NIFs is part of the diffuse data path — it's premultiplied into the albedo before any other shading. NifSkope's `sk_default.frag` does the same (`albedo = baseMap.rgb * C.rgb`). Outfit Studio also does the same.

**Alternative considered**: gate vertex-color application on a debug toggle. Rejected because vertex colors *are* the surface color when present (typical case: brows, where vertex color modulates a generic brow texture).

### Stage 1b: tint operations

[basic.frag](../src/SkyRender/Shaders/basic.frag) (`has_greyscale_to_palette`). Three mutually-exclusive paths plus a fourth that runs in addition:

```
if has_greyscale_to_palette:     // hair, BSLSP_HAIRTINT shader type
    baseColor.rgb = baseColor.rrr * tint_color * greyscaleToPaletteScale;
else if has_tint_color:           // body, BSLSP_SKINTINT shader type
    baseColor.rgb *= tint_color;

if has_detail_map:                // face shapes with slot 3 populated
    baseColor.rgb = overlayBlend(baseColor.rgb, detailSample);

if has_face_tint_map:             // primary head shape with slot 6
    if MSN: baseColor.rgb = overlayBlend(baseColor.rgb, tintSample);
    else:   baseColor.rgb *= tintSample;
```

#### Greyscale-to-palette (hair tint)

Only on shapes with `bslspShaderType == BSLSP_HAIRTINT`. The diffuse texture is monochrome — only the red channel is meaningful — and gets multiplied by `tint_color × greyscaleToPaletteScale`. The host populates `tint_color` from the NIF's baked `BSLSP.hairTintColor`; the NPC record's `HairColor` FormLink is used only as a fallback when the shape has no baked tint. (Engine-verified empirically 2026-07: editing the NIF's tint recolors the hair in-game with no plugin edit, so the baked value wins at runtime.)

A host can supply its own tint for a shape (for example SkyRender.Character's worn hair-slot rule, [CHARACTER_PIPELINE.md](CHARACTER_PIPELINE.md#npc-texture-inputs)).

**Alternative**: simple RGB multiply (`baseColor.rgb *= tint_color`). Used when SLSF1_Greyscale_To_PaletteColor is **not** set but the shape is still a hair-tint shader. The choice happens host-side in `ApplyTexturesToGlMesh` ([VM_CharacterViewer.cs](../src/SkyRender.Character/ViewModels/VM_CharacterViewer.cs)).

#### Skin tint (body)

`baseColor.rgb *= tint_color`. The `tint_color` uniform comes from `MaterialBindOptions.SkinTintColor` (for an NPC, its QNAM `TextureLighting` color) when `built.ShaderType == 5`. This is what gives a vanilla generic male body its per-NPC warm-tan instead of looking the same on every character.

The NIF's `BSLSP.skinTintColor` is **not** read for this — it's reliably `(1,1,1)` on every body NIF in the wild. The engine doesn't use the NIF field; it uses the NPC record. We follow the engine.

##### Skin-tint operator selector (debug)

The `tint_color` blend has a runtime-selectable operator behind it (`u_skinTintOperator`, surfaced in the render panel). 0 is the production multiply; the rest exist to triangulate which operator the engine actually uses for face-vs-body skin-tone consistency, since multiply produces a visible seam on some replacer NPCs:

| Op | Formula | Notes |
|---|---|---|
| 0 | `albedo * tint` | Production default. |
| 1 | `overlay(albedo, tint)` | Photoshop-style. Brightens 2× where albedo < 0.5 (most skin), darkens above. |
| 2 | `pow(pow(albedo, 2.2) * pow(tint, 2.2), 1/2.2)` | Linear-space multiply — model the tint as happening in linear lighting space. |
| 3 | `pow(albedo, 1/tint)` | Gamma-aware. Reduces dark-region darkening for low tint values. |
| 4 | `mix(albedo, albedo*tint, u_skinTintLerpStrength)` | User-controlled lerp. |
| 5 | `mix(albedo, albedo*tint, skin_tint_alpha)` | Lerp weighted by NIF's per-shape `BSLSP.skinTintAlpha`. Always 0 in vanilla / replacer NIFs we've sampled — effectively reproduces "no tint." Useful as a control point. |
| 6 | `pegtop(albedo, tint) * (1.012, 0.996, 1.012)` | Engine-faithful per Community Shaders' `GetFacegenRGBTintBaseColor`. Pegtop soft-light is `b² + 2·t·b·(1-b)`; the trailing constant is the engine's small per-channel color-shift. |

The Pegtop operator (op 6) is the engine-correct one; the others remain selectable for cross-checking against alternate hypotheses. `u_skinTintApplyToFace` additionally extends the operator to ShaderType==4 face shapes (production: only ShaderType==5 body shapes participate); the `is_face_shape` per-mesh uniform gates the branch so the toggle flips without scene reload. `u_vertexColorMode` is an unrelated debug override on the Stage-1 vertex-color multiply (auto / force-on / force-off) that's tucked into the same render-panel row because it's used during the same diagnostic work.

**Hair-tint isolation.** Hair-tint shapes (BSLSP_HAIRTINT, ShaderType 6) without the greyscale-to-palette flag also flow through the `has_tint_color` branch — the diffuse is multiplied by the hair tint (baked NIF `hairTintColor`, record `HairColor` fallback). They must NOT participate in the SkinTint operator experiments, though: the body Pegtop path's `(1.012, 0.996, 1.012)` color-shift constant has no business being on hair, and the soft-light / gamma / lerp ops would shift hair color away from the simple engine RGB multiply that's the engine-correct hair behavior. The per-mesh `is_hair_tint` uniform forces `op = 0` (multiply) regardless of the host's selection, isolating the operator experiments to body / face skin tinting.

##### Engine-source cross-check (verified against CS)

Op 6, FaceTint mode 4, and the engine-style detail-map blend are verified byte-for-byte against the Community Shaders source. Three checkpoints, all matching:

- **Tint formula** — `Lighting.hlsl: GetFacegenRGBTintBaseColor` is `K · (b² + 2tb(1-b))` with `K = (1.01171875, 0.99609375, 1.01171875)`. Op 6 implements this identically.

- **Tint source** — `Actor::UpdateSkinColor` reads `npc->bodyTintColor` (the QNAM byte triple at offset 246 on TESNPC), divides each byte by 255, and calls `UpdateBodyTint(NiColor)`, which traverses the scene graph and writes the float color straight into `BSLightingShaderMaterialFacegenTint::tintColor` — the source of the HLSL `TintColor` uniform. A host should pass the QNAM bytes divided by 255 (`ResolvedNpcMeshPaths.TextureLightingColor`), the same conversion, so the source matches the engine byte-for-byte.

- **Color space** — `Color::Diffuse` (in CS's `Color.hlsli`) is identity in non-PBR mode and `linear → gamma` in PBR mode. The PBR branch existing only to deliver gamma values to the rest of the pipeline means the vanilla (non-PBR) pipeline operates on gamma-encoded values throughout — texture samplers do not auto-decode, lighting math runs in gamma space, ACES tonemap + framebuffer-srgb encoding at the end. The renderer matches: `PixelInternalFormat.Rgba8` upload (no auto-decode) with `GL_FRAMEBUFFER_SRGB` at output. Confirmed by inverting this experimentally — uploading diffuses as `PixelInternalFormat.Srgb8Alpha8` for sampler-side auto-decode produced uniform red-shift across all NPCs because the downstream lighting + tonemap stack was tuned for gamma-space inputs. Reverted; gamma-space upload is engine-correct.

#### Detail map (face)

[basic.frag](../src/SkyRender/Shaders/basic.frag) (`has_detail_map`). When SLSF1_Facegen_Detail_Map is set and slot 3 has a texture, `overlayBlend(baseColor, detailSample)` adds detail (skin pores, stubble) on top of the diffuse before the FaceTint pass.

**Missing detail-map file → slot treated as empty.** If slot 3 names a file that doesn't resolve anywhere in the load order, the mesh gets `HasDetailMap=false` instead of the 1×1 white fallback texture (`ApplyTexturesToGlMesh`). The engine ignores a missing detail map; binding white instead saturates the face under *both* blend modes (legacy overlay with a white top layer → white; engine-style → ×~4 multiply). Real-world trigger: female facegen NIFs baking `textures\actors\character\female\blankdetailmap.dds`, which vanilla only ships in the `male` directory. The broken path is still recorded as a texture source so the hover tooltip shows it as "(not found)", and (for face shapes) the slot then qualifies for the BlankDetailmap debug fallback below.

#### FaceTint (face)

[basic.frag](../src/SkyRender/Shaders/basic.frag) (`has_face_tint_map`). The interesting one. Two blend modes are gated on `is_model_space` (the SLSF1_Model_Space_Normals flag):

- **MSN faces → overlay blend.** Matches NifSkope's `sk_msn.frag`. Vanilla FaceGen always bakes MSN, and so do the common body replacers.
- **Non-MSN faces → multiply blend.** Matches the apparent in-game behavior for tangent-space face replacers that ship tangent-space faces. NifSkope's `sk_default.frag` (the non-MSN counterpart) has no FaceTint code at all; multiply is the closest practical fit that still preserves per-pixel features (freckles, lipstick) which a "no-op" path would lose.

This is the only place the renderer makes a decision based on MSN beyond the normal-map sampling itself. The short version of the empirical derivation is that overlay's `2·base·tint` branch (taken for `base < 0.5`, which describes most skin diffuses) brightens the face by 2× when the FaceTint texture's mean RGB > 0.5 — which is essentially every FaceTint. For MSN faces this matches the engine; for non-MSN faces the engine doesn't apply FaceTint at all, leaving overlay 2× off.

`FACE_TINT_MODE` const at the top of [basic.frag](../src/SkyRender/Shaders/basic.frag) forces overlay (`1`) or multiply (`2`) for cross-checking.

##### FaceTint runtime mode + Pegtop + multiply-on-empty

The shader-side `FACE_TINT_MODE` const has been promoted to a `u_faceTintMode` uniform so the host can flip operators at runtime to triangulate the engine's actual FaceTint blend without scene reload. Five modes are now available:

| Mode | Behavior |
|---|---|
| 0 | Auto (MSN → overlay, non-MSN → multiply) |
| 1 | Always overlay |
| 2 | Always multiply |
| 3 | Always skip (use for vanilla children whose multiply renders too brown) |
| 4 | Pegtop soft-light (engine-faithful per Community Shaders' `GetFacegenBaseColor`) |

The Pegtop helper (added in the [skin-tint operator selector](#skin-tint-operator-selector-debug) commit) is reused here. Mode 4 is the engine-correct one; the others remain selectable for cross-checking.

**Multiply-on-empty-slot-3 override.** A second toggle (`u_faceTintMultiplyOnEmptyDetail`) forces multiply for face shapes (`ShaderType==4`) whose NIF has `SLSF1_Facegen_Detail_Map` set but whose `BSShaderTextureSet` slot 3 is empty. Empirically resolves the seam on modder faces that omit slot 3 (Brynjolf, Aia Arria, Angeline Morrard from Ordinary People) without affecting NPCs whose slot 3 is populated. The per-mesh `is_face_empty_detail` uniform is set in `ApplyTexturesToGlMesh` based on the NIF flag + slot inspection; the shader-side toggle decides whether to act on it. Useful as a stopgap pending the engine-style detail-map blend, which addresses the same shapes from the detail-map side.

##### Engine-style detail-map blend

The legacy detail-map path (described under [Detail map (face)](#detail-map-face) above) ran *before* the FaceTint blend as a Photoshop overlay. The Skyrim engine's actual handling, per Community Shaders' `Lighting.hlsl` replacement shader (`GetFacegenBaseColor`), is different on two fronts:

- **Order**: detail map is applied AFTER the FaceTint blend, not before.
- **Blend**: it's a multiply against a transformed sample, not an overlay against the raw sample.

The transform is:

```
detailColor = 3.984375 * (vec3(1/255, 0, 1/255) + sampledDetail)
postFaceTint *= detailColor
```

The constant `3.984375` is roughly `4 - 1/64`; the `(1/255, 0, 1/255)` offset prevents the green channel from being lifted off zero (so a black-green-zero detail texel multiplies to mid-gray instead of nuking the surface to black). At a "neutral" detail sample of `(0.25, 0.5, 0.25)` the result is approximately `(1.0, 2.0, 1.0)` — a 2× green boost that the engine then absorbs through subsequent grading. With Bethesda's BlankDetailmap.dds (mid-gray), the multiply lands close to identity; with a populated slot 3 (freckles, dirt, complexion), it brightens.

The `u_detailMapEngineStyle` uniform selects between the legacy overlay path and the engine-style multiply path; both are gated on the same `has_detail_map && u_enableDetail` test, so toggling doesn't change which shapes participate, only how they composite. Pairs with `FaceTintMode==4` (Pegtop) and `SkinTintOperator==6` (Pegtop with engine constant) to reproduce the engine's full face composition pipeline.

**Defaults: Pegtop face/body + engine-style detail.** As of the same commit that flipped these defaults, new VMs ship with `SkinTintOperator = 6`, `FaceTintMode = 4`, and `UseEngineStyleDetailMap = true` — the engine-faithful trio. Validated against vanilla Addvar, Nordic Faces, Aia, Angeline, Bjartur, and a replacer-provided Lydia (all match in-game / NifSkope) plus Frodnar (matches the engine's own minor seam). Brynjolf still requires `SkinTintApplyToFace` to be enabled — open question, tracked under follow-up investigation. Hosts that want the legacy multiply behavior can flip the operators back from the render-panel.

##### BlankDetailmap fallback (debug)

A separate experimental host-side toggle (`UseBlankDetailFallback`) substitutes `textures\actors\character\male\BlankDetailmap.dds` when slot 3 is empty on a face shape. The substitution happens in `ApplyTexturesToGlMesh` at scene-build time — the toggle therefore fires `ReloadRequested` rather than just pushing a uniform. Tests whether the engine substitutes a similar default at runtime for slot-3-empty faces (Brynjolf, Aia, Angeline). Mostly redundant with the Pegtop + engine-style detail combination: for those shapes the multiply with BlankDetailmap (mid-gray) lands close to identity, which is also what skipping the detail step gets you. Kept as a control point because it isolates "did the engine substitute a texture?" from "what blend math?" for future investigation.

### Stage 1c: skin saturation boost

[basic.frag](../src/SkyRender/Shaders/basic.frag) (`u_skinSaturationBoost`). Optional, host-tunable chroma adjustment applied AFTER all Stage-1b tint stages (skin tint, detail, FaceTint) and BEFORE Stage 2 normal calculation. Gated on the per-mesh `is_skin` uniform (true for shader types 4 and 5 — face plus body, hands, feet) and a non-1.0 `u_skinSaturationBoost`:

```glsl
if (is_skin && u_skinSaturationBoost != 1.0) {
    float lum = dot(baseColor.rgb, vec3(0.2126, 0.7152, 0.0722));
    baseColor.rgb = max(mix(vec3(lum), baseColor.rgb, u_skinSaturationBoost), 0.0);
}
```

Same luminance-preserving formulation as `Color::Saturation` in CS's `Color.hlsli`, with a non-negative clamp. Default 1.0 is no-op. Values > 1 boost chroma along the original hue, restoring race-distinguishing skin character that the downstream pipeline tends to compress toward neutral (Imperials look pale, Redguards Mediterranean, Orcs olive at high SSS settings). Values < 1 desaturate skin while leaving hair/eyes untouched.

Applied to the diffuse albedo before lighting, so specular highlights stay achromatic — wet/oily skin highlights consume `specularColor`, not `baseColor`. Hair (BSLSP_HAIRTINT, type 6), eyes (BSLSP_EYE, type 16), and any other non-skin shapes pass through unchanged.

Practical context: at high SSS strength (the host's old default of 2.0, since changed to 0.0), the warm-flesh injection desaturates skin broadly. A boost in the 1.25–1.75 range typically recovers correct appearance for all races simultaneously. After lowering the SSS default the boost remains useful as a per-NPC dial when scene lighting biases skin toward neutral.

### Stage 2: normal calculation

[basic.frag](../src/SkyRender/Shaders/basic.frag) (`is_model_space`). Three paths:

```
if has_normal_map:
    if is_model_space:
        // MSN: sample as model-space, flip Z (NIF Z-up → renderer Y-up), transform to view space
    else if tbnIsValid:
        // TSN: sample, G-flip (DirectX convention), TBN transform to view space
    else:
        // No tangents available, fall back to vertex normal in TBN[2]
else:
    // No normal map, use vertex normal
```

**MSN path**: Bethesda's MSN textures are stored in Y-up local model space (models face +Z). Our renderer is Y-up world space (models face −Z), so the only swizzle needed is a Z flip. There is **no** DirectX G-flip on the MSN path — that's a tangent-space convention only.

**TSN path**: standard tangent-space normal mapping with the DirectX G-flip (`normal_tangentSpace.g *= -1.0`). The TBN matrix is constructed in the vertex shader from the per-vertex tangent/bitangent.

**Alternatives considered**: deriving normals via screen-space derivatives of position (`dFdx/dFdy` cross product). NifSkope's `sk_msn.frag` has commented-out code for this. Rejected because vertex normals are correct on Bethesda meshes (we've verified this via the `DEBUG_VIZ_WORLD_NORMAL` debug branch); face normals would lose smoothing.

### Stage 2b: projected material

[basic.frag](../src/SkyRender/Shaders/basic.frag) (`u_hasProjected`). The engine's single-pass
`Projected_UV` path: the snow, ash or moss a static's material object (MATO) lays over the shape's
own diffuse. NIF data never turns it on; a host sets `GlMesh.HasProjectedMaterial` and its
parameters from scene data, so characters and every other mesh draw exactly as before.

```
w      = dot(N, dir) * vertexAlpha - params.w - params.x * noise(worldXY * params.z)
albedo = mix(base, projectedDiffuse(worldXY * params.z * tiling) * colour, smoothstep(-0.1, 0.1, w))
```

- `N` is the **normal-mapped** world normal from Stage 2, so the material settles into the base
  texture's own relief per pixel and the tiling noise breaks its edge. Lighting keeps the base
  normal: vanilla lerps a projected normal only into the normal buffer later image-space passes
  read, never into the lighting normal.
- `params` is the engine's `ProjectedUVParams`. For a single-pass material the engine builds it
  from the material's falloff scale and bias, its noise UV scale and the placing record's max angle
  as `((1 - cos) * scale, -, 1 / noiseUVScale, (1 - cos) * bias + cos)`;
  `GlMesh.ProjectedParamsFor` computes exactly that, and a host that does not want the max-angle
  fold passes 90 degrees.
- Noise and diffuse coordinates are planar in world X/Y (Skyrim's horizontal plane); `dir` is
  world up. `tiling` is the display setting `fProjectedUVDiffuseNormalTilingScale`.
- `ProjectedDiffuseTexture == 0` is the engine's flat-colour path
  (`bEnableProjecteUVDiffuseNormals` off): a hard edge in the plain colour.
- The vertex colour multiplies the result afterwards, as it does the base albedo.
- The two textures (in vanilla `textures\effects\projectednoise.dds` and `projecteddiffuse.dds`,
  bound for every projected draw whatever the material) ride texture units 14 and 15, the first two
  terrain layer-normal units, which no non-terrain draw samples, so the shader's sampler count does
  not grow. A context with fewer than 16 units skips the stage.
- `DEBUG_VIZ_PROJECTED_WEIGHT` (off) outputs the coverage weight as grey.

Sources: the decompiled vanilla SE lighting shader (Skyrim-SE-Shader-Tools, `ProjectedUV.hlsl`),
Community Shaders' `Lighting.hlsl` (same weight; it adds triplanar sampling and normal blending,
not modelled here), and a reverse-engineered account of how the engine fills the constants from a
material object. Coverage was compared against in-game captures of snowy mountain statics.

### Stage 3: dynamic lighting

[basic.frag](../src/SkyRender/Shaders/basic.frag) (`MAX_LIGHTS`). The largest stage. Up to `MAX_LIGHTS = 5` lights are accumulated in a single loop:

```
for each light:
    if type == 1 (ambient):  finalColor += lightColor * baseColor.rgb * ao;
    if type == 2 (directional):
        compute diffuse, specular, backlight, rimlight, subsurface, eye-catchlight;
        accumulate into finalColor
```

Each directional light contributes:

#### Diffuse

Lambert (`max(NdotL, 0)`), upgraded to a "wrap" lighting term (`NdotL * 0.5 + 0.5`) when SLSF2_Soft_Lighting is set. Wrap lighting is the cheapest skin SSS approximation — it lets light bleed slightly past the terminator into the shadow side, removing the harsh terminator that pure Lambert produces on faces.

**Alternative**: half-lambert (`(NdotL * 0.5 + 0.5)^2`). Produces a similar effect but with a sharper midtone falloff. Rejected because Bethesda's actual `lightingEffect1` value (read into our `subsurfaceRolloff` uniform) drives a parameterized wrap, applied separately in the SSS path; doing half-lambert here would double up.

#### Shadows

`sampleShadowPCF` ([basic.frag](../src/SkyRender/Shaders/basic.frag)). 3×3 PCF kernel on top of GPU `sampler2DShadow` bilinear PCF (= 36 effective samples). Slope-scale bias to avoid acne at grazing angles. **Only the key light (index 1)** casts shadows — fill and rim lights are arranged to bounce or wrap, so shadowing them produces double-darkening in occluded regions.

**Not yet implemented: cascaded shadow maps (CSM).** A single orthographic shadow map is fitted to `ShadowBounds`. That is plenty for one model or a room, but over a large exterior the map's texels spread thin and shadow detail drops; cascades would fix that.

#### Specular

Blinn-Phong with the BSLSP's `specularColor`, `specularStrength`, `glossiness`. Modulated by slot 7's `.r` channel when present (the specular mask), or 1.0 (full strength) when absent.

`shadow` factor is applied so specular highlights vanish in shadow correctly.

**Alternative**: GGX microfacet BRDF. Not used: the BSLSP data is authored for Blinn-Phong (`glossiness` 30–80 for skin, 200+ for eyes), which matches the engine's look. GGX would add roughness-aware shoulders that aren't authored into the data.

#### Hair backlight

When SLSF1_Hair_Soft_Lighting is set: `pow(1 - VdotN, rimlightPower) * u_backlightColor * lightColor * NdotL * baseColor`. This is the classic hair "translucent edge" effect — visible only at silhouette angles, gated on the lit side via `NdotL`.

`u_backlightColor` is a host-side uniform, default warm-amber, dialed by the user in the lighting preset.

#### Rim lighting (skin)

When SLSF2_Rim_Lighting is set: `pow(1 - VdotN, rimlightPower) * lightColor * baseColor`. Different from hair backlight in that it doesn't gate on `NdotL` — rim shows on silhouette regardless of lit side. Used for skin to suggest the slight backscatter at ear/nose silhouettes.

#### Subsurface scattering

[basic.frag](../src/SkyRender/Shaders/basic.frag) (`subsurfaceRolloff`). Two terms:

- **Forward scatter (wrap-extension delta)**: `max(wrap - lambert, 0)` where `wrap = (NdotL + R) / (1 + R)`, `lambert = max(NdotL, 0)`, and R = `subsurfaceRolloff` (BSLSP's `lightingEffect1` per the Bethesda spec). At R=0 the wrap collapses onto Lambert and the delta is zero everywhere. At R>0 the delta is zero on the fully-lit hemisphere (`wrap == lambert`), positive only at/past the terminator, and peaks at NdotL = 0 with magnitude `R/(1+R)`. This is the warm bleed past the standard terminator that physical SSS produces.
- **Back scatter / transmission**: `pow(max(-NdotL, 0), 3)` — bright where the light is *behind* the surface relative to the viewer. Tightened by the `pow 3` so only thin backlit edges glow (ears, nostrils, lip rims).

The SSS color is `mix(vec3(1.0, 0.35, 0.25), baseColor.rgb, 0.4)` — 60% warm flesh tint, 40% the surface color. The 40% bias toward `baseColor` is so dark-skinned NPCs don't get unrealistically bright-red SSS while still reading as warm.

**Why the forward term is a delta, not the full wrap.** An earlier formulation used `forward = sss_color * wrap` directly, which is large (≈1) on the fully-lit side. That added a fixed-hue warm contribution on top of the standard diffuse, visibly desaturating skin away from its surface tone — Imperials read pale, Redguards Mediterranean, green Orcs olive/yellow. The delta formulation removes that artifact: lit pixels get zero SSS contribution and keep their surface hue exactly, while the terminator and shadow-side contributions (where physical SSS actually does warm light) are preserved. Conceptually this is what pre-integrated SSS via a curvature LUT ([Penner 2011](https://advances.realtimerendering.com/s2011/Penner%20-%20Pre-Integrated%20Skin%20Rendering%20%28Siggraph%202011%20Advances%29.pptx)) produces on average — only the terminator transition gets warmth.

The mask comes from slot 2 (`_sk.dds` for vanilla; the `.r` channel). Vanilla bodies ship with a 4×4 black `_sk.dds` (`mean RGB = (0,0,0)` per the diagnostic dumper), which effectively disables SSS — the engine's same observation made via the empty-mask convention. The body replacers checked so far follow the same convention.

`u_subsurfaceStrength` is a host-side global multiplier (0 disables, 1 is honest source-value SSS, >1 boosts). With the delta formulation the lit side is hue-stable at any strength, so the multiplier can sit at 1.0 by default without skin-tone drift; higher values just deepen the terminator bleed.

**SSS is added separately, not multiplied by `baseColor`** — `sss_color` already mixes in the surface color, so a final multiply would double-tint and look muddy.

#### Eye catchlight

When `is_eye` (BSLSP_EYE shader type, ID 16) and the light is the key light: a tight Blinn-Phong spot at glossiness 256 added *on top of* `baseColor`-multiplied lighting: the "wet eye" sparkle of a close-up. A render toggle, on by default for NPC renders.

**Why on top of baseColor**: a real eye catchlight is the studio key reflecting off the wet cornea, not tinted by the iris pigment. Multiplying by iris color (`baseColor`) would make the catchlight green on green eyes, etc. We want it to stay white-on-iris.

### Stage 4: environment mapping

[basic.frag](../src/SkyRender/Shaders/basic.frag) (`texture_envmap`). When SLSF1_Environment_Mapping or SLSF1_Eye_Environment_Mapping is set and slot 4 has a texture:

```glsl
viewDirWorld = normalize(u_cameraPos - v_worldPos);
reflectWorld = reflect(-viewDirWorld, normalize(v_worldNormal));

if (is_env_map_2d) {
    // Legacy spherical-2D fallback for mod-shipped panoramic envmaps
    float m = 2.0 * sqrt(rx*rx + ry*ry + (rz+1)*(rz+1));
    envColor = texture(texture_envmap_2d, vec2(rx/m + 0.5, ry/m + 0.5)).rgb;
} else {
    // Engine-faithful cubemap path (vanilla DDS cubemaps)
    envColor = texture(texture_envmap, reflectWorld).rgb;
}

envMask = has_env_mask ? texture(envmask, TexCoords).r : 1.0;
finalColor += envColor * envMask * scale;
```

`scale` arrives pre-selected from the host: GlRenderer sends the NIF's `eyeCubemapScale` for BSLSP_EYE (shader type 16) shapes and `envMapScale` for everything else, matching the engine. The selection is deliberately keyed on shader type rather than `is_eye` -- semantic eyes authored as BSLSP_ENVMAP (classified `IsEye` via host head-part data) take their authored `envMapScale` in-game.

**Cubemap loading.** Vanilla Skyrim envmaps in slot 4 are DDS cubemaps (`Caps2 & 0xFE00 == 0xFE00` → all six face flags set). Pfim 0.11.4 reads only the first face of a multi-face DDS, so [`AssetCache.DecodeDdsCubemap`](../src/SkyRender/Assets/AssetCache.cs) parses the 124-byte DDS_HEADER itself, slices the payload into six equal face buffers (DDS spec stores faces in `+X, -X, +Y, -Y, +Z, -Z` order, sequentially after the header), and feeds each face through Pfim as a synthesized "single-face" DDS stream — same header with the cubemap bits cleared from `Caps2`, prefixed to that face's bytes. This decouples the cubemap-detection step from Pfim's lack of multi-face support without requiring a different DDS library.

[`GlTextureManager.LoadEnvMap()`](../src/SkyRender/Gl/GlTextureManager.cs) returns `(handle, isCube)`. If the file is a complete cubemap, `UploadCubemap` issues six `glTexImage2D` calls onto `GL_TEXTURE_CUBE_MAP_POSITIVE_X..NEGATIVE_Z` with `GL_CLAMP_TO_EDGE` on all three axes (necessary for cubemap seam continuity; `GL_REPEAT` would produce visible joins). If the file isn't a cubemap (mod-shipped panoramic 2D), we fall back to the legacy 2D path and the host sets `glMesh.IsEnvMap2D = true` so the shader takes the spherical-UV branch.

**Reflection vector coordinate space.** Computed in **world space** using `u_cameraPos` (per-frame, from `OrbitCamera.GetEyePosition()`) and `v_worldNormal` (vertex/geometry normal, not the per-pixel bumped normal). This matches Community Shaders' `Lighting.hlsl` envmap path — bumped reflections would require transforming the per-pixel `normal_viewSpace` back to world via `inverse(u_view)` and aren't part of the engine-faithful pipeline.

**Why the geometry normal, not the bumped one.** Cubemap reflections track macro shape; the eye is a sphere, the body is a body, regardless of fine surface detail in the normal map. Sampling the cubemap with the bumped normal would make per-texel reflections that the eye reads as noisy texture rather than reflective surface. CS, NifSkope, and Outfit Studio all use the geometry normal for envmap.

**Driver completeness.** Texture unit 6 is `samplerCube`; unit 10 is the 2D fallback `sampler2D`. `GlRenderer` always binds *something* to both units per draw call: a real cubemap or the 1×1 black `_defaultBlackCubemap` to unit 6, and either the 2D envmap or texture 0 to unit 10. This avoids "incomplete texture target" warnings on strict drivers when `has_environment_map` is false. `EnableCap.TextureCubeMapSeamless` is enabled at renderer init for consistent edge filtering across vendors.

### Stage 5: emissive

When SLSF1_Own_Emit is set:

```glsl
finalColor += emissiveColor * emissiveMultiple * baseColor.rgb;
```

The multiply by `baseColor.rgb` matches NifSkope's `color = albedo * (diffuse + emissive) + spec` — emissive is bounded by the surface color, so dark regions of the diffuse (e.g., lash hairs that share a mesh with a glowing iris) don't emit. An earlier version of this stage was pure-additive (`finalColor += emissiveColor * emissiveMultiple`), which produced a saturated-yellow stomp across the entire eye-and-lash mesh on shapes like BB's Serana Replacer that ship with strong vampire-eye emissive values like `(0.89, 0.65, 0) × 1.42`.

**Glow maps** (SLSF2_Glow_Map with Own_Emit, non-skin shaders): slot 2 modulates the term per texel, `emissiveColor * emissiveMultiple * glow.rgb`, matching sk_default.frag. Typical authoring is a white emissive colour x 1 with the pattern and colour in the map (enchanted gear, lit windows). `NifMaterialBinder` binds it; a host that sets `GlMesh` up itself sets `GlowTexture` / `HasGlowMap`.

### Stage 6: tone-map, fresnel, vignette

[basic.frag](../src/SkyRender/Shaders/basic.frag) (`u_enableToneMapping`). All folded under `u_enableToneMapping` so the legacy linear pipeline stays bit-for-bit reproducible when the toggle is off.

#### Fresnel contour darkening

```
fresnel = pow(1 - max(NdotV, 0), 4);
finalColor *= mix(1.0, 0.85, fresnel);
```

~15% darkening at silhouette edges. Real photography has this from grazing-angle microfacet shadowing; without it the silhouette reads as flat-shaded. Folded under the tone-map toggle since both are "finishing touches" that ship together.

#### ACES filmic tonemap

```
c = finalColor * 0.6;
c = (c * (2.51c + 0.03)) / (c * (2.43c + 0.59) + 0.14);  // Narkowicz 2015 ACES approx.
c = mix(luminance(c), c, 1.10);  // mild saturation boost
```

ACES filmic compresses HDR highlights, adds a soft toe in shadows, and produces a warm shoulder that reads as photographic rather than flat linear. Pairs with `FRAMEBUFFER_SRGB` on the host so the framebuffer gamma-encodes on write.

**Alternative**: Reinhard, Hable/Uncharted 2 (NifSkope and Outfit Studio both use this). Reinhard crushes highlights too aggressively. Uncharted 2 is reasonable but doesn't have ACES's warm shoulder.

#### Vignette

`smoothstep(u_vignetteRadius, sqrt(2), distFromCenter)` — radial darkening from screen center toward corners. Reads less as a "vignette effect" and more as natural lens falloff. Tunable from 0 (off) to 1 (corners to black) via `u_vignetteIntensity`.

### Final: framebuffer alpha

The fragment shader writes `FragColor = vec4(finalColor, baseColor.a)` — output alpha is just the surface's own alpha, no special-casing.

Earlier versions of this shader had a luma-driven workaround (`outAlpha = min(baseColor.a, lit_luma)` for any ST_EYE shape) to make a separate wet-eye outer cornea (shipped by some face replacers) fade where dim, since hard-coded `GL_SRC_ALPHA / GL_ONE_MINUS_SRC_ALPHA` blending would otherwise paint a solid black void over the iris underneath. That workaround was replaced once the renderer started honoring per-mesh `SrcBlend / DstBlend` from `NiAlphaProperty` (see [Part 1 NiAlphaProperty](#nialphaproperty)) — such a wet-eye shape is authored as additive blend (`SRC_ALPHA / ONE`), which produces the engine-correct "black cornea adds nothing, catchlight adds brightness" behaviour natively. No shader logic needed.

#### Off-screen readback forces output alpha to 1.0

`GameWindowOffscreenRenderer.RenderInternalCore` stamps `alpha = 255` over every pixel of the resolved byte buffer between `ReadPixelsRgba` and PNG encode. The shader still writes its honest per-fragment alpha — this is a CPU-side overwrite of the readback bytes before they reach the PNG encoder.

**Observed:** the shader's per-fragment alpha is `texture_alpha × vertex_alpha` (after the multiply in Stage 1). For most shapes that's 1.0. But for shapes where:

- the diffuse texture has `alpha < 1` somewhere (vanilla Khajiit faces stamp alpha < 1 across the entire face; alpha-tested hair cards have alpha < 1 at strand silhouettes; any cutout geometry), or
- the `SLSF1_Vertex_Alpha` flag is set on the BSLSP and vertex colors carry alpha < 1 (Khajiit faces have a low-alpha ring around the head/body seam, alpha dropping as low as 0.325),

…the framebuffer ends up storing partial alpha in those pixels. `glClear` sets alpha=1.0 across the FBO initially, then shader writes overwrite alpha wherever a fragment is drawn.

**Live preview empirically displays opaque** for the same scene. The exact mechanism producing the opaque display hasn't been traced — candidates include GLWpfControl explicitly normalizing alpha during its present step, WPF's `D3DImage` compositor ignoring source alpha at composition time, or the underlying D3D9 surface format not surfacing a usable alpha channel to WPF. The verified empirical outcome is: per-fragment α values from the shader reach the off-screen pipeline's readback bytes but never surface in the on-screen pathway.

**Off-screen images** end in `glReadPixels` + PNG encode. PNG keeps the alpha channel. Alpha-aware image viewers, including WPF Image controls, honor PNG alpha and composite the image over their panel background. Khajiit faces with α ≈ 0.5 alpha-blend with a host's gray gallery panel at 50% mix, producing the "head darker than body" / "skin swallows the illumination" effect that looks like dimmed shading but is actually alpha bleed-through to the UI panel underneath. This was caught by opening the PNG in IrfanView with transparency-aware display: the whole Khajiit face read as a transparency checkerboard, and human reference NPCs showed transparency around the hair silhouettes (a long-known artifact users had been compensating for with a black background in the gallery preview panel).

The fix is a one-loop CPU pass forcing alpha=255 on every pixel. It works because:

- Offscreen images are rendered against an opaque background (`glClear` with the requested background color, alpha=1.0). Output alpha carries no useful information in the PNG.
- Anti-aliasing at hair / cutout silhouettes already lives in the RGB channel — the MSAA resolve blends edge-fragment samples that passed alpha-test with samples that didn't (which stay at the cleared background color), producing the correct soft RGB blend without needing the PNG alpha channel for edge softness. Stripping alpha doesn't introduce jaggy silhouettes.
- The pre-stamp alpha bytes are display-pipeline noise downstream of a pipeline whose actual color information lives entirely in RGB.

If transparent-background exports are ever wanted, this should become conditional via a request flag rather than unconditional. Currently it's always-on because no caller wants transparent images.

##### Related dead ends

Two earlier attempts went down the gamma rabbit hole because the symptom looked like a brightness asymmetry between the off-screen tile and the live preview. Both were reverted once the alpha-channel write-through was understood:

1. **`Srgb8Alpha8` resolve FBO + `GL_FRAMEBUFFER_SRGB` around the blit.** Produced washed-out renders across all races. The gamma encoding was brightening pixels on top of the underlying alpha-blend symptom but not addressing the actual transparency issue.
2. **CPU-side linear → sRGB IEC 61966-2-1 LUT after `glReadPixels`, gated on `EnableToneMapping`.** Same problem at a different layer — the math was correct in isolation, but stacked on top of the alpha-blend display problem produced "skin lifted to washed-out highlights, then alpha-blended against panel gray, producing pale-but-muddy tones."

In both cases the PNG's RGB was always correct; the dimming was happening at display time via UI panel show-through. No gamma adjustment was needed.

#### History: where this bug was hiding

For a long time the only visible symptom was transparency around hair-card silhouettes — fine cutout edges with alpha < 1 from MSAA sample coverage. Hosts worked around it with a black background panel that masked the hair transparency. The fact that **all** Khajiit faces stored partial alpha across the *entire* face surface was hidden behind a separate symptom: `MaleHeadKhajiit`'s `NiAlphaProperty.AlphaTest=True, threshold=73` caused the shader's `discard` to drop partial-alpha fragments at the head/body seam (where vertex alpha was low enough to drag `tex_α × vert_α` below 0.286), leaving a salt-and-pepper of *fully opaque cleared-background grey* pixels through the face. The face looked "head darker than body" but had alpha=1.0 in the PNG — gallery panels didn't blend through.

Suppressing the alpha-test discard on `ShaderType==4` face shapes (see [Part 1 NiAlphaProperty](#nialphaproperty), specifically the face-shape suppression rationale) made all face fragments render. Their `tex_α × vert_α` then became the per-pixel framebuffer alpha and surfaced in the PNG. That's when the alpha-strip step was needed — the alpha-test suppression and the alpha-strip are two halves of the same fix.

---

## Part 3 — Host overlay pass (markers, lines, surface patches)

Everything in Parts 1–2 renders the *model*. The overlay pass renders host gizmos **on top of** it: marker spheres, line segments, and surface patches. It runs last in `Render()` ([GlRenderer.cs](../src/SkyRender/Gl/GlRenderer.cs)), after the wireframe overlay and the clip-plane visual and before the light gizmos, mostly with **depth test disabled** so the gizmos stay visible through the mesh from any angle.

The renderer owns only the mechanism. A host adds an `IRenderOverlay` to `GlRenderer.Overlays`; each frame its `Draw(GlRenderer, in OverlayContext)` calls the renderer's primitives (`BeginOverlaySurfacePass` / `EndOverlaySurfacePass`, `BeginOverlayLinePass` / `EndOverlayLinePass`, `DrawOverlayMarkers`, `DrawOverlaySolidTriangles`, `DrawOverlayTranslucentTint`, `DrawOverlayVertexColorTriangles`, `DrawOverlayLines`, `SetOverlayShaded`). All positions are in the same **pre-ModelScale local space** as `GlMesh.CpuPositions`, so overlays track the scene through `ModelScale` changes. A host's tools (picking, measuring, annotation, navigation debugging) typically draw their markers, lines and patches as one such overlay.

Hosts that need to read the installed geometry back (picking, measuring) use `GlMesh.CpuPositions` and the mesh index buffers; SkyRender.Character adds per-shape accessors ([CHARACTER_PIPELINE.md](CHARACTER_PIPELINE.md#geometry-accessors)).

### The debug shader + overlay GL state

Most overlay geometry is drawn through one shared VAO/VBO (`_debugVao` / `_debugVbo`, vertex layout = `position(3) + normal(3)` = 6 floats) and one shader pair ([Shaders/debug.vert](../src/SkyRender/Shaders/debug.vert) + [debug.frag](../src/SkyRender/Shaders/debug.frag)). The fragment shader is trivial: a single `u_color` uniform with a `u_shaded` switch — `u_shaded=0` outputs the flat color (lines), `u_shaded=1` applies a two-sided fake-sun lambert so filled/3D geometry (marker spheres, the region solid) reads as shaded shape even at one color.

The one exception is the **vertex-colour triangle** primitive (`DrawOverlayVertexColorTriangles`), which needs a *per-vertex* color (a `u_color` uniform can't express a gradient), so it has its own VAO/VBO (`_vertexColorVao` / `_vertexColorVbo`, layout = `position(3) + normal(3) + color(3)` = 9 floats) and shader pair ([Shaders/vertexcolor_unlit.vert](../src/SkyRender/Shaders/vertexcolor_unlit.vert) + [vertexcolor_unlit.frag](../src/SkyRender/Shaders/vertexcolor_unlit.frag)). `vertexcolor_unlit.frag` is **unlit** — it outputs the interpolated per-vertex color directly, because that color *is* the datum (e.g. a magnitude); applying the debug pass's fake-sun shade would falsify the magnitudes. The normal is still carried in the layout for parity (and so a future lit variant needs no re-plumbing) but is unused by the fragment shader.

Shared state for the whole pass: **depth test off**; markers + solid additionally enable **back-face cull** and `u_shaded=1`; lines use `u_shaded=0` and a per-channel `GL.LineWidth`. Buffers are uploaded per-draw with `BufferUsageHint.DynamicDraw` (the overlay changes every frame the user edits, so there's no value in static buffers).

**Wide-line fallback.** `Initialize` probes once whether the context honors `glLineWidth > 1` (`_wideLinesSupported`). A forward-compatible core context — the offscreen renderer's `GameWindow` reports `CONTEXT_FLAGS = FORWARD_COMPATIBLE`, core profile — rejects it with `GL_INVALID_VALUE` and keeps drawing 1 px lines, which all but vanish in a scaled-down thumbnail. There `DrawOverlayLines` instead expands the segments on the CPU into screen-aligned quads of the same pixel width: both endpoints are projected through `view * projection` to NDC, offset by half the width perpendicular to the segment *in pixel space* (so the width is exact on non-square viewports), and drawn as two triangles with identity `u_view` / `u_projection` and Z pinned to 0 (depth test is off for the pass anyway; Z = 0 keeps the quad inside the clip range). Back-face culling is suspended for the quads, since their winding follows the segment's screen direction. Segments with an endpoint behind the camera are skipped. The live WPF viewer's context accepts wide lines, so it keeps the `GL.LineWidth` path unchanged.

**The overlay draws into whatever framebuffer the passes before it leave bound**, so any post-process that runs ahead of it must hand back the host's scene FBO. Bloom (`RenderBloom`, run just before the overlay pass when `EnableBloom && EnableToneMapping`) captures `DRAW_FRAMEBUFFER_BINDING` as its restore target, and it must do so **before** `EnsureBloomFbos`: (re)creating the bloom FBOs leaves the last one bound, and a capture after that "restores" to a bloom FBO, so the glow composite and the whole overlay pass land off-screen. The live viewer would only hit that on its first frame or a resize, but the offscreen renderer builds a fresh `GlRenderer` per render, so every bloom-enabled offscreen image would lose both its bloom and its overlay.

`debug.frag` also carries a `u_alpha` output-alpha uniform, added for the [section clip plane](#section-clip-plane-clip-f--clip-b)'s translucent fill. It is `1.0` for every overlay primitive except the translucent tint (below) — but an unset GLSL uniform is `0`, i.e. fully transparent, so **every** caller of the debug shader must push it rather than relying on a default. The clip-plane visualization enables blending and clears the depth mask, and restores both.

**Translucent tint (`DrawOverlayTranslucentTint`, depth-tested).** The one overlay primitive drawn with the depth test **on**: a 6-float (position + normal) triangle list, lit like the solid primitive but alpha-blended (depth writes off) with `glBlendFuncSeparate(SRC_ALPHA, ONE_MINUS_SRC_ALPHA, ZERO, ONE)` — the separate alpha factors keep the destination alpha at 1, so an offscreen BGRA readback doesn't come out semi-transparent where the tint sits — plus `glPolygonOffset(-1, -2)` on fill, because a patch lying exactly on the model surface would otherwise z-fight. The result is a surface tint: shaded where the patch is visible, hidden on the far side. This works because the overlay pass draws into the scene FBO whose depth buffer still holds the model (the same reason the wireframe overlay can depth-test). It restores depth-off / opaque / depth-writes-on before returning. One colour, unless a list of vertex ranges is passed, in which case each range is drawn as its own `glDrawArrays` call with its own `u_color` from the same uploaded buffer (only the listed ranges are drawn).

The overlay pass runs with `GL_CLIP_DISTANCE0` **off**, so no overlay is cut by an armed section clip: a marker on a surface the user has cut away stays visible, which is the same reasoning behind the pass's depth test being off.

> **Same shader, two looks.** A "solid object visible through the model" (`DrawOverlaySolidTriangles`) is *not* transparency — it's the lit debug geometry drawn with depth test off, exactly like the marker spheres. A flat single color reads as a 3D solid because `u_shaded=1` varies brightness with the face normal, so it needs no main-shader changes and no alpha/sort-order work.

### Marker cost

Marker spheres are a pre-built 2×-subdivided octahedron (128 tris) scaled by the caller's radius × `ModelScale` and re-centered per marker. Because each sphere re-uploads ~3 KB, dotting a marker at *every* vertex of a dense loop produces an overlapping "beaded cord" and tanks the framerate — draw dense geometry as lines or one triangle list instead (the triangle primitives are one `BufferData` + one `DrawArrays`).

### Overlays in offscreen renders

The offscreen renderer draws through the same `GlRenderer.Render`, so an overlay a scene builder registers renders into the offscreen image too. For NPC renders the hook for that is `NpcRenderRequest.BeforeDraw` ([CHARACTER_PIPELINE.md](CHARACTER_PIPELINE.md#overlays-in-offscreen-npc-renders-beforedraw)).

---

## Part 4 — Comparison with NifSkope and Outfit Studio

Both reference applications ship shader source under `res/shaders/`. NifSkope splits Skyrim shading across `sk_default.frag` (179 lines) and `sk_msn.frag` (194 lines); Outfit Studio uses a single `default.frag` (320 lines).

### High-level comparison

| Feature | This renderer | NifSkope | Outfit Studio |
|---|---|---|---|
| Multi-mesh scene | ✓ | ✗ (one shape at a time) | ✓ |
| MSN vs TSN normal maps | ✓ both | ✓ both (separate shaders) | ✓ both (one shader) |
| FaceTint (slot 6) blend | overlay (MSN) / multiply (non-MSN) | overlay+double for MSN; not applied for non-MSN | not applied |
| Detail map (slot 3) | overlay | overlay (MSN only, then doubles albedo) | not applied |
| Tint color (slot N/A, host uniform) | ✓ for skin/hair | ✓ for skin/hair | ✗ |
| SLSF2_Soft_Lighting wrap | NdotL · 0.5 + 0.5 | wrap formula via `lightingEffect1` mask | wrap formula via `softlighting` mask |
| Hair backlight (SLSF1_Hair_Soft_Lighting) | rim·diffuse·baseColor | rim only (no NdotL gate) | not applied |
| Rim light (SLSF2_Rim_Lighting) | rim·baseColor | rim·smoothstep(L·V) | code present but commented out |
| Subsurface scattering | wrap + back-scatter, slot 2 mask | not applied | not applied |
| Specular | Blinn-Phong, slot 7 mask | Blinn-Phong, normalMap.a or slot 7 | Blinn-Phong, normalMap.a or slot 7 |
| Environment mapping | cubemap (slot 4), spherical-2D fallback | cubemap (slot 4) | cubemap (slot 4) |
| Eye catchlight | dedicated tight Blinn-Phong spot | not applied | not applied |
| Shadows | PCF directional (key light) | not applied | not applied |
| SSAO | screen-space, separate pass | not applied | not applied |
| Tone-map | ACES filmic (Narkowicz) | Hable/Uncharted 2 | Hable/Uncharted 2 |
| Fresnel silhouette darkening | ✓ | ✗ | ✗ |
| Vignette | ✓ | ✗ | ✗ |

The shorter both reference shaders are reflects their narrower scope: NifSkope previews one mesh at a time without image-quality finishing; Outfit Studio is an editor where the renderer's job is to show the silhouette of garments while you edit them, not to produce a final-render-quality image.

### Where we differ deliberately

- **FaceTint blend on non-MSN faces.** NifSkope's `sk_default.frag` has zero FaceTint code; Outfit Studio doesn't apply FaceTint at all. Neither tool has the multi-mesh seam problem because (a) NifSkope only renders one shape so there's no neck to compare, (b) Outfit Studio doesn't tint anything. We need *something* on non-MSN faces to preserve per-pixel features (freckles, lipstick) and not produce a brightness skew vs the body. Multiply is the closest practical fit. See [`FACE_TINT_MODE` comment in basic.frag](../src/SkyRender/Shaders/basic.frag).

- **Subsurface scattering for skin.** Neither reference applies SSS. Without it, faces look like plastic mannequins under directional lighting. Our wrap term `(NdotL + R) / (1 + R)` is engine-faithful — `R` is the BSLSP `subsurfaceRolloff` field, which the Bethesda docs document as the wrap-lighting parameter and `lightingEffect1` in the binary schema. The forward-scatter contribution we *add* is the **delta over Lambert** (`max(wrap - lambert, 0)`), not the wrap directly, so the lit hemisphere gets zero SSS and only the terminator/shadow side is warm-tinted. The rest of the SSS formulation is our own and was empirically tuned, not derived from observed engine behavior:

  - The warm-flesh `vec3(1.0, 0.35, 0.25)` and the 40% blend toward `baseColor` are hand-picked values that "look right" — Bethesda's engine, per Community Shaders' analytic forward path ([Lighting.hlsl `GetSoftLightMultiplier`](https://github.com/doodlum/skyrim-community-shaders)), reads the per-pixel rim/soft tint from slot 2 (`rimSoftLightingTexture`) rather than a hard-coded constant. Vanilla skin ships an empty 4×4 `_sk.dds`, so on stock content the engine's analytic SSS is effectively zero and CS additionally applies a *screen-space* Burley/SeparableSSS pass (which we can't replicate in a forward renderer without a deferred G-buffer + multi-pass post). For acceptable skin in our forward pipeline, the analytic warm-tint mix is the practical substitute.
  - The transmission term `pow(max(-NdotL, 0), 3) × sss_color × 0.6` is our own approximation; the engine's transmission likely uses a screen-space gaussian blur of the back-lit translucent contribution rather than a pure analytic falloff.
  - The slot-2 `_sk.dds` mask is read as an SSS strength multiplier (`.r` channel), which matches how the engine documentation describes the texture role, but the resulting contribution we add is hand-tuned, not engine-equivalent.

  Net: visually we land in an acceptable place (skin doesn't read as plastic, ear/nostril edges glow when backlit) without the lit-side hue shift the original full-wrap formulation produced. It's not a faithful reproduction of the engine's screen-space SSS. If we ever need a closer engine match — e.g., to match an in-game screenshot pixel-for-pixel — replacing the analytic forward+transmission terms with a pre-integrated LUT or a Burley screen-space pass is the next step.

- **Eye catchlight.** Neither NifSkope nor Outfit Studio renders a dedicated catchlight. We do, because a close-up of a face reads as "alive vs. dead" largely on the wet-eye specular dot, and the studio-style lighting rig has no bright environment for the eye cubemap to reflect.

- **Shadows + SSAO + tone-map.** Final-image finishing. NifSkope and Outfit Studio are utility renderers; SkyRender aims at finished images of models and scenes. SSAO uses a depth + normal prepass ([Shaders/depth_only.vert](../src/SkyRender/Shaders/depth_only.vert), [Shaders/depth_only.frag](../src/SkyRender/Shaders/depth_only.frag)) that writes view-space normals to an RGB8 G-buffer alongside depth; [ssao.frag](../src/SkyRender/Shaders/ssao.frag) samples those normals directly rather than reconstructing them via `cross(dFdx, dFdy)`. Reconstructed normals are constant per triangle (position varies linearly in screen space inside a triangle, so its derivatives are constant), which makes the hemisphere orient off the *flat geometric face normal* and the triangulation pop on smooth surfaces — collarbone, neck, cheek — at any non-trivial radius. The 4×4 box blur in [ssao_blur.frag](../src/SkyRender/Shaders/ssao_blur.frag) cannot hide that because polygon edges are far larger than its footprint and `pow(occlusion, u_intensity)` amplifies the per-triangle contrast before the blur runs. Sampling the interpolated smooth normal from the G-buffer eliminates the artifact at the source.

### Where we differ accidentally / by omission

- **Albedo doubling on MSN face shapes.** NifSkope's `sk_msn.frag` does `albedo += albedo` (multiply by 2) after the detail+tint overlays. We don't do this — and the absence of a face/body seam (with tonemap on *or* off) is empirical evidence the engine doesn't either. The doubling is gated on `hasDetailMask = isST(ST_FaceTint)`, which is false for body shapes; if the engine ran the same code, MSN faces would render 2× brighter than bodies in-game, which they don't. NifSkope likely inherits this from older fixed-function-era vertex lighting where the per-vertex `diffuse + emissive` varying was pre-scaled to a smaller range and the doubling compensated. NifSkope's single-mesh preview hides the asymmetry — they never composite an MSN face against a body in the same frame, so the doubling never produces a visible seam in their tool. Treat this NifSkope code as a NifSkope-specific quirk, not a missing feature on our side.

- **Not yet implemented: wetness, parallax, refraction.** The engine supports all of these via dedicated BSLSP fields and shader flags. They are rare on characters (almost no skin shapes set the parallax flag) but common in scenes: parallax on architecture and dungeon walls, refraction on water and glass. Planned work.

- **Not yet implemented: glow maps (slot 2 emissive modulation).** Some emissive shapes use slot 2 as an emissive mask. We use slot 2 only for SSS on skin shapes; for non-skin emissive shapes we apply `Own_Emit` without modulation. Visually incorrect for, e.g., an enchanted weapon's glow or a lit window.

- **Skin desaturates at high SSS strength.** With `SubsurfaceStrength` at 0 (the current default), skin tones render correctly across all races — Imperials warm, Redguards distinctly dark, Orcs saturated green. Raising it toward the prior default of 2.0 globally desaturates skin (Imperials pale, Redguards Mediterranean, Orcs olive), washing race-distinguishing character toward neutral. The tint pipeline itself is engine-faithful at all SSS strengths — Pegtop + color-shift, QNAM passthrough, and gamma-space rendering all match the CS source byte-for-byte (per the [engine-source cross-check](#engine-source-cross-check-verified-against-cs)) — so the desaturation almost certainly originates in the SSS shader stage or a lighting-interaction issue rather than the tint pipeline. The [skin saturation boost](#stage-1c-skin-saturation-boost) is the user-facing compensation dial; SSS shader audit is a separate follow-up. Causes eliminated during the investigation that traced this back to SSS: NIF `skinTintColor` (always (1,1,1) per render logs), Pegtop math, the color-shift constant, QNAM source/passthrough, color-space mismatch (sRGB-vs-gamma explicitly tested and falsified).

- **TODO — hair-gap fade misses non-HAIRTINT hair; stale eye-AO comment.** The SSAO hair-gap fade in [basic.frag](../src/SkyRender/Shaders/basic.frag) keys on `is_hair_tint` (BSLSP_HAIRTINT, ShaderType 6). Hair authored as DEFAULT/ENVMAP with alpha blending (occasionally seen in pre-colored hair packs that don't want engine tinting) misses the fade, so background-surface AO shades its strands — the same classification-gap class as the FoxGlove ENVMAP eyeballs that motivated `ResolvedNpcMeshPaths.EyeShapeNames`. If a real specimen shows up: add an "is hair geometry" flag fed by host head-part data (HeadPart types Hair / FacialHair / Eyebrows — the EyeShapeNames plumbing is the template) plus `BodyPart == "Hair"` for ARMA wigs, and apply it to the gap fade ONLY. Hair *tinting* must stay keyed on ShaderType 6, which is engine-faithful. While in there, fix the stale comment in basic.frag's `is_eye` AO opt-out block: it still claims lashes pass the prepass gate and write a depth step over the eyeball, but `GlRenderer.RenderDepthPrepass` has since been changed to skip ALL alpha-blend and alpha-test geometry, so that rationale no longer describes current behavior (the opt-out itself remains correct — eyeballs sit in the socket concavity and gain nothing from diffuse AO).

---

## Part 5 — GL context ownership

Every `GLWpfControl` mints its **own private GL context**. GL object names (buffers, textures, VAOs, shader programs) are per-context, and two freshly-created contexts hand out the *same low integers* for the same allocation sequence. So with two viewers alive, viewer B's VBO #7 and viewer A's VBO #7 are different objects that are indistinguishable by name — a GL call issued for B while A's context is current silently reads or destroys A's object.

The simple rule that makes this a non-issue: **defer all GL work to the owning control's `Render` callback**, where that control's context is guaranteed current. The sample app and the character viewer both work this way.

```
off-thread                        UI thread, OUTSIDE render callback      UI thread, INSIDE render callback
──────────                        ────────────────────────────────       ─────────────────────────────────
NIF parse, CPU skinning,     ──▶  dispatch to the UI thread:       ──▶  GlControl.Render
DDS decode, deformation           pending = scene data              ├─ install pending scene     ← all uploads
                                     ^ queues data only, no GL            └─ Renderer.Render(...)       ← all draws
```

A host that issues GL calls from anywhere else (an immediate re-upload, or teardown after its control has stopped rendering) must first make the owning control's context current, on the thread that control runs on. With more than one control alive this is mandatory: issuing a call while another control's context is current silently reads or destroys that control's objects. SkyRender.Character's viewer handles this through a settable marshaller; see [CHARACTER_PIPELINE.md](CHARACTER_PIPELINE.md#concurrent-viewers-the-view-model-contract).

---

## Part 6 — Terrain and water

Landscape and water are not NIF content: they come from LAND, LTEX/TXST and WATR records, which
SkyRender does not read. A host reads them and hands over plain data in the `SkyRender.World`
namespace; SkyRender builds and draws it. Code: [World/](../src/SkyRender/World/).

```
host reads LAND / LTEX / TXST / WATR
      │
      ▼
TerrainCell (heights, VNML normals, VCLR colours, 4 quadrants: base + alpha layers)
WaterSurface (height, footprint, WaterMaterial)
      │
      ▼
[World/WorldMeshBuilder.cs] BuildTerrainQuadrant / BuildWater   (GL-free, testable)
      │
      ▼
[World/GlWorldFactory.cs] Upload* + Apply*Material -> ordinary GlMesh objects
      │
      ▼
GlRenderer: the same passes as every mesh (opaque, shadow, SSAO prepass, wireframe, clip)
```

### Terrain data

- **Grid.** A cell is 4096 units with a 33x33 vertex grid (128 apart); vertex 32 of one cell is vertex 0
  of its neighbour. Arrays are x-fastest (`y * 33 + x`). Positions are Skyrim Z-up, converted to the
  renderer's Y-up with `(x, z, -y)`, the same mapping as NIF geometry.
- **Heights.** `LandHeightDecoder.Decode(offset, gradients)` decodes VHGT: column 0 accumulates down
  the rows from the offset, each row then along x, 8 units per step.
- **Normals and colours.** `TerrainCell.DecodeNormals` reads VNML as three **signed** bytes per vertex
  (OpenMW's ESM4 loader reads them as `int8`); `DecodeColors` reads VCLR as unsigned RGB. Without
  normals each triangle is shaded with its face normal; without colours, white.
- **Layers.** Each quadrant has a base texture and up to 8 alpha layers in record order, each with a
  17x17 opacity grid (VTXT positions 0..288, row by row from the bottom-left).
- **Missing base texture.** A null `TerrainQuadrant.Base` draws the base as white. Choosing the
  fallback is the host's job, because it depends on the game's data: for a quadrant with no BTXT, or
  a BTXT or ATXT whose texture reference is zero, the engine draws its default landscape texture
  (`textures\landscape\dirt02.dds` in Skyrim; UESP's LAND page, and OpenMW's ESM4 loader does the
  same). An ATXT that no VTXT follows paints nothing.
- **Water depth.** `TerrainHeightField.HeightAt` interpolates on the drawn triangles (each square
  splits along its (x,y)-(x+1,y+1) diagonal), so the water's depth tint follows the ground that is
  actually drawn.

### Terrain shading

One mesh per quadrant, three vertices per triangle, with the layer weights in a second vertex buffer
(attribute locations 6-7, two vec4). `basic.frag`'s stage 1 has a terrain branch (`u_isTerrain`) that
blends the layers before everything else, so lighting, shadows, SSAO and post-processing run once on
the result, exactly as for any mesh.

- **Blend (`TerrainBlendMode`).** `Over`, the default: each layer over the result so far, weighted by
  its opacity. It was chosen by comparing against in-game screenshots (Riverwood, vanilla load order,
  free camera at matched poses). It reproduced the game's layer pattern, while a normalised sum and
  the texture-alpha-weighted chain both washed the layers out. `OverWithTextureAlpha` (what separately
  drawn alpha-blended layer sheets produce) and `NormalizedSum` remain for comparison. Community
  Shaders' landscape shader confirms the per-layer arithmetic (`weight × sample`, the texture alpha
  is not a weight). The CPU-side derivation of the engine's per-vertex weights from VTXT opacity is
  not in any shader source; `Over` is the empirical match.
- **Tiling.** `TerrainBuildOptions.TextureRepeat` defaults to 2048 / 12 world units per repeat
  (12 per quadrant), matched against an in-game close-up. The opacity-defined patch outlines
  already matched at that framing, so only the texture-scale detail had to be fitted. This is
  empirical, not a documented engine constant.
- **Normal maps.** The base's normal map is the ordinary `texture_normal`. Each layer's normal map is
  on units 14-21 and blended with the same weights as the albedo; a layer without one contributes a
  flat normal. GL 3.3 guarantees only 16 texture units, so the renderer reads
  `GL_MAX_TEXTURE_IMAGE_UNITS` at start-up and enables as many layer normal maps as fit; most GPUs
  report 32.
- **Vertex colours.** Divided by their brightest channel in the shader before the multiply (as the
  engine does), so VCLR tints without darkening.
- **Texture units.** The 8 layer diffuses reuse the 2D units of samplers a terrain draw never reads
  (skin, specular, face tint, detail, env mask, 2D env, glow) plus unit 13. Two `sampler2D` uniforms
  may share a unit.
- **Missing textures.** A quadrant whose base doesn't resolve draws on the white texture; a layer whose
  texture doesn't resolve is left out (a white layer would be worse than none).

### Water

`WorldMeshBuilder.BuildWater` builds a grid (`WaterMaterial.GridResolution`, default 48 squares per
side). Per vertex it stores the body colour and an opacity, both from the depth to the ground (no
ground counts as deep):

- **Without `DepthFog`**: shallow lerps to deep over `ShallowBand`, and opacity follows the
  shore-alpha ramp (opaque by default).
- **With `DepthFog`** (the water type's above-water fog near / far / amount): shallow lerps to deep
  by depth / far, and opacity is the engine's refraction weight,
  `1 - saturate((far - min(depth, far)) / (far - near)) ^ amount`, so the ground shows through
  shallow water and the surface closes up by the fog's far distance. Community Shaders'
  `Water.hlsl` uses the depth along the view ray and calls the divisor the fog range; SkyRender
  uses the vertical depth per vertex and reads the range as far - near.

The body colour is lit like any surface. The reflection is then mixed over the lit colour
(`basic.vert` works out the fraction from the **live** camera, `basic.frag` mixes it), as
`Water.hlsl` does:

- **Reflection colour.** With `ReflectsSky`, `GlRenderer.SkyColor` (default `ClearColor`): with water
  reflections on, the engine samples its reflection of the sky. Without it, the water type's
  reflection colour times its reflectivity, the engine's term when reflections are off.
- **Sky gradient** (`GlRenderer.SkyGradient`, opt-in). With `ReflectsSky` and a gradient set, the
  reflection is the gradient's colour at the elevation of the reflected view ray, per pixel
  (`basic.frag` `skyGradientAt`): the keys are (elevation in degrees, colour), interpolated linearly
  and held beyond the first and last; up to 16. The engine's water samples its sky reflection along
  `reflect(view, normal)` (`Water.hlsl` `GetWaterSpecularColor`, the reflection cubemap); SkyRender
  reflects off the flat surface (world up), so the elevation is the view ray's depression angle.
  For a Skyrim exterior the sky is the engine's dome (`Sky.hlsl`, the vanilla sky shader's port):
  per vertex of `meshes\sky\atmosphere.nif`, `VParams x (BlendColor0 x c.r + BlendColor1 x c.g +
  BlendColor2 x c.b)` plus the image space's sky scale added after (`PParams.y`). The vertex colour
  is a blend mask, measured on the vanilla mesh: red 1 at the rim up to 2 degrees, green peaking at
  about 18 degrees, blue 1 at the zenith (0.94 at 59, 0.98 at 74). The three stops are the
  weather's horizon, sky-lower and sky-upper colours (NAM0 slots 8, 7, 0; Community Shaders'
  Effects11 names them gradient horizon / middle / top). A host builds the keys from those; the
  renderer applies no colour of its own. Checked against the in-game Riverwood stills (SkyrimClear
  noon): the bare dome is far darker and bluer than the game's sky (an image-space'd 81,175,233 at
  17 degrees against the game's 131,208,255) and its river; the game's sky matches the dome seen
  through the weather's far fog at its maximum, `(1 - max) x dome + max x fogFar`, plus the sky
  scale (140,206,246 at 17 degrees). That fog over the sky is an in-game fit, not sourced (the
  engine's `Atmosphere` object does hold a `BSFogProperty`); it is the host's to apply, in the keys.
- **Fraction.** `lerp(1, fresnel, distanceFactor)`, with Schlick Fresnel (F0 = the water type's
  Fresnel) and `distanceFactor = saturate(lerp(w, 1, (d - 8192) / (noiseFalloff - 8192)))`, where d
  is the distance to the camera. For vanilla rivers (noise falloff about 4000) that is Fresnel
  near the camera and a mirror beyond 8192 units. `w` is a frame flag whose value the shader does
  not show; SkyRender takes 0, which an in-game aerial of Riverwood bears out (the river reads as
  the sky). `NoiseFalloff` 0 keeps the Fresnel blend at every distance.

The first noise texture is bound as the wave normal map.

#### Per-pixel depth (`WaterMaterial.PerPixelDepth`)

Opt-in, with `DepthFog`. The mesh is flagged `GlMesh.WaterSceneDepth`; while such water is drawn,
the renderer runs its depth prepass (the ambient-occlusion one, with alpha-tested shapes kept when
it runs for water alone) and the water fragment reads the opaque scene's depth behind it. This
follows Community Shaders' `Water.hlsl` with its `DEPTH` path:

- **Depth along the view ray.** `d` = distance to the scene behind the pixel minus distance to the
  water fragment: `Water.hlsl` takes the depth buffer's view position, the share of the ray beyond
  the water plane, times the ray's length (`distanceMul.x`); for one flat surface that is the same
  length. Nothing behind (the sky) counts as deep. The vertical depth per vertex is no longer used.
- **Body colour.** `lerp(shallow, deep, saturate(d / far))` (`refractionDiffuseColor`), then lit
  like any surface (the engine splits it into shadowed sun and ambient).
- **Refraction weight.** `refractionMul = 1 - saturate((far - min(d, far)) / (far - near)) ^ amount`.
- **Composition**, the shader's vertex-colour branch:
  `specularFraction = lerp(1, fresnel * refractionMul, distanceFactor)`,
  `preFog = lerp(lerp(bed, body, refractionMul), reflection, specularFraction) + sun`, then the
  distance fog. Here `bed` is the scene already in the framebuffer (the engine's refraction texture,
  without the refraction offset), so the fragment writes the rest as colour and
  `alpha = 1 - (1 - fog)(1 - specularFraction)(1 - refractionMul)`. Far away (`distanceFactor` 0)
  the surface is all reflection and the bed never shows. The shader's other branch instead blends
  the bed back in by `refractionMul` at every distance; the vertex-colour branch is the one an
  in-game aerial bears out (below).
- **Sun specular** (`Water.hlsl` `GetSunColor`; the same form in the vanilla `SIMPLE` technique,
  per a published decompilation): `saturate(dot(reflect(view, normal), sunDir)) ^ power x sun x
  magnitude`, with the water type's sun specular power and magnitude, on the noise normal. The key
  light is the sun. It fades over shallow water by the water type's depth specular-lighting factor
  (`depthControl.w = 1 + factor * (saturate(d / far) - 1)`). The renderer's own Blinn term is off for
  this water. The engine's shadow on the specular is not applied.
- Fresnel stays per vertex on the flat normal (the engine uses the noise normal).
- The image space is applied to the water's colour before the blend, not to the blended result
  (as for every blended surface here).

Checked against in-game stills of a Riverwood river (SkyrimClear noon). From 11,000 units (the
high aerial) the game's river is opaque and uniform (pixel standard deviation 7 to 10 per channel);
the per-vertex path let the bed show in patches (20 to 24), the per-pixel path draws it opaque with
the bed hidden. At 2,760 units the bed shows only along the shore, as in the game. At 550 units the
game's water is nearly opaque with dense sun glints; here the bed still shows more (about a fifth
at the 135-unit view-ray depth there) and the glints are sparser (one noise layer).

Not modelled: reflection buffers (no reflected scenery; the reflection is one sky colour, which is
darker than the game's sky reflection from above), the refraction offset, the refraction texture's
own fog, flow maps, the second and third noise layers.

### Distance fog

`GlRenderer.Fog` (a `DistanceFog`, null by default) fogs every lit surface last, after lighting and
the water reflection, in the engine's form (Community Shaders' `Lighting.hlsl` and `Water.hlsl`):
with d the distance from the camera, `f = min(max, saturate((d - near) / (far - near)) ^ power)`, and
the colour moves to `lerp(nearColor, farColor, f)` by `f`. The shader takes the engine's packed
parameters, `(near / range, 1 / range, power, max)`, which is how the engine fills them from its fog
property's start and end distances, power and clamp (a published reimplementation of the engine's
lighting-shader setup, `TechUpdateFogConstants`, writes `FogParam` this way, and the near colour also
goes to the pixel shader's `FogColor`). A host supplies the colours and distances; for a Skyrim exterior they are the weather's fog near and
far colours and its day or night fog distances, power and max. Effect-shader shapes are not fogged.
Characters are unaffected unless a host sets it.

Checked against the game (Riverwood, SkyrimClear at noon, a high aerial and a low shot toward the
village): with the image space below applied, the haze is no heavier than the game's, and the
game's distance reads paler than ours rather than bluer. The heavy blue-grey haze seen without the
image space came from the surfaces, not the fog: lit by the unscaled sun they sat far below the fog
colour, so the fog's lerp dominated.

### Image space

`GlRenderer.ImageSpace` (an `ImageSpaceFinish`, null by default) finishes every lit surface and the
clear colour with the engine's HDR tone map and cinematic grade, in place of the renderer's ACES
finish (and its sRGB framebuffer encode and bloom). It is the arithmetic of the retail
`ISHDRTonemapBlendCinematic` pixel shader (Reinhard branch), with the constants the engine packs
from the image space's data. With `A` the lit colour and `L = (0.2125, 0.7154, 0.0721)`:

```
Y = max(dot(A, L), 1e-5)             Z = Y * exposureRatio
Q = Z * (1 + k * Z) / (1 + Z)        k = 1 / (White * 1.1)^2   (1.1 = fReinhardWhiteScale)
rgb = A * Q / Y                      K = dot(rgb, L)
g = lerp(K, rgb, Saturation)         g = lerp(g, K * TintColor, TintAmount)
out = saturate(P + Contrast * (Brightness * g - P)) ^ outputExponent
```

The tone curve acts on luminance and returns to RGB by `Q / Y`; the contrast pivot `P` is the first
channel of the engine's adaptation texture. Sources: Community Shaders' `ISHDR.hlsl` as first ported
from the game, and a published audit of the retail shader bytecode and the engine's constant
packing (curve coefficient `(White * fReinhardWhiteScale)^-2`, observed 0.8264 for White 1;
cinematic row `(saturation, 0, contrast, brightness)`; final exponent 1 at `fGamma` 1). That audit
also read a frame at SkyrimClear noon with both adaptation channels equal (exposure ratio 1) at
0.36 to 0.375.

What a host supplies, and what is not modelled:
- **Adapted luminance.** The engine eases both adaptation channels toward the frame's average
  luminance over time (eye adapt speed and strength set the rates), so once settled the exposure
  ratio is 1 and `P` is that average. A single frame has no history, so the host passes `P`
  (`ImageSpaceFinish.AdaptedLuminance`) and, if it wants, `ExposureRatio`.
- **Sunlight scale.** The engine multiplies the sun's colour by the image space's HDR sunlight
  scale when it sets the lighting shader's `DirLightColor` (the same reimplementation reads it from
  the image-space manager's current data). That is a lighting input, so the host scales its key
  light; `ImageSpaceFinish` does not.
- **Not modelled:** bloom (the engine adds a blurred bright pass weighted by
  `saturate(receiveBloomThreshold - Q)`; its producer is not documented), the filmic curve branch,
  the sky scale, and fades.
- The finish runs per fragment, before blending, because the frame is drawn to an 8-bit target;
  the engine grades the blended frame. Opaque surfaces are unaffected by the difference; blended
  effects are approximate. Other shaders (gizmos, unlit vertex colour) are not graded.

Checked against the game (same scenes as the fog, plus a close-up of the ground): the close-up's
sunlit ground matches the game's brightness. Overall the frame is a little darker and more
saturated than the game's (mean luminance 0.47 against 0.59 on the aerial, 0.38 against 0.44 on the
low shot), in line with the missing bloom: in the audited frame bloom added about 0.1 where it was
sampled. Without the image space the weather-lit frame was about 1.6 to 2 times darker than the
game.

### Trees (`SLSF2_Tree_Anim`)

On tree shapes the vertex colour's alpha is the wind-sway weight: the engine's vertex shader scales the
sway by it, and it is not opacity. `NifMaterialBinder` sets `GlMesh.VertexAlphaIsWindWeight` from the
flag, and `basic.frag` then keeps the vertex RGB but leaves the alpha to the texture. Before this,
the alpha test removed most leaf cards and trees drew as bare trunks. Hosts that build meshes
without `NifMaterialBinder` should set the property themselves.

---

## Appendix A — Shader flag inventory table

Bit layout per [NifSkope `glproperty.h` lines 450–517](https://github.com/niftools/nifskope/blob/develop/src/gl/glproperty.h#L450).

**Legend for the "Used" column:**
- ✓ — The shader or host code branches on this bit.
- ~ — We don't read this bit, but achieve the same runtime behavior via a different signal that's reliably paired with the flag.
- ✗ — Not yet implemented: the engine feature appears in real content and SkyRender has no code path for it yet. The notes say what supporting it would take.
- — — No rendering effect for a viewer (UI flags, packing hints, reserved bits), or not meaningful on `BSLightingShaderProperty`.

### SLSF1 (shaderFlags1)

| Bit | Name | Used | Notes |
|---|---|---|---|
| 0 | Specular | ✓ | Gates the Blinn-Phong specular pass. |
| 1 | Skinned | ~ | We use a stronger signal: niflysharp's `GetShapeBoneList(shape, ...)` returns the actual bone count from the SkinInstance/SkinData blocks. The flag exists to indicate the *presence* of skinning data; checking the data directly is one extra niflysharp call but correct even on malformed NIFs where the flag is out of sync. |
| 2 | Temp_Refraction | ✗ | Engine-internal flag for water/glass refraction shapes. Refraction is not yet implemented. |
| 3 | Vertex_Alpha | ✗ | Tells the engine to consume the vertex color's alpha channel. We use SLSF2_Vertex_Colors as the gate and always multiply `baseColor.a *= vertexColor.a` when it's set. The Vertex_Alpha-without-Vertex_Colors edge case is not handled separately. |
| 4 | Greyscale_To_PaletteColor | ✓ | Lighting shader: switches the hair-tint code path to `baseColor.rrr * tint_color * scale`. Effect shader: colour = `baseColorScale × palette(source.g, baseColor.r × vertex.r)` from the greyscale texture ([Effect shaders](#effect-shaders-bseffectshaderproperty)). |
| 5 | Greyscale_To_PaletteAlpha | ~ | Effect shader: ✓, alpha = `palette(source.a, alpha).a`. Lighting shader: ✗, it would mean a third palette branch in the hair-tint path; not seen on characters. |
| 6 | Use_Falloff | ✓ | Effect shader only: opacity = `lerp(startOpacity, stopOpacity, smoothstep((abs(N·V) - startAngle) / (stopAngle - startAngle)))`, the angles stored as cosines. Per pixel here, per vertex in the engine. |
| 7 | Environment_Mapping | ✓ | Enables slot 4 spherical env-map sampling. |
| 8 | Recieve_Shadows | ✗ | We unconditionally sample the shadow map in the lighting loop when `EnableShadows` is true. Honoring it per shape needs a uniform and a branch; characters always set it, but some scene objects (sky domes, distant meshes) clear it. |
| 9 | Cast_Shadows | ✗ | We don't read this flag. The shadow pre-pass uses heuristic culls instead (`RenderShadowDepthPass` in [GlRenderer.cs](../src/SkyRender/Gl/GlRenderer.cs)): skip pure alpha-blend shapes (their shadow would be a soft amorphous blob), skip eyes (always self-shadowing). On characters the flag is always set and the heuristic gives cleaner shadows; in scenes, honoring a cleared flag is still to do. |
| 10 | Facegen_Detail_Map | ✓ | Enables slot 3 overlay (detail map on faces). |
| 11 | Parallax | ✗ | We don't sample slot 3 as a height map for view-dependent UV displacement. Needs a per-pixel raymarch in the fragment shader. Rare on characters; common on architecture, dungeon walls and some armor. |
| 12 | Model_Space_Normals | ✓ | Selects MSN normal-map path *and* FaceTint blend mode (overlay vs multiply). |
| 13 | Non_Projective_Shadows | ✗ | Specialty flag for shapes that should produce undistorted shadows (stairs and similar). |
| 14 | Landscape | ~ | Marks a landscape shape. SkyRender draws landscape from LAND data rather than from NIF shapes ([Part 6](#part-6--terrain-and-water)). |
| 15 | Refraction | ✗ | Glass/water refraction effect. Needs a backbuffer copy + UV displacement stage. |
| 16 | Fire_Refraction | ✗ | Same as Refraction, for fire effects. |
| 17 | Eye_Environment_Mapping | ✓ | Enables slot 4 env-map for BSLSP_EYE shader type. |
| 18 | Hair_Soft_Lighting | ✓ | Enables hair backlight pass. |
| 19 | Screendoor_Alpha_Fade | ✗ | Drives dithered alpha fadeout for LOD distance transitions. LOD and distance fade are not yet implemented. |
| 20 | Localmap_Hide_Secret | — | Local-map UI flag — controls whether the shape appears on the in-game local map. No rendering implication. |
| 21 | FaceGen_RGB_Tint | ✗ | We don't branch on this bit; we apply `tint_color` unconditionally when `built.ShaderType == 5` (BSLSP_SKINTINT). In practice the bit is set whenever shader type is 5, so the type acts as a proxy. A hypothetical shape with shader type 5 and this flag *unset* would get over-tinted by us — engine would render it with the BSLSP-stored `(1,1,1)` skinTintColor instead. Not seen in practice. |
| 22 | Own_Emit | ✓ | Enables emissive pass. |
| 23 | Projected_UV | ~ | World-projected UVs (snow and moss coverage on scene objects). The engine sets it at runtime from a static's material object, never from the NIF, so the flag itself is not read; the single-pass path is drawn when a host sets `GlMesh.HasProjectedMaterial` ([Stage 2b](#stage-2b-projected-material)). Multi-pass materials (a second piece of geometry) are not modelled. |
| 24 | Multiple_Textures | ✗ | Engine flag for shapes that swap their texture set based on game state. |
| 25 | Remappable_Textures | ✗ | Runtime texture-slot remapping. |
| 26 | Decal | ~ | In the alpha-blend pass a decal never writes depth and draws after the sorted blended set in NIF block order (Pass 2 in [GlRenderer.cs](../src/SkyRender/Gl/GlRenderer.cs)), which is what character decals (hairlines, face overlays) need. The engine's depth offset for decals over coplanar surfaces is not applied, so opaque decal geometry in cell scenes can still z-fight. |
| 27 | Dynamic_Decal | ~ | Treated as Decal. |
| 28 | Parallax_Occlusion | ✗ | Heavy-cost parallax variant with self-occlusion sampling. |
| 29 | External_Emittance | ✗ | Tells the engine the emissive value comes from an external source (world lighting, scripts) rather than the BSLSP-stored field. We always use the BSLSP-stored value. |
| 30 | Soft_Effect | ✓ | Effect shader only: alpha fades over `softFalloffDepth` as the scene surface behind comes close, and near-zero alpha is discarded. The fade needs the ambient-occlusion depth prepass (opaque geometry); without it only the discard applies. |
| 31 | ZBuffer_Test | ~ | Effect shader: ✓, a cleared bit draws the shape without the depth test. Lighting shader: ✗, `GL_DEPTH_TEST` stays on for the whole main pass (characters always set it). |

### SLSF2 (shaderFlags2)

| Bit | Name | Used | Notes |
|---|---|---|---|
| 0 | ZBuffer_Write | ~ | Lighting shader: honoured functionally via `NiAlphaProperty.HasAlphaBlend`: alpha-blended shapes get `GL.DepthMask(false)` for their pass (the main pass in `GlRenderer.Render`, [GlRenderer.cs](../src/SkyRender/Gl/GlRenderer.cs)), opaque shapes get `GL.DepthMask(true)`. Bethesda pairs the two signals — alpha-blended shapes have this flag *unset* in the BSLSP and the alpha-blend bit *set* in the AlphaProperty. We use the AlphaProperty signal because it's also what tells us to enable `GL_BLEND`, so the two decisions are co-located. Effect shader: read directly (`GlMesh.DepthWrite`). |
| 1 | LOD_Landscape | ✗ | Distant-LOD landscape. LOD is not yet implemented. |
| 2 | LOD_Objects | ✗ | Distant-LOD object. |
| 3 | No_Fade | ✗ | Disables LOD distance fade; relevant once LOD fade exists. |
| 4 | Double_Sided | ✓ | Disables `GL_CULL_FACE` host-side for the shape. Effect shapes under an `NiBillboardNode` are also drawn double-sided: the engine turns a billboard to face the camera, so it is never culled there. |
| 5 | Vertex_Colors | ✓ | Enables `baseColor *= vertexColor`. |
| 6 | Glow_Map | ✓ | With `Own_Emit`, slot 2 on non-skin shaders modulates the emissive term per texel (`emissiveColor × emissiveMultiple × glow.rgb`, sk_default.frag), bound by `NifMaterialBinder` ([Stage 5](#stage-5-emissive)). Enchantment glows, lit windows and similar. Skin shader types keep slot 2 as the subsurface map. |
| 7 | Assume_Shadowmask | ✗ | Tells the engine to interpret the diffuse alpha channel as a shadow mask. |
| 8 | Packed_Tangent | — | Indicates tangents are stored packed in vertex data with a Bethesda-specific encoding. niflysharp unpacks them transparently — we receive standard `vec3` tangents from `nif.GetTangentsForShape(shape)` regardless of this bit. |
| 9 | Multi_Index_Snow | ✗ | Snow-coverage variant for outdoor objects. |
| 10 | Vertex_Lighting | ✗ | Tells the engine to use baked vertex lighting and skip dynamic lights. We always light dynamically. Characters never set it; some scene objects do. |
| 11 | Uniform_Scale | — | Optimization hint (uniform world-space scale, so the normal matrix needs no inverse-transpose). We compute the inverse-transpose unconditionally. |
| 12 | Fit_Slope | ✗ | Conforms a shape to terrain slope. |
| 13 | Billboard | ✗ | Tree/grass billboard rotation around the world-up axis. |
| 14 | No_LOD_Land_Blend | ✗ | LOD landscape blending. |
| 15 | EnvMap_Light_Fade | ✗ | Fades the environment-map contribution by `NdotL`. Subtle; visible on strongly reflective surfaces in dim light. |
| 16 | Wireframe | — | Engine wireframe rendering mode. We have a dedicated wireframe shader ([Shaders/wireframe.frag](../src/SkyRender/Shaders/wireframe.frag)) used as a viewport mode toggle, so this BSLSP bit is not consulted. |
| 17 | Weapon_Blood | ✗ | Weapon damage overlay. |
| 18 | Hide_On_Local_Map | — | Local-map UI flag. No rendering implication. |
| 19 | Premult_Alpha | ✗ | Diffuse uses premultiplied alpha. We assume straight alpha across the board; misapplying this on an alpha-blended shape would over-darken the fringes. Not seen on character meshes. |
| 20 | Cloud_LOD | ✗ | Cloud rendering. |
| 21 | Anisotropic_Lighting | ✗ | Hair-strand-direction anisotropic specular. We use isotropic Blinn-Phong for hair. Honoring this would mean implementing a Kajiya-Kay or Marschner BRDF and reading hair-flow tangents. The absence is most visible on long, dramatically lit hair; with the default subtle backlight and isotropic spec it reads acceptably. |
| 22 | No_Transparency_Multisampling | ✗ | MSAA tuning flag. The offscreen renderer uses 4x MSAA but does not consult this flag; the interactive control's framebuffer is the host's. |
| 23 | Unused01 | — | Reserved bit, no engine semantics. |
| 24 | Multi_Layer_Parallax | ✗ | Layered parallax (ice, layered glass). |
| 25 | Soft_Lighting | ✓ | Switches diffuse from Lambert to wrap (`NdotL * 0.5 + 0.5`). |
| 26 | Rim_Lighting | ✓ | Enables skin rim pass. |
| 27 | Back_Lighting | ✗ | Per-pixel back-lighting using a backlight texture. We have a hair-soft-lighting term but no separate skin back-lighting path. Visible on thin-skinned regions (ears, finger webs) and on foliage lit from behind; under the default lighting these don't get strong back-light, so the absence isn't striking. Adding it would mean reading slot 7 as a backlight texture (currently used for spec masks) under a different shader-flag check. |
| 28 | Unused02 | — | Reserved. |
| 29 | Tree_Anim | ~ | Tree wind animation. Scenes are static, so no sway is drawn, but the flag is honoured for what it changes: the vertex alpha is the sway weight, not opacity ([Part 6](#trees-slsf2_tree_anim)). |
| 30 | Effect_Lighting | ~ | Effect shader only: the lit permutation, where `lightingInfluence` mixes in the light at the object. Approximated by the scene's summed ambient and directional lights, unshadowed and unoriented (the engine's `DLightColor`); point lights are not modelled. |
| 31 | HD_LOD_Objects | ✗ | HD LOD object variant. |

---

## Appendix B — BSLightingShaderProperty field inventory

What we read from the shape's BSLSP, where it goes, and what we ignore.

| Field | Read | Used as | Notes |
|---|---|---|---|
| `shaderFlags1` | ✓ | Bit-tested per Appendix A | |
| `shaderFlags2` | ✓ | Bit-tested per Appendix A | |
| `bslspShaderType` | ✓ | Branches in the material binder | Selects FACE/SKINTINT/HAIRTINT/EYE rendering paths |
| `glossiness` | ✓ | `materialGlossiness` uniform | Blinn-Phong exponent |
| `specularStrength` | ✓ | `materialSpecularStrength` uniform | Spec multiplier |
| `specularColor` | ✓ | `specularColor` uniform | RGB |
| `softlighting` | ✗ | (bypassed) | We use `subsurfaceRolloff` for the wrap term instead |
| `rimlightPower` | ✓ | `rimlightPower` uniform | Exponent for hair backlight + skin rim |
| `subsurfaceRolloff` | ✓ | `subsurfaceRolloff` uniform | Controls SSS wrap |
| `backlightPower` | ✗ | (unused) | We don't do back-lighting separately |
| `grayscaleToPaletteScale` | ✓ | `greyscaleToPaletteScale` uniform | Hair-tint multiplier |
| `fresnelPower` | ✗ | (unused) | We have a fixed `pow(..., 4)` in the tone-map fresnel |
| `emissiveColor` | ✓ | `emissiveColor` uniform | RGB |
| `emissiveMultiple` | ✓ | `emissiveMultiple` uniform | Scalar |
| `uvScale` | ✓ | Vertex shader `u_uvScale` | Folded into TexCoords at vertex stage |
| `uvOffset` | ✓ | Vertex shader `u_uvOffset` | Same |
| `environmentMapScale` | ✓ | `envMapScale` uniform | |
| `eyeCubemapScale` | ✓ | folded into `envMapScale` uniform | Host-side selection; used only for BSLSP_EYE (shader type 16) shapes |
| `refractionStrength` | ✗ | (unused) | Refraction not yet implemented |
| `skinTintColor` | ✗ | (unused) | NIF stores `(1,1,1)` — the engine doesn't use this for body tinting at runtime; it pulls QNAM from the NPC record, which a host passes as `MaterialBindOptions.SkinTintColor`. We follow the engine. |
| `skinTintAlpha` | ✗ | (unused) | Same |
| `hairTintColor` | ✓ | `tint_color` uniform (when ShaderType==HAIRTINT) | Baked value wins (engine-verified). SkyRender.Character supplies the NPC's hair colour as the fallback and a worn hair-slot tint ([CHARACTER_PIPELINE.md](CHARACTER_PIPELINE.md#npc-texture-inputs)) |
| `parallaxInnerLayerThickness` | ✗ | (unused) | Parallax not yet implemented |
| `parallaxRefractionScale` | ✗ | (unused) | |
| `parallaxInnerLayerTextureScale` | ✗ | (unused) | |
| `parallaxEnvmapStrength` | ✗ | (unused) | |
| `wetnessSpecScale` … `wetnessMetalness` | ✗ | (unused) | Wetness not yet implemented |
| `lumEmittance` … `finalExposureMax` | ✗ | (unused) | Newer-version exposure controls; only meaningful in FO76-era files |
| `subsurfaceColor` | ✗ | (unused) | Hardcoded warm-flesh `(1.0, 0.35, 0.25)` in the SSS shader |
| `transmissiveScale` | ✗ | (unused) | |
| `sparkleParameters` | ✗ | (unused) | |

---

## Appendix C — Texture slot inventory

Per the BSShaderTextureSet convention, slot meanings vary by shader type. The renderer's interpretation:

| Slot | Skin shaders (FACE, SKINTINT) | Hair (HAIRTINT) | Eye (EYE) | Default |
|---|---|---|---|---|
| 0 | Diffuse | Diffuse (greyscale palette) | Iris diffuse | Diffuse |
| 1 | Normal (TSN or MSN) | Normal | Normal | Normal |
| 2 | SSS mask (`.r` channel) | (unused) | (unused) | Glow map (not yet implemented) |
| 3 | Detail map (when SLSF1_Facegen_Detail_Map) | (unused) | (unused) | Parallax height (not yet implemented) |
| 4 | Env cubemap | (unused) | Eye cubemap | Env cubemap |
| 5 | Env mask | (unused) | Env mask | Env mask |
| 6 | FaceTint (face shapes only, primary head) | (unused) | (unused) | (unused) |
| 7 | Specular mask (`.r`) | (unused) | (unused) | Specular mask |
| 8 | (unused) | (unused) | (unused) | (unused) |

FaceTint goes only on the primary head shape: among all shapes in a FaceGen NIF, the one elected by `NifMeshBuilder` (see [Dismember partitions](#dismember-partitions-and-shape-filtering)) gets it. Accessory shapes (mouth, eyes, brows, lashes) share the FaceGen NIF but each have their own diffuse and don't need the per-NPC tint baked at character creation.
