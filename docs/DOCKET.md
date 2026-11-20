# SkyRender docket

Open work worth doing, with how to prove it before changing code. Close an item by moving it to
the CHANGELOG entry that ships it.

Items marked *lead* came from houseCARL's notes on two outside tools, Terrain Inspector 2.1 and
SkyInspector 1.10.19b (handoff `houseCARL/_claude/Handoffs/Tasks/2026-11-16-skyrender-leads-from-outside-tools.md`;
the lead number is in brackets). Those tools have no licence, so take only the fact. Check every
fact against the game or a trusted source (Community Shaders, NifSkope, nif.xml) before acting on
it; the tool's word is not enough.

## Correctness

### 1. Apply vertex colours only when SLSF2_Vertex_Colors is set *(lead 1)*
- **Now:** `NifMeshBuilder` sets `HasVertexColors` whenever the shape carries colour data
  (`Nif/NifMeshBuilder.cs` ~1888). `SLSF2_VertexColors` (bit 5) is declared at line 470 but never
  read. The lighting shader multiplies the colours in, and the effect path copies the same flag
  (`NifMaterialBinder.ApplyEffectMaterial`).
- **Claim:** the engine ignores colour data when the flag is clear. Some fur meshes store other
  data in that channel and come out rainbow-coloured.
- **Test:** scan the corpus with `tools/NifSnapshot` for lighting and effect shapes that have
  colours but the flag clear. Render the worst one against in-game or NifSkope. Check that the
  same flag gates effect shapes too (Community Shaders' effect shader), and that the Tree_Anim
  alpha path still gets its wind weights when the flag is clear.
- **Worth:** high, and the fix is a few lines. Any mesh in this state renders wrong colours
  today.

### 2. Slot 2 on non-glow shapes is the rim/back-light mask *(lead 2)*
- **Now:** slot 2 is bound only as a skin map (shader types 4/5) or a glow map (SLSF2_Glow_Map +
  Own_Emit). For any other shape it is ignored. Rim lighting (`basic.frag` ~1292) has no mask.
  The glow gate does not check shader type 2.
- **Claim:** slot 2 is a glow map only for shader type 2 with Glow_Map. Otherwise it masks rim
  and back lighting.
- **Test:** read Community Shaders `Lighting.hlsl` for the RIM_LIGHTING / BACK_LIGHTING sampling.
  This also settles the conflict with our slot-7 back-light reading (`NifMaterialBinder` line
  ~152). Then render one vanilla creature or armour with Rim_Lighting and a slot-2 texture.
- **Worth:** medium-low. Rim light is subtle, but an unmasked rim over-brightens edges.

### 3. NiSwitchNode / NiLODNode: draw only the active child *(lead 11)*
- **Now:** `NifAccess.Shapes` returns every shape in block order, so every child of a switch or
  LOD node is drawn.
- **Test:** count NiSwitchNode and NiLODNode in vanilla meshes (NifSnapshot or a block-type scan).
  Render one that has them and look for overlapping duplicates. Confirm the active-index field in
  nif.xml (NiSwitchNode Index; for NiLODNode, the first LOD level).
- **Worth:** medium where such nodes occur (the doubled geometry is easy to see), and nothing
  elsewhere. Do the scan first.

### 4. Distance fog when far ≤ near *(lead 19, the renderer's part)*
- **Now:** `GlRenderer` and `DistanceFog.Amount` clamp the range to 1e-3. With far ≤ near,
  everything past `near` gets full fog.
- **Claim:** no fog when far ≤ near. In the engine's own form, a negative range saturates to 0
  past `near`, which agrees.
- **Test:** a `DistanceFogTests` case for far < near, plus one interior whose lighting template
  has far ≤ near, compared in-game.
- **Worth:** small, cheap and contained. It prevents a fully fogged frame from bad or zero data.

### 5. Terrain: more than 8 layers per quadrant; vertex-colour form *(lead 17)*
- **Now:** `TerrainQuadrant.MaxLayers = 8`; later layers are dropped (`GlWorldFactory` ~117). The
  "over" chain, per-quadrant base and 17×17 per-quadrant opacity already match the lead. Vertex
  colour is divided by its brightest channel (`TerrainCell.Colors` doc); the lead says it
  multiplies.
- **Test:** a host-side count of layers per quadrant over Skyrim.esm LAND records (the lead says
  "up to 19" per cell; per-quadrant counts are what matter). If some quadrants exceed 8, render
  one and look for the missing paint. Settle the vertex-colour form with one in-game comparison
  of a strongly tinted VCLR cell.
- **Worth:** depends on the count; raising the cap costs texture units, or a second pass. The
  vertex-colour question is one screenshot.
- *Already checked:* texture repeat stays at 12 per **quadrant** (in-game comparison, recorded in
  `TerrainBuildOptions.TextureRepeat`). The lead's "12 per cell" does not override it.

### 6. Multilayer Parallax (shader type 11) *(lead 3)*
- **Now:** not modelled. Env-map mask fallback (slot 5 → specular → normal alpha) is already
  done.
- **Test:** list type-11 shapes in the corpus; read the inner-layer terms in Community Shaders.
- **Worth:** low. The type is rare (some ice and armour).

### 7. Light at the model's AttachLight node *(lead 14)*
- **Now:** `PointLight` positions come from the host; SkyRender exposes no way to find
  `AttachLight` in a light's model.
- **Change:** a small `NifAccess`/`ReferencePlacement` helper that returns the node's offset, for
  the host to apply.
- **Test:** a torch sconce or candle in-game against a render, light placed with and without the
  offset.
- **Worth:** low-medium. Point-light pools shift by the node's offset, often tens of units.

## Performance

### 8. Keep the shadow map while nothing it depends on changes *(lead 27, adapted)*
- **Now:** the sun shadow pass and every point-light cube re-render each frame (`GlRenderer`
  ~1326–1332).
- **Change:** skip the pass when the casters, the light and the light frustum are unchanged. That
  is a cache key, not the outside tool's 0.4 s timer, which shows stale shadows.
- **Test:** `RenderTimings` on an interactive world scene with point-light shadows, orbiting the
  camera. Also check pixel equality between the cached and the re-rendered frame.
- **Worth:** medium for interactive world views (viewprobe); nothing for one-shot stills.

### 9. Optional texture-size cap that reads only the mips it needs *(lead 25)*
- **Now:** there is no resolution cap. Uploads take the full chain from level 0. `DdsMipPolicy`
  only decides whether authored mips are used.
- **Change:** a cap setting; `CompressedDds.Read` skips mip levels above it, both when reading
  and when uploading.
- **Test:** `TextureUploadDiagnostic` bytes read, upload time and VRAM for a 50-NPC prewarm,
  capped against uncapped, plus pixel comparison of 750 px portraits (should be near-identical).
- **Worth:** medium for batch portrait renders and big world scenes; it is a new setting, so
  default off.

## Map tiles

From houseCARL's handoff `houseCARL/_claude/Handoffs/Tasks/2026-11-19-skyrender-top-down-map-tiles.md`.
Its needs 1, 3, 4 and 6 are built (CHANGELOG 4.0.0, "Top-down map tiles"); need 2 is met only by
the camera's ceiling. What is left:

### 10. Level of detail by scale *(handoff need 5)*
- **Now:** a tile draws full meshes whatever its scale. At a whole-hold view a tile covers
  hundreds of cells, which is too much to load.
- **Change:** a host-facing way to build a tile's scene from the game's LOD (`DistantLodLoader`
  already reads terrain, object, tree and water LOD) with no loaded cells cut out of it.
- **Test:** one Whiterun-hold tile at 256 units a pixel from LOD against the same from full cells:
  time, and whether roads and roofs still read.
- **Worth:** high for the Workbench: it decides whether a zoomed-out map can be textured at all.

### 11. Draw only what the tile's rectangle holds
- **Now:** `GlRenderer` draws every mesh for every frame. A 512 px tile over 4 of 9 loaded cells
  drew in 15 ms against 4 ms with only its own 4 cells loaded.
- **Change:** skip meshes whose bounds lie outside an orthographic camera's box (the bounds are
  not kept on `GlMesh` today).
- **Test:** `RenderTimings.DrawMs` for one tile inside a 5 x 5 cell scene, with and without.
- **Worth:** medium, and only once hosts keep large scenes loaded.

### 12. A height cut that closes what it cuts *(handoff need 2)*
- **Now:** `TopDownCamera`'s ceiling is the near plane: geometry it passes through is left open
  and its inside shows.
- **Test:** a town under an overhang, ceiling set below the overhang; look for open shells.
- **Worth:** unknown until a real case is tried. The plain ceiling may be enough.

### 13. A faster PNG for tiles
- **Now:** a warm 512 px tile is about 6 ms as BGRA32 and 29 ms as PNG: `PngWriter` compresses at
  `CompressionLevel.Optimal`.
- **Change:** a request setting for the compression level, or `Fastest` for RGBA output.
- **Worth:** small. A host that caches tiles on disk pays it once; one that shows them takes BGRA32.

## Live view host

From houseCARL's handoff `houseCARL/_claude/Handoffs/Tasks/2026-11-19-skyrender-view-host-benchmark.md`.
`SkyRender.Wpf`'s `SkyRenderView` is built and CellDemo runs on it (CHANGELOG 4.0.0, "Live view
host"); the measurements, on a slow and on a healthy compositor, are in
`artifacts/fps/host-bench/RESULT.md`. What is left:

### 14. UI-thread stalls and an orbit hitch in CellDemo's frame logs
- **Now:** on a healthy compositor, 9 of 8,605 native frames waited 16 to 184 ms for the UI
  thread (`queued_ms`) in a scene that draws in 0.1 ms, and most runs of either host show one
  55 to 65 ms interval in the orbit step. Neither is the pacer; neither is explained.
- **Test:** log with the demo's log pane, readout and status text switched off in turn; then the
  same view in an otherwise empty window.
- **Worth:** medium. They are the largest late frames left, and a consumer copying the demo
  would copy their cause.

### 15. Why this machine's compositor goes slow
- **Now:** unexplained. It was slow for the Workbench's stage 2 (2026-11-14: WPF tick 15.5 ms,
  31 ms with a child window, 140 ms with `GLWpfControl` drawing) and for the 2026-11-19 host
  matrix; it was healthy for the Workbench's and the demo's frame logs earlier on 2026-11-19. Every
  slow figure is a whole number of 15.6 ms system timer ticks, so the compositor is on a timer,
  not the display's vertical blank. Candidates not tested: the display asleep, the session locked
  or remote, a driver setting.
- **Test:** `CompositorTiming.MeasureCompositionMs()` with the display on and someone at the
  machine, then with the display asleep, then over a remote session.
- **Worth:** medium. The default host no longer depends on the answer; `WpfTexture` and any
  `GLWpfControl` still do.

### 16. The other samples and the character viewer on `SkyRenderView`
- **Now:** `SkyRender.Demo` and `SkyRender.Character`'s viewer (`UC_CharacterViewer`) host a
  `GLWpfControl` themselves, with WPF's frame tick and no readout or log.
- **Change:** move both onto `SkyRenderView`. The character viewer may need `WpfTexture` if it
  draws WPF content over the view; check before choosing.
- **Worth:** medium. NPC2 and SynthEBD ride on the character viewer, and it is the view most
  people see.

### 17. Conditions not reached
- A display faster than 60 Hz; a second machine; a laptop with two graphics adapters; a remote
  desktop session; a real mouse and keyboard (the bench posts window messages, which skip the
  system's own input queue and its coalescing of mouse moves).
- **Test:** the same `matrix.sh` on each, plus one run by hand with the frame log on.

## Parked

- **Particle systems** *(leads 23, 28)*: `NifAccess.Shapes` drops `NiParticleSystem` entirely.
  Drawing them (alpha from NiAlphaProperty, atlas frames from Subtexture Offsets, then culling
  and budgets) is a feature of its own. Revisit if fire and smoke matter for stills.
- **UV / effect controllers** *(lead 8)*: the effect path is static by design (`basic.frag` ~639).
  It matters only for animation.
- **Missing effect source texture** *(lead 9)*: confirm what `u_effectHasTexture = false` draws.
  It should not be a visible white or glow quad where the game shows nothing.
