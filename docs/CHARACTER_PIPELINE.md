# SkyRender.Character pipeline

SkyRender.Character builds characters out of the core pipeline: it resolves an NPC's mesh parts,
loads them with the right inputs, binds NPC-specific material data, and hosts the result in an
interactive WPF view model (`VM_CharacterViewer`) and an offscreen scene builder
(`NpcSceneBuilder`). Read [RENDERING_PIPELINE.md](RENDERING_PIPELINE.md) first: parsing, material
binding, shading, overlays and offscreen rendering all work as described there. This document
covers only what the character tier adds.

## Overview

```
ResolvedNpcMeshPaths (from the host's INpcMeshDataSource)
      │  body / hands / feet / head (FaceGen) / hair / tail paths, skeleton,
      │  TXST texture sets, skin and hair colours, FaceTint, eye shape names
      ▼
[Assets/CharacterPreviewCache.cs] path resolution + prewarm (composes the core AssetCache)
      │
      ▼
NifMeshBuilder.BuildFromFile per part, with the part's biped label (core)
      │
      ▼
[ViewModels/VM_CharacterViewer.cs] ApplyTexturesToGlMesh
   ├─ ARMA TXST overrides on top of NIF-baked texture paths
   ├─ NPC inputs as MaterialBindOptions: hair-tint / FaceGen-tint slot 0,
   │  QNAM skin colour, eye-name matching, blank-detail fallback
   └─ NifMaterialBinder.Bind (core)
      │
      ▼
override channels, shape deformation, slot occupancy → GlRenderer (core)
```

Offscreen NPC renders run the same steps on a fresh `VM_CharacterViewer` per request, created by
`NpcSceneBuilder` (the NPC `IOffscreenSceneBuilder`) from an `NpcRenderRequest`.

## NPC texture inputs

ARMA TXST records can override slots at render time via `ResolvedNpcMeshPaths.TxstTextures[bodyPart][slot]` — applied in `VM_CharacterViewer.ApplyTexturesToGlMesh` on top of the NIF-baked path before the texture is loaded.

**TXST overrides apply to skin shapes only** (`BSLSP shaderType == 5` / `ST_SkinTint`). Body / Hands / Feet NIFs can ship non-skin shapes that share the same NIF — vanilla body NIFs carry a separate clothing shape alongside the skin, hands NIFs sometimes carry fingernail accessories, etc. Those shapes share the body-slot dismember partition (32 / 33 / 37) but use a default-shader path with their own NIF-baked diffuse and normal. The ARMA TXST diffuse for the body part is *skin* (`FemaleBody_1.dds`); applying it indiscriminately to every shape in the NIF clobbers that clothing shape with skin detail it shouldn't have. Gating on shader type 5 lets multi-shape skin NIFs (some body meshes split the torso into more than one skin shape) still receive the override on every skin shape, while leaving non-skin attachments alone. Applied in two places: the install-time fold into `effectiveTextures` (`InstallOneShapeTextures` in [VM_CharacterViewer.cs](../src/SkyRender.Character/ViewModels/VM_CharacterViewer.cs)) and the post-load `ApplyTextureOverrides` target filter (`ApplyTextureOverrides` in [VM_CharacterViewer.cs](../src/SkyRender.Character/ViewModels/VM_CharacterViewer.cs), via `GlMesh.IsSkinShape`).

**Hair colour.** The core rule is that a hair-tint shape's baked `hairTintColor` wins. The
character tier supplies the NPC record's `HairColor` (`ResolvedNpcMeshPaths.HairColorRgb`) as the
fallback when a shape has no baked tint, plus one exception:

**Exception — worn hair-slot items.** The baked-wins rule describes the ENGINE, and the engine never tints worn armor. RaceMenu's skee64 does: `bEnableTintHairSlot`, "automatically tinting worn items in the hair slot where they have the Hair Tint Shader". Wig meshes are routinely authored with a near-black placeholder tint on that assumption — High Poly NPC Overhaul's KS Hairdos wigs bake `(0.133, 0.133, 0.133)`, which is why that mod is famously black-haired until RaceMenu is installed. So for shapes that come from a worn hair-slot item (biped 31 — `HairMeshPath`, or a `MeshOverrideKind.Hair` override) a host-supplied `ResolvedNpcMeshPaths.WornHairSlotTintRgb` wins over the baked value. Null (the host's opt-out, correct for a load order without RaceMenu) restores baked-wins. FaceGen/head-part hair is unaffected — it isn't a worn item, and skee64 never touches it.

**Skin colour.** The NPC record's QNAM `TextureLighting` colour
(`ResolvedNpcMeshPaths.TextureLightingColor`, bytes divided by 255) is passed to the binder as
`MaterialBindOptions.SkinTintColor`; see the core
[skin tint](RENDERING_PIPELINE.md#skin-tint-body) and its
[engine-source cross-check](RENDERING_PIPELINE.md#engine-source-cross-check-verified-against-cs).

**Eye and armour reflections.** Eyes (BSLSP_EYE, `eyeCubemapScale`, vanilla `textures\cubemaps\eyecubemap.dds` with the `EyeEnvironmentMask_M.dds` mask) and env-mapped armour (`envMapScale`, slot 4 / slot 5) go through the core [environment-mapping stage](RENDERING_PIPELINE.md#stage-4-environment-mapping): the cube map is looked up in game axes and scaled by the light reaching the surface. The character tier adds nothing of its own beyond classifying ENVMAP-typed eyeballs as eyes (`ResolvedNpcMeshPaths.EyeShapeNames`), which keeps their authored `envMapScale`.

## Shape deformation and prewarm

### Shape deformation hook (`IShapeDeformer`)

A host deforms the installed body shapes on the CPU through one seam, `VM_CharacterViewer.ApplyShapeDeformer(IShapeDeformer deformer, int weight, bool retainAsSceneInput = true)`. The viewer owns the mechanics; the host owns what the deformation is:

1. **Queue or apply.** While the body shapes aren't installed (`CanDeformShapes` false: no `BodyBaseMeshes` yet, or a rebuild pending) the call is parked in `_pendingShapeDeform` and drained in `ProcessPendingScene`'s commit block -- after texture overrides, before mesh overrides and `SceneCommitted` -- by calling `ApplyShapeDeformer` again. A later call supersedes a queued one.
2. **Prepare.** `NpcWeight` is set, then `IShapeDeformer.Prepare(CurrentBodyNifDiskPath, weight)` runs once. Returning false (nothing to apply) leaves the shapes untouched and raises nothing.
3. **Per shape** of `BodyBaseMeshes` (`DeformShape`): start from the base pose -- the `_0`/`_1` bind poses lerped at the weight when the shape has a pair (the engine's armor-weight blend, re-derived so a weight change needs no reload), else `BindPosePositions` -- then **one** `IShapeDeformer.Deform(shapeName, baseMesh, positions, weight)` call with the whole array, then `NifMeshBuilder.RecalculateNormals` and `NifMeshBuilder.ApplySkinning`. Never a per-vertex callback (see the seam performance rules).
4. **Upload + notify.** `GlMesh.CpuPositions` take the result immediately; the interleaved GPU re-uploads are batched into one `RenderThreadMarshaller.Invoke` ([Part 5](RENDERING_PIPELINE.md#part-5--gl-context-ownership)). `CurrentSceneShapeDeform` records (deformer, weight) -- cleared when a new scene commits, never set while only queued -- and `ShapesDeformed` fires (also when the deform threw part-way; the exception is logged).

`HasPendingShapeDeformer` reports a queued application, so a caller that applies and then reads positions can wait until `CurrentSceneShapeDeform` matches its request. With `retainAsSceneInput` the deformer is kept for `TryGetSceneInputsSnapshot` (`ShapeDeformer` / `ShapeDeformWeight`) and `SceneInputsChanged` fires, even when GL never starts. The guest overlay ([below](#guest-overlay-scene-superimpose)) takes its own deformer through `LoadGuestAsync` and deforms through the same `DeformShape`.

Offscreen renders have no deformation field: A host applies one from `NpcRenderRequest.BeforeDraw`, which runs after the overrides on the fully built scene, where `ApplyShapeDeformer` is immediate.

`SceneCleared` (from `ClearScene`) and `GlContextReset` tell a host when per-scene data it derived (e.g. files next to the body NIF) must be dropped. `SkinningInfo.BoneTransforms` and `CachedSkinTransform` are public so a host can reproduce the deform-then-skin result without the viewer (`SkinOffset` skins an offset once).

What a deformation does (for example per-body morph data, with any editing tools built on top) is entirely the host's.

### Interactive prewarm

**Interactive prewarm** ([VM_CharacterViewer.cs](../src/SkyRender.Character/ViewModels/VM_CharacterViewer.cs) `PrewarmIdentityAsync` -> `CharacterPreviewCache.PrewarmNpc`): the same offload, reached from the live viewer instead of the offscreen batch path, for hosts that know which NPC they will show next while the user is still looking at the current one. It resolves mesh paths and runs the GL-free parse + decode on the thread pool and **does not touch the committed scene** - no `StatusText`, no `IsLoading`, no cancellation of an in-flight load - so it can cost time but never changes what is rendered. It no-ops when the identity is already the loaded one, swallows its own failures (a miss just loads lazily as before), and takes a caller-owned `CancellationToken` because a stale prediction competing for I/O with the load the user actually asked for is worse than no prefetch.

## Override channels (textures + meshes)

The host can mutate a loaded scene through two **neutral, replace-on-reapply** channels on `VM_CharacterViewer`. Both queue while a load/rebuild is in flight and drain in `ProcessPendingScene` once the scene commits (so a host can fire them right after `LoadAsync` returns without awaiting the GL install).

**`ApplyTextureOverrides(IEnumerable<TextureOverride>)`** — retargets texture slots (0/1/2/3/7) on existing skin shapes. Each `TextureOverride(bodyPart, slot, gameRelativePath)` routes to `Renderer.Meshes.Where(m => m.BodyPart == bodyPart && m.IsSkinShape)` (Head routes only to the primary head shape). The host determines `bodyPart`/`slot` from its own record data. Slot 3 (facegen detail) replaces the head's complexion/dirt detail map; if the override file doesn't resolve, the mesh's current slot-3 state is kept rather than binding the white fallback (same rationale as the [missing detail-map demotion](RENDERING_PIPELINE.md#detail-map-face)).

*Per-shape (worn-armor `AlternateTextures`).* A `TextureOverride` may carry an optional `ShapeName`. When set, it targets **only** the shape whose NIF geometry node name equals it — `Renderer.Meshes.Where(m => m.BodyPart == bodyPart && m.ShapeName == ShapeName)` — ignoring the skin-shape gate (an alternate texture can retexture a non-skin sub-shape). This is the **base-body** counterpart to the mesh-override `ShapeTextures` channel below: a worn-armor `AlternateTextures` (MODS) entry retextures one named sub-shape of a body NIF that a replacer split into several distinctly-named shapes, and the base body is rendered directly, not as a mesh override, so it needs this path rather than `MeshOverride.ShapeTextures`. Shape-named overrides are applied **after** the flat body-wide ones (stable `OrderBy` on `ShapeName` emptiness) so the more specific per-shape texture wins on its shape when a config carries both for the same slot. **Index vs. Name:** the alternate texture's `Index` field is *not* the NIF shape enumeration order (a `[Index == 1]` entry named `BodyShapeB` can be the third geometry in the file), so the host must resolve the entry's `Name` — the target 3D object name — from the record template and pass that here; matching by geometry index alone would texture the wrong shape. The mesh-override channel's `MeshOverride.AlternateTextures` handles the same two-field problem from the other side: it carries **both** the entry's 3D Name and 3D Index, matches by name first, and only entries whose name matches *no* shape of the mesh fall back to index matching (`AlternateTextureMatching`) — because mesh-editor rebuilds **rename** shapes while keeping their order (name goes stale, index stays good; the engine keys on the index, so the variant renders in game but was lost here — the “black skirt” bug), while block-sorting tools desync the index but keep names good. Renamers keep order and re-sorters keep names, so name-first + dangling-only index fallback covers both.

**`ApplyMeshOverrides(IEnumerable<MeshOverride>)`** — *synthesizes* extra renderable shapes from `.nif` files the base NPC doesn't carry. The first consumer is an auxiliary armature on a non-base biped slot (e.g. slot 52, `(BipedObjectFlag)4194304`) that some mods add to the actor **at runtime by script** — it is never in the static `WornArmor` the resolver walks, so its mesh lives only in a host-supplied model path. The same channel serves outfit and headgear previews.

Per override (`MeshOverride`: `Key`, `MeshPath`, `BipedSlots`, `HidesSlots`, `Kind`, `Textures`) the channel:

1. resolves `MeshPath` via `GameAssetResolver` (an auxiliary armature's NIFs are typically dependency-mod assets under `Data\meshes\…`, resolved like any other mesh — not under the config prefix);
2. CPU-skins it to the **current scene's skeleton** (`_cachedMeshPaths.SkeletonPath`) using the same `NifMeshBuilder.BuildFromFile` path as the base meshes — skinning and skin-tint are already generic (bone weights from the NIF, tint from the NIF shader type), so no per-slot logic is needed;
3. applies the bundled `Textures` (else the NIF's own `BSShaderTextureSet`), then folds any per-shape `AlternateTextures` (MODS) entries on top of their target shape — matched by 3D Name with a 3D-Index fallback for renamed/rebuilt meshes (see the *Per-shape* note above and `AlternateTextureMatching`); entries that bind to no shape by either key are logged, not silently dropped — an override ships its mesh and texture set together, so the texture set travels *with* the mesh rather than through the texture-only channel;
4. tints by `Kind` default — `Skin` lets the shader decide (an auxiliary skin mesh tints with the body QNAM like any slot-32 skin shape); `Armor`/`Headgear` never take the skin QNAM tint;
5. weight-morphs it — the override NIF is the `_1` (weight-100) variant, so its `_0` companion is loaded and `Positions`/normals are lerped at `t = NpcWeight/100` (the same `BlendWeightMorph` the base body gets). Without this the auxiliary mesh, authored to fit a weight-100 body, floats low/forward on a lower-weight NPC;
6. registers the shape under `Key` (also used as `GlMesh.BodyPart`, so a slot's `SkinTexture.*` routed by `ApplyTextureOverrides` lands on it) with its biped slots.

**Armor skin inheritance.** An armor NIF commonly bakes its own bare-skin shapes — the bare arms of a sleeveless cuirass, the wrists of gauntlets — as `ShaderType 5` (`ST_SkinTint`) geometry that ships a **placeholder** body diffuse (typically `MaleBody_1.dds`). When applying an `Armor`/`Headgear` override, the channel replaces that placeholder on those skin shapes with the **actor's race skin** — the same TXST the base body already resolved, reused from `_cachedMeshPaths.TxstTextures` (the `Body` set for body/forearm/calf/feet skin, the `Hands` set for hand-slot pieces). The `ShaderType == 5` gate keeps this off the armor's metal/leather shapes; a `SkinTexture` the ArmorAddon supplies itself (`MeshOverride.Textures`, from `NAM0`) takes precedence per slot. Without this, a race with a distinct body texture (e.g. Snow Elf → `MaleBodySnowElf.dds`) shows default-tan arms under armor while its face and bare body stay pale. Implemented in `VM_CharacterViewer.ApplyMeshOverrides`, mirroring the existing per-shape rule that already preserves the QNAM **skin tint** for these same baked-in armor skin shapes (armors with exposed skin such as the Hide cuirass) — this extends that handling from tint to **texture**.

> **Caveat — observed, not documented.** This rule (*an armor's skin partition is textured with the actor's race skin unless the ArmorAddon overrides it via `NAM0`*) is the **observed** in-engine result, not behavior taken from authoritative Bethesda / Creation Kit documentation. It was confirmed empirically with **Knight-Paladin Gelebor** in the Ancient Falmer cuirass: the cuirass ArmorAddon's `NAM0` skin texture is empty and the cuirass NIF references `MaleBody_1.dds`, yet in-game his arms render as pale as his face — so the only possible source for that texture is the `SnowElfRace` skin. This **contradicts a fair amount of online modding "lore"** which states that armor uses its own baked skin texture. There may be tertiary / advanced engine behavior — per-armor skin-texture swap lists (`NAM2`/`NAM3`), skin-tone interactions, race-specific armatures — that we do **not** model here, for lack of concrete documentation. If a future asset contradicts the observed rule, this is the place to revisit.

**Numeric slot routing.** `ParseBodyPart` understands the raw-cast `(BipedObjectFlag)N` form (not just `BipedObjectFlag.Body/Hands/Feet`), mapping each single-bit flag to a routing key — named base parts keep their labels, everything else becomes a generic `"Slot{n}"` (slot 52 → `"Slot52"`). `BipedFlagToBodyPart(int)` is the shared converter so a host builds a `MeshOverride.Key` that matches what the parser routes textures to. No semantic per-slot concept exists anywhere — the slot number is the only key.

**Slot occupancy / hiding.** Every shape carries the biped slots it occupies (`NpcMeshInfo.BipedSlots`, attached through `GlMesh.HostData`; base shapes get theirs from their body-part label, override shapes from `MeshOverride.BipedSlots`) plus a `SlotDrawPriority` (0 skin/base, 1 armor, 2 headgear). After each `ApplyMeshOverrides`, `ResolveSlotVisibility` hides any shape a strictly-higher-priority shape's `HidesSlots` covers — body armor (slot 32) hides the base body, headgear (slots 30/31) hides hair. The flag is `GlMesh.IsHiddenByHost`, kept separate from the missing-texture cull (`IsRendering`); the render passes gate on `ShouldRender = IsRendering && !HiddenBySlotOccupancy`. An auxiliary mesh on a free slot (e.g. slot 52) collides with nothing, so it is never hidden and never hides — but the machinery is in place for a host's clothing/headgear features.

**Bone resolution → skip, or warn-but-render.** CPU-skinning resolves each weighted bone's world transform from the skeleton NIF or, failing that, the **mesh's own NIF** — actor body/armor NIFs embed copies of the bones they're weighted to, which is how the base body renders auxiliary-armature weights even on a skeleton that lacks those bones (in the field: a test body skinned 26 bones, 24 from the skeleton + 2 from the body NIF, when the skeleton mod that adds the auxiliary bones was absent; the auxiliary armature's own NIF likewise embeds the full bone chain it is weighted to). `TryApplyCpuSkinning` classifies each weighted bone and surfaces two signals:

- **`BuiltMesh.UnresolvedSkinBones`** — bone present in *neither* source; it keeps a zero transform and would collapse its vertices to the origin. `ApplyMeshOverrides` **skips** any shape that has one — no crash, no bind-pose collapse.
- **`BuiltMesh.BonesAbsentFromSkeleton`** — bone present in the mesh NIF but absent from the skeleton, so it rendered via the mesh-NIF fallback. The shape still renders, but on a frame the base meshes (which DO get those bones from the skeleton) don't share, so it can be **misaligned**. This is exactly how a missing/incompatible skeleton mod manifests (an auxiliary mesh that sits slightly off the body). `ApplyMeshOverrides` renders it but adds a skeleton-compatibility warning — **except for SMP/HDT physics bones**: when the NIF links a physics XML (`NiStringExtraData "HDT Skinned Mesh Physics Object"`, surfaced as `BuiltMesh.PhysicsXmlPaths`), bones that config references (checked against the resolved XML's attribute values) legitimately exist *only* in the mesh NIF — no skeleton ships them, the physics engine animates them at runtime, and the bind-pose fallback render is exactly the authored rest pose — so they are excluded from the warning (`VM_CharacterViewer.FilterPhysicsDrivenBones`). Without this, every SMP-skirted outfit tripped a false "install XPMSSE" warning. Bones the physics config does not name (e.g. genuine XPMSSE-only bones on a vanilla skeleton) still warn; an unresolvable/unreadable XML leaves all bones warned (conservative).

Both, plus unresolved override NIFs, are surfaced on `VM_CharacterViewer.MeshOverrideWarnings`, which a host shows as a render-preview warning line — e.g. "the loaded skeleton is missing bone(s) […]; install the skeleton these meshes require (XPMSSE / XP32 Maximum Skeleton)." The normal load path ignores both signals — its behavior is unchanged.

**Offscreen parity.** The same channel is available to the headless mugshot path: `NpcRenderRequest.MeshOverrides` (an `IEnumerable<MeshOverride>?`) is applied after texture overrides and before the framing/render, via the same `VM_CharacterViewer.ApplyMeshOverrides` on the per-render VM — so a saved PNG shows the auxiliary/outfit/headgear shapes (and the slot-hiding) identically to the live preview. The post-apply `MeshOverrideWarnings` are copied into the optional `OffscreenRenderRequest.MeshOverrideWarningsOut` list (parallel to `MissingMeshPathsOut` / `MissingTexturePathsOut`) so the host can flag an incomplete offscreen render the same way it flags one in the preview. Null `MeshOverrides` leaves the offscreen path byte-identical to before.

**Body replacement (`BodyReplacement`).** Unlike the two channels above this is a *load-time* input, not a post-load mutation: `VM_CharacterViewer.BodyReplacement` (record `BodyReplacement(BodyNifPath, PartMeshes, TextureFolder, UsesNpcSkin)`) names a body type to show instead of the NPC's own. `LoadAsync` swaps it into the resolved paths (`ResolvedNpcMeshPaths.WithBodyReplacement`) before anything captures them, so `LoadAllMeshParts`, `_cachedMeshPaths` and the software fallback's `SceneInputsSnapshot` all see one set of part paths, and captures it as `_sceneBodyReplacement` for that load's install. The body is always swapped; hands, feet and tail are swapped when their resolved path's `BodyReplacement.PartKey` -- lower-case, no `data\`/`meshes\` prefix, no extension or `_0`/`_1` suffix, so `Actors\Character\Character Assets\FemaleHands_1.nif` becomes `actors\character\character assets\femalehands` -- is in `PartMeshes` (a body key there also wins over `BodyNifPath`). The host fills `PartMeshes` with the replacement body's part meshes, so the wrists and ankles join the new body instead of keeping the NPC's old hands and feet. Head and hair stay the NPC's own. The swapped NIFs load as the ordinary `"Body"` / `"Hands"` / `"Feet"` / `"Tail"` parts, skinned to the NPC's skeleton; only the body is morphed (`_cachedBodyMeshes`). The record compares by value, `PartMeshes` contents included (`CacheKey`), and a changed or cleared replacement counts as a different scene in the same-NPC short-circuit.

A replacement body usually has no `_0`/`_1` pair (the weight blend is then skipped) and whatever per-body data a host deformer needs is the host's to supply. `SceneBodyReplacement` reports the replacement the installed scene was built with.

*Textures.* With a replacement, every body part's skin shapes (`ShaderType == 5` in `BodyReplacement.SkinBodyParts`: Body, Hands, Feet, Tail) take their skin from one source, whether that part's mesh was swapped or kept, so no seam opens at the wrists or ankles. The NPC's skin TXST is not applied to them: it is UV-mapped to the NPC's own body and scrambles on a different one. Instead `ResolveReplacementBodyTextures` looks up each shape's NIF-embedded texture paths under `TextureFolder` (a data-folder-like root, tried as given and with a `textures\` prefix by the public `VM_CharacterViewer.ResolveTextureUnderFolder`; a host passes the mod folder that ships the body or the skin mod the user picked, since in the merged data folder those vanilla-style paths resolve to the *installed* body's textures). A shape whose diffuse isn't found there -- or with no folder -- renders untextured in flat beige (`GlTextureManager.NeutralSkinTexture`, sRGB 222/192/168) with the NPC's QNAM skin tint off (`ApplyNeutralSkin`); a partial set is not mixed with another body's diffuse. Non-skin shapes keep their own textures as usual. The face follows too, so the neck has no seam: for the FaceGen head's face-tint skin shape (`ShaderType 4`), `ApplyReplacementHeadTextures` reads every texture slot the head NIF names -- diffuse, normal, subsurface, specular and the detail / complexion map (slot 3: `blankdetailmap.dds`, or an `age40` / `age50` / freckles complexion) -- and re-points each one whose path exists under `TextureFolder`, slot by slot; a slot the folder lacks keeps the NPC's file. Skin mods ship `femalehead.dds` / `_msn` / `_sk` / `_s` and their complexion maps as a set with the body's, and the head shares one UV layout across body mods. An NPC whose face uses its own texture paths (a follower or replacer's custom head) matches nothing and keeps them, so a seam can remain for such NPCs. The FaceGen tint is not a NIF slot and stays the NPC's. The same helper runs on the scene install, the head-only rebuild after a head-part change, and the guest overlay; neither `UsesNpcSkin` nor neutral beige touches the face. **Exception -- `UsesNpcSkin`:** every body part keeps the NPC's own TXST and QNAM tint exactly like the worn body, and `TextureFolder` is ignored -- for a replacement sharing the NPC's body's UV layout (for example a variant of the installed body type).

`LoadGuestAsync` takes the same `BodyReplacement` as an optional argument (part of its cache key, carried on `GuestSceneRequest`) and applies the same part swap and texture rule, so a superimposed guest shows its own body type.

## Viewer tools

### Section clip plane controls

The plane itself is a core renderer feature
([RENDERING_PIPELINE.md](RENDERING_PIPELINE.md#section-clip-plane-clip-f--clip-b)). The viewer arms
it from the two toggles under the axis gizmo in its viewport HUD.

**Axis is locked, not live.** `VM_CharacterViewer.ArmClipPlane` samples the camera's view direction **once**, picks whichever world axis it most nearly runs along (largest `|component|`), orients that axis *away* from the camera, and parks the plane at the camera's orbit target. So a front-on view yields the XY plane, a side view YZ, a top-down view XZ. The lock is deliberate: re-resolving the dominant axis per frame makes the cut snap to a different axis mid-orbit, and the depth the user scrolled to stops meaning what it meant a moment earlier. Toggling off and back on re-detects against the new view. Flipping **F → B** directly keeps the plane exactly where it is and only swaps the surviving half-space — both modes are expressed against the same stored `_clipNormal`, negated for Back.

**Moving it.** Ctrl+wheel in the viewport calls `NudgeClipPlane`, sliding the plane along its locked normal with a step of `max(0.03, Distance × 0.0025)` so one notch covers a comparable fraction of the screen at any zoom. The step is deliberately fine: positioning a section cut is an aiming task, where the useful travel is the few units around the surface being sliced, and a step coarse enough to cross the body in a handful of notches skips straight past it. Long traversals are covered by spinning the wheel. The plain wheel deliberately stays camera zoom.

**Known limit.** Clipping is display-only — the CPU-side vertex ray-pick in `VM_CharacterViewer` knows nothing about the plane, so a left-click in Pick Vertex mode can still land on geometry the clip has hidden.

### Texture visibility lock

The per-slot texture flags and their `TextureSlots` names are core (`GlMesh.*Enabled`,
[RENDERING_PIPELINE.md](RENDERING_PIPELINE.md#per-slot-texture-toggles)); the viewer's
right-click menu offers a mesh's `PresentTextureSlots`. The lock is
[Gl/TextureVisibilityLock.cs](../src/SkyRender.Character/Gl/TextureVisibilityLock.cs).

**The lock.** The flags live on each mesh, so any load that builds new meshes brings every slot back on — and a host that steps through NPCs does that on every load. "Lock Texture Visibility" keeps the choice at the viewer level: `VM_CharacterViewer.SetTextureVisibilityLocked(true)` snapshots the scene into a `TextureVisibilityLock`, and every mesh created afterwards gets the remembered choice. It is **session-only** (never persisted: an invisible, restored "textures off" would mislead), and additive: off by default, and a host never calls it.

- **Keys**, resolved per slot, most specific first: the **shape** (`BodyPart` + trimmed `ShapeName`, case-insensitive), then its **group** (`BodyPart` + kind — skin / eye / hair / other, from `IsSkinShape` / `IsEye` / `IsHairTintShader`), then a **fallback** for groups never seen, set only by "Hide All Textures" or by engaging on a fully hidden scene. The kind exists because a FaceGen head's face, eyes, hair and brows are all `BodyPart == "Head"`: a per-part rule would strip the next NPC's hair because this NPC's face was hidden. On engage, a shape remembers its whole state (on and off) but its group learns only what is off — on is every new mesh's default, and recording it would let one untouched shape cancel a hidden sibling.
- **Where it applies:** just before each `Renderer.AddMesh` — `InstallOneShapeTextures` (the sliced scene install), `ApplyOneMeshOverride`, `InstallReplacedHead`, `InstallGuestScene` — once textures, `BodyPart` and the shader flags are set. Deliberately *not* one sweep at `SceneCommitted`: the install is sliced across render ticks, so early shapes would draw textured for a few frames, and a sweep over meshes already on screen could override choices made on them. `ApplyTextureOverrides` re-applies to just the slots an override made present (a normal/skin/detail/specular map the NIF lacked).
- **A slot the mesh lacks is never touched** — toggling it is either invisible (the `has_*` gate) or wrong (Diffuse off greys an untextured shape).
- **Guest overlay:** guest meshes get the lock too, except `TintColor`, which the Translucent style draws its flat overlay colour through. The lock learns only from the primary scene: guest shapes share names with it and would overwrite its choices.
- **Menu semantics:** while locked, each slot checkbox also updates the remembered state (the lock follows the latest choice, not a frozen snapshot). "Hide All Textures" turns off every present slot on every mesh and, when locked, records "hide everything" (fallback included). "Reset All Textures" turns everything back on and clears the remembered state; the lock stays engaged. Unlocking stops re-applying without changing what is shown. Mesh visibility ("Show Mesh") is not part of the lock.
- **Indicator:** `TexturesHiddenByLock` (locked AND at least one slot remembered off) drives the host's viewport badge — a host shows "Textures hidden (locked)" top-right.
- **Diagnostics:** one verbose line per batch — `texture visibility lock re-applied to N mesh(es) (scene install | mesh overrides | head-only rebuild | guest overlay | texture overrides); hidden: …; no remembered choice for …` — plus the existing `GLMESH[i]` state dump at scene commit, which prints every mesh's `*Enabled` flags.

## Geometry accessors

Hosts read the installed geometry back through accessors on `VM_CharacterViewer`, all in the same **pre-ModelScale local space** as `GlMesh.CpuPositions` (post-deformation, see [the deform hook](#shape-deformation-hook-ishapedeformer)):

| Member | Returns |
|---|---|
| `TryGetCurrentVertex(shape, index, out pos)` | one vertex's current position |
| `GetShapePositions(shape)` | `Vector3[]` of the current vertex positions, freshly allocated |
| `GetShapeIndices(shape)` | `int[]` triangle index buffer (flat triplets, shape-local), **not** cloned |
| `GetShapeBoneInfo(shape)` | per-vertex bone indices + weights (4 each, flat), **not** cloned |
| `GetCurrentShapeVertexCounts()` | shape name → vertex count, for topology checks |
| `BodyBaseMeshes` | the body shapes as loaded (bind pose, `_0`/`_1` endpoints, skinning), read-only by contract |
| `HitTest(x, y, w, h)` | the nearest rendered `GlMesh` under a screen point (ray vs CPU triangles) |


## Overlays in offscreen NPC renders (`BeforeDraw`)

The offscreen renderer draws through the same `GlRenderer.Render` ([core overlay pass](RENDERING_PIPELINE.md#part-3--host-overlay-pass-markers-lines-surface-patches)), so a host overlay renders into an offscreen image too. Because an overlay is usually computed *from the deformed geometry*, a host can't precompute it before the request: the offscreen scene doesn't exist yet. `NpcRenderRequest.BeforeDraw` (an `Action<VM_CharacterViewer>?`) closes that gap. It runs on the render thread against the per-request VM **after** mesh load and texture/mesh overrides and **before** `ConfigureCamera` + `Render`, so a deformation it applies (`ApplyShapeDeformer` is immediate there) and the geometry accessors above describe this render, and an overlay it registers is drawn in the same frame.

The per-request VM is created fresh for every render (by `NpcSceneBuilder`, the NPC `IOffscreenSceneBuilder`; the offscreen renderer itself only owns the context, framebuffers, asset scopes and readback), so a hook's state can't leak into a later request (including a host's software fallback preview, which shares the renderer). The hook must not touch WPF objects — it isn't on the dispatcher thread — and a throwing hook is logged and costs only what it had left to do; the frame still renders. Null `BeforeDraw` leaves the offscreen path byte-identical to before.


## Concurrent viewers: the view-model contract

The core rule ([RENDERING_PIPELINE.md](RENDERING_PIPELINE.md#part-5--gl-context-ownership)): every
`GLWpfControl` has its own GL context, and GL work belongs in that control's `Render` callback,
where `VM_CharacterViewer.ProcessPendingScene()` does all uploads and `Renderer.Render(...)` all
draws.

Two paths escape that discipline and are the reason a **context-pinning marshaller** is mandatory once a host shows more than one viewer:

| Escape hatch | What it does | Why it can't simply be deferred |
| --- | --- | --- |
| `VM_CharacterViewer.ApplyShapeDeformer` | `GlMesh.UpdateVertexData` — re-uploads deformed vertices after a host deformation | Callers apply a deformation and immediately read back `CpuPositions` to measure/classify; deferring to the next frame would race those readers |
| `VM_CharacterViewer.Dispose` | deletes shaders, VAOs, VBOs, textures | The control stops rendering the moment its window closes, so a queued teardown would never drain — it would leak the whole scene |

### The contract

`VM_CharacterViewer.RenderThreadMarshaller` is settable precisely so a host can supply an implementation that makes the owning context current before running the action:

- **Single-viewer hosts** keep the plain dispatch-to-UI-thread marshaller (`WpfDispatcherMarshaller`). The only context the UI thread ever has current is that viewer's, so pinning is a no-op.
- **Multi-viewer hosts must install a pinning marshaller.** A typical one is installed by the host's viewer control when it starts its GL control (first context) and again on every `DataContextChanged` (a control serves many VMs over its life, and each arrives holding the DI-registered dispatcher marshaller). It calls `GLWpfControl.Context.MakeCurrent()` **inside** the dispatched action — `IGraphicsContext.MakeCurrent` is only safe on the thread the control runs on, never from the caller's thread.

`ApplyShapeDeformer` batches its uploads into a single marshalled block rather than marshalling per shape, so a deformation costs one `MakeCurrent`, not one per body part.

A host with a side-by-side comparison view can run three viewers at once.

> **When adding a new GL call to `VM_CharacterViewer`:** if it can run outside `ProcessPendingScene` / `Renderer.Render`, it must go through `_renderThread.Invoke`. Anything else is a correctness bug that only reproduces with two viewers on screen.


## Guest overlay scene ("superimpose")

`VM_CharacterViewer.GuestScene.cs` installs a **complete second scene** alongside the primary one, at the same origin, so two differently deformed models can be compared directly. For example, a comparison view can draw one pane's model inside another.

The guest is loaded **by the host VM**, not transplanted from the other viewer. That follows directly from the context rule above: the other pane's `GlMesh` handles name objects in *its* context and are meaningless here. So the guest runs the same pipeline as the primary scene — this VM's `LoadAllMeshParts`, `CreateGlMesh`, `ApplyTexturesToGlMesh`, this VM's `GlTextureManager`, this VM's context — and is installed into a separate `_guestMeshes` list that the primary scene's bookkeeping (`_meshesByBodyPart`, `_cachedBodyMeshes`, `_builtMeshesByBodyPart`) never sees.

```
LoadGuestAsync(identity, deformer, weight, bodyReplacement)
  ├─ resolve paths + LoadAllMeshParts        (off-thread, under the PRIMARY scene's scope chain)
  └─ _renderThread.Invoke → _guestRequest, _guestInstallPending = true
                                    │
ProcessPendingScene (render callback)
  └─ ProcessPendingGuestScene()     ← step 0, before the primary drain
       ├─ bail unless the primary scene is quiescent
       │  (_sceneInstall == null && _pendingScene == null && !_sceneRebuildPending)
       └─ InstallGuestScene: CreateGlMesh → textures → DeformShape → ApplyGuestStyle → AddMesh
```

Four points that are easy to get wrong:

- **The guest resolves under the primary scene's scope chain.** It is being composited into this viewer's world, so `LoadGuestAsync` re-pushes `_currentSceneScopes` / `_currentSceneFolders`. Resolving it under a default chain would silently fall back to the vanilla data folder for its textures.
- **`ClearScene` destroys the guest too.** `Renderer.ClearMeshes()` disposes *every* mesh in the renderer. `ClearScene` therefore calls `NotifyGuestMeshesDestroyed`, which drops the dangling references and re-arms the install so the overlay comes back once the incoming primary scene commits. That is what makes the host pane free to change its own preset without losing the overlay.
- **Geometry is shared, not duplicated.** Both scenes deform through the same `DeformShape` helper, so the two panes' bodies are produced by identical math. The guest's deformer is prepared against the guest's *own* body NIF (`IShapeDeformer.Prepare`) — it may be a different body entirely from the primary's — and, unlike the primary path, the guest is re-derived even when `Prepare` returns false.
- **The guest takes no part in slot occupancy.** `BipedSlots` and `HidesSlots` are forced to 0: it is a comparison overlay, not gear worn by the primary NPC, so it must neither hide the primary model's shapes nor be hidden by them.

**Draw styles** are expressed through per-mesh `GlMesh` material fields; only `InsideOutside` also uses a dedicated pass (the hidden-surface pass below):

| `GuestOverlayStyle` | How it is expressed | Pass it lands in |
| --- | --- | --- |
| `Textured` | untouched after the texture pass | whichever pass its own `NiAlphaProperty` implies |
| `Translucent` | `HasAlphaBlend`, `MaterialAlpha < 1`, `DepthWrite = false`, flat `TintColor` | alpha-blend (never occludes the primary model) |
| `Wireframe` | `RenderAsWireframeFallback` + `WireframeColorOverride` | wireframe overlay only; solid passes skip it |
| `InsideOutside` | Translucent's fields + `DepthBias = +GuestMatchTolerance`; `HiddenSurfaceTint = GuestInsideTintColor`, `HiddenSurfaceDepthBias = -GuestMatchTolerance` | alpha-blend (outside: orange) **and** the hidden-surface pass (inside: blue) |

*Why `InsideOutside`.* A translucent guest over an opaque primary shows where the guest pokes **out**, but where it falls **inside** the primary model the depth test simply drops it; making both translucent doesn't fix that, because two blended layers give the same colour whichever is in front. `InsideOutside` puts the depth order in the colour: the translucent pass paints guest surface in front of the primary model (orange), and **Pass 3, the hidden-surface pass** (`GlRenderer`, after the blend pass) redraws every mesh with a `HiddenSurfaceTint` with `DepthFunc(Greater)`, depth writes off and that flat tint, so only guest surface lying **behind** the primary model's survives (blue). Back faces stay culled, so it is the guest's near surface in both passes, not its far side.

*Match tolerance (`GlMesh.DepthBias`).* Unchanged regions of the two bodies coincide exactly and would z-fight between the two tints. `basic.vert` therefore slides each vertex along its view ray by `u_depthBias` world units (`pos_viewSpace.xyz += normalize(pos_viewSpace.xyz) * u_depthBias`) -- screen position unchanged, depth only, and in world units rather than depth-buffer units, so the tolerance doesn't depend on camera distance or depth precision. The translucent pass is pushed back by the tolerance and the hidden pass pulled forward by it, so guest surface within +/- tolerance of the primary model's paints in neither: untinted means "same here". A host can expose the tolerance (default 0.25 units). Known limit: it is an x-ray, so guest surface hidden by *another* part of the primary model (a leg behind the primary's hand) also paints blue.

`WireframeColorOverride` exists specifically so the guest can borrow the missing-texture fallback's *draw behavior* without inheriting its *color* — that green means "this shape's diffuse failed to decode", and a guest drawn in it would read as a texture error.

