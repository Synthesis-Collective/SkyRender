# Plan: port SkyRender from `niflysharp` (SWIG) to `Nifly` (managed)

Status: **decisions made 2026-10-31; awaiting go-ahead to start Phase 0.** Nothing is implemented.

## Goal

Replace `niflysharp` 1.3.0 (SteveTownsend's SWIG bindings over the C++ nifly, Windows-only
`niflycpp.dll`, last updated 2022) with `Nifly` 1.1.0 (ousnius's clean-room C# rewrite,
GPL-3.0, net8/net10, cross-platform). That removes the only native, Windows-only dependency of
the core `SkyRender` package.

**Done when:**
- No `niflysharp` reference remains in SkyRender.
- Every mesh in the test corpus builds the same as before, or differs only in ways that are
  explained and accepted.
- Scripted renders match the old ones within tolerance.
- Cell load and NPC load stay within +10% of today in CellDemo and in NPC Plugin Chooser 2.
- A GitHub Actions Ubuntu job runs the core tests.
- houseCARL and NPC2 still restore, build and run unchanged on their current pins, and a
  houseCARL-shaped project restores and runs against the new version.

## Decisions (Piranha, 2026-10-31)

| # | Question | Decision |
|---|---|---|
| 1 | Public API | **Wrap.** Callers get a SkyRender-owned `LoadedNif`, never a library type. |
| 2 | Performance budget | Cell load and NPC load within **+10%** of the 3.1.1 baseline. |
| 3 | Linux | **GitHub Actions** Ubuntu job (there's no local Linux install). |
| 4 | Benchmarks | In **NPC Plugin Chooser 2** and **SkyRender.CellDemo**, where performance matters. No benchmark project in SkyRender. |
| 5 | Corpus | **The houseCARL testing environment**: the Skyrim Conflict Lab MO2 instance at `X:\Skyrim Conflict Lab` (201 mod folders), on Skyrim SE at `C:\Games\Steam\steamapps\common\Skyrim Special Edition`. **Not** PiranhaRim VR. |

## Downstream consumers, and how nothing breaks

### houseCARL (dev session active, so hands off)

What it does today:
- `src/housecarl-core` already uses **Nifly**, but its own fork: `Nifly 1.1.0-housecarl.4`
  (Avick3110/NiflySharp @ `55da7071`). That is upstream 1.1.0 plus bounds checks that throw
  `InvalidDataException`, plus one extra public class, `NifLoadErrors`. It is vendored in
  `packages/local`. The repo's `nuget.config` uses **source mapping that only lets `Nifly` come
  from `packages/local`**.
- `_geometry-harness/viewprobe` references `housecarl-core` **and** `SkyRender.Character`
  (pinned at 3.0.1, can be overridden with `-p:SkyRenderVersion`).
- viewprobe also calls `nifly.NifFile` directly (`TexturedScene.cs:4172`,
  `UnreadableAssets.cs:288`). That only works because SkyRender 3.x pulls in `niflysharp`
  transitively.

The conflict, and how to avoid it:

- **Restore.** If SkyRender 4.0 declared `Nifly >= 1.1.0`, a houseCARL project using it would
  fail to restore. NuGet sorts `1.1.0-housecarl.4` *below* `1.1.0`, and source mapping stops it
  fetching upstream 1.1.0. The result is a downgrade warning or error, or a "package not found".

  **Fix:** SkyRender declares `Nifly` with a minimum of `1.1.0-0`. Any 1.1.0 build satisfies
  that, the fork included, and nuget.org has no prereleases, so other consumers still get
  upstream `1.1.0`.

  Phase 0 checks two things before anything relies on this:
  - whether `dotnet pack` raises NU5104 (a stable package with a prerelease range);
  - that a houseCARL-shaped scratch project restores with it.
- **Runtime.** Both packages produce the same assembly identity, `NiflySharp 1.1.0.0`, so one
  copy loads, and in houseCARL that's the fork. SkyRender compiles only against upstream 1.1.0's
  public API, which the fork contains in full. `NifFileLoader` must treat the fork's
  `InvalidDataException` as a failed load (return null), never let it escape. The corpus runs
  against **both** builds (Phase 3).
- **Source.** Publishing 4.0 changes nothing for houseCARL until houseCARL bumps its pin. When
  it does, viewprobe's two direct `nifly.NifFile` uses need their own `niflysharp` reference or
  a port, and any SkyRender API it calls moves to `LoadedNif`. That is houseCARL's change to
  make. This plan delivers a handoff note for it and edits nothing there.

### NPC Plugin Chooser 2

- References `SkyRender.Character` (pinned at 3.1.1) and its own `niflysharp` 1.3.0.
- NPC2's own NIF code is `BackEnd/NifHandler.cs` (1,500 lines, about 98 nifly-touching lines)
  plus six unit-test files. It **reads** NIFs (texture scans, physics XML paths, render shape
  names, partitions) and **writes** them into users' mod output: `RewriteTexturePaths`,
  `RemoveShapesByName`, `RenameShapesByName`, `BakeWigIntoFaceGen`.
- **Decision (Piranha, 2026-10-31): NPC2 migrates to ousnius's `Nifly` too**, in a separate
  NPC2 session, provided its performance doesn't degrade. It uses `NiflySharp` directly,
  **not** through SkyRender:
  - SkyRender is a renderer. Its `LoadedNif` wrapper deliberately doesn't expose the library,
    and NPC2's writes need the library's full editing API.
  - With both moved, each app carries one NIF library, and SkyRender's dependency and NPC2's
    resolve to the same `NiflySharp` assembly.
- The one coupling: `NifHandler.cs:112` gets a `nifly.NifFile` from
  `SkyRender.NifFileLoader.TryLoad`, so that BSA assets served from memory still load. 4.0 adds a
  public `NifFileLoader.TryReadBytes(path, resolver)`, and NPC2 calls
  `new NifFile().Load(new MemoryStream(bytes))` itself.
- **Order:**
  - NPC2's own reads and writes don't depend on SkyRender 4.0, because `niflysharp` and `Nifly`
    can sit side by side. That part can start any time, on SkyRender 3.1.1.
  - Only the `:112` line waits for a 4.0 prerelease.
  - NPC2 ships the change once SkyRender 4.0 and NPC2's own checks both pass.
- **NPC2's checks** (its session owns them):
  - Differential reads: every read API, old against new, over the lab corpus.
  - **Differential writes:** each write API applied to the same inputs with both libraries,
    the outputs reloaded and compared block by block, never byte by byte. houseCARL found that
    Nifly's `Save` re-sorts blocks into its own canonical order. It also found that a struct in
    a list must be read, modified and re-assigned, and that edits must go through the block's
    owning ref or they don't persist (`docs/architecture/nif.md` in houseCARL).
  - A/B timing of the scan and bake paths on an idle machine.
- Several NPC2 sessions are open. All NPC2 work for this port happens in **its own git
  worktree**, built with `-p:SkyRenderVersion=<local prerelease>` when 4.0 is needed. It never
  touches the shared checkout.

### Safety rules for the whole port

1. **No writes** to `S:\Dev\houseCARL`, its worktrees, its `packages/local`, or the NuGet
   cache folder `~/.nuget/packages/nifly/1.1.0-housecarl.4`.
2. **The lab is read-only for this port.** houseCARL sessions use it all the time (DevBench
   runs, R2 test profiles), so:
   - Read its `mods\` folders and the SE game's BSAs directly.
   - Never launch MO2 or the game from it, never write inside it, and never add or change a
     profile. If a load-order run needs a profile, pass a **copy** of its `loadorder.txt` /
     `plugins.txt`.
   - Ask before including any NSFW mod (houseCARL's standing lab rule).
3. **Prereleases go only to `S:\Dev\LocalNuGet`**, as `4.0.0-nifly.N`. Existing pins (3.0.1,
   3.1.1) never resolve to them. Nothing goes to nuget.org without your explicit go.
4. **Check the machine is idle before any timing:** `tasklist` for `viewprobe.exe`, the game,
   MO2 and OBS (houseCARL's own rule). If something is running, wait or label the numbers as
   contended. A DevBench run in the lab counts as running: corpus reads compete with it for the
   X: drive.
5. **Never stop another session's processes;** ask first.
6. Normal repo rules: no commits without your say-so, and the version bump comes last.

## Where SkyRender changes

| Area | File(s) | nifly-touching lines | Notes |
|---|---|---|---|
| Loader | `Nif/NifFileLoader.cs` | 8 | `Load(Stream)` over the bytes, and the `swigCPtr` reflection hack goes away. Returns `LoadedNif`. Adds `TryReadBytes`. |
| Wrapper | new `Nif/LoadedNif.cs` | | Public, opaque, `IDisposable` so existing `using` lines still compile. The inner `NifFile` is `internal` (the Character tier sees it through `InternalsVisibleTo`). |
| Adapter | new `Nif/NifAccess.cs` | | Internal. See Phase 1. |
| Mesh building | `Nif/NifMeshBuilder.cs` | ~208 | Geometry, shaders, alpha, skinning, partitions, billboards, transforms. |
| LOD | `World/LodNifReader.cs` | ~30 | `.btr` / `.bto`; check `NifFileLoadOptions.IsTerrainFile`. |
| Diagnostics | `Nif/NifDiagnosticDumper*.cs`, `Nif/NifDumpApi.cs` | ~80 | Report unknown blocks by their real type from the header table, not `NiUnknown`. |
| Character tier | `CharacterPreviewCache.cs`, `VM_CharacterViewer.cs`, `AlternateTextureMatching.cs` | ~20 | Skeleton plumbing switches to `LoadedNif`. |
| Tests | `InMemoryBsaAssetTests.cs`, new core-only test project | 8 | |
| CI | `.github/workflows/*.yml` | | Ubuntu job. |
| Packaging and docs | `SkyRender.csproj`, `NOTICE.md`, `CHANGELOG.md`, `docs/USAGE.md` | | |

### The structural difference

The old library offered flattening helpers on `NifFile`, such as `GetVertsForShape`,
`GetUvsForShape`, `GetNormalsForShape`, `GetColorsForShape`, `GetTangentsForShape`,
`GetShapeBoneList`, `GetShapeBoneTransform`, `GetNodeTransformToGlobal`, `GetShapePartitions`,
`GetTexturePathByIndex` and `MatTransform.ComposeTransforms`.

The new library is a block model generated from nif.xml. Shapes expose raw vertex data
(`BSTriShape.VertexDataSSE` / `VertexData`, `NiTriShapeData` lists), and most of those read
helpers are missing (it has setters only). Shader fields become typed properties. For example,
`TextureClampMode`, `LightingInfluence` and `EnvMapMinLOD` are now separate bytes, so the packed
`textureClampMode` uint workaround in `NifMeshBuilder` gets deleted rather than ported.

Two quirks houseCARL already documented (`docs/architecture/nif.md`):
- Read alpha, shader and skin refs straight off `INiShape`, never through
  `GetPropertyOfType<T>`. That throws a NullReferenceException on SE shapes, whose legacy
  `Properties` list is null.
- Some `INiShader` accessors are default interface methods that return a constant. Use the
  concrete block's properties instead.

## Phase 0: baseline and the packaging check (on 3.1.1, before any change)

1. **Packaging check, in the scratchpad only.**
   - Build a throwaway 4.0 prerelease shell that depends on `Nifly` `1.1.0-0`. Pack it and
     record whether NU5104 appears.
   - Then build a scratch project shaped like viewprobe: a project reference using
     `Nifly 1.1.0-housecarl.4` (a **copy** of the vendored nupkg in a scratch feed), plus that
     prerelease, under a copy of houseCARL's source-mapping `nuget.config`. It must restore,
     build, and load `NiflySharp` once.
   - If this fails, stop and bring back options. Do not work around it inside houseCARL.
2. **Geometry snapshot harness.** This is a dev-only console tool (`tools/NifSnapshot`, not
   packed, not in CI). It runs `NifMeshBuilder.BuildFromFile` over the corpus and writes one
   canonical record per `BuiltMesh`: positions, normals, tangents, bitangents, UVs, colours,
   indices, texture paths per slot, every material scalar and flag, alpha state, skinning
   results, partition slots, billboard mode and frame, and warnings. Floats are written
   bit-exact, along with a hash per file.
3. **Corpus**, from the Skyrim Conflict Lab, read-only:
   - Every mesh in the SE game's BSAs (`Skyrim - Meshes0/1.bsa`, DLC and CC archives).
   - Every loose `.nif` / `.btr` / `.bto` in every lab mod folder, whether or not a profile
     enables it. This is a parse test, so the winning file doesn't matter. The run lists NSFW
     mod folders for you to clear or exclude before it starts.
   - A character set built with the skeleton: bodies, armours, hair, FaceGen heads with
     `BSDynamicTriShape`.

   Snapshots stay local, outside every repo.

   **Load-order runs** (CellDemo, NPC2) need a profile. The default is `DevBench-Minimal`, the
   one profile the lab keeps stable. A richer profile can be picked for wider coverage. These
   demos read a Data folder, not MO2's virtual file system. That means:
   - CellDemo runs against the SE game folder with a copied load order.
   - NPC2 is pointed at the lab through its own MO2 support, using an **isolated settings
     copy** so your normal NPC2 configuration isn't touched. Where NPC2 keeps its settings gets
     checked first.
4. **Render goldens:**
   - CellDemo: `--cell` / `--world --grid --eye --shot --exit` on a fixed list (Bannered Mare
     interior, Riverwood exterior with `--lod`, a grass-heavy cell, a water cell).
   - CharacterDemo: `--npc ... --require-outfit --shot --exit` on a fixed NPC list. **Clothed
     only**: `--require-outfit` skips any NPC without a body-slot outfit, so no unclothed render
     is captured.
5. **Performance baseline** (Phase 4), with the machine confirmed idle.

## Phase 1: wrapper and compatibility adapter

- **`LoadedNif`** and the new loader signatures:
  - `NifFileLoader.TryLoad` → `LoadedNif?`
  - `NifFileLoader.TryReadBytes`
  - `BuildFromFile(..., LoadedNif? skeletonNif, Func<LoadedNif?>? skeletonProvider)`
  - `BuildFromNif(LoadedNif)`
  - `NifDumpApi.DumpToString(LoadedNif)`
- **`NifAccess`** rebuilds the old helpers on the new block model. Each one is a direct port of
  the matching function in the C++ nifly `NifFile.cpp`, so the behaviour is the same by
  construction:
  - **Geometry per shape family:**
    - `BSTriShape` / `BSSubIndexTriShape` (SSE `VertexDataSSE`: half-float UVs, byte normals
      and tangents, byte colours).
    - `BSDynamicTriShape`: dynamic vertices replace the static positions. This matters for
      FaceGen heads.
    - `NiTriShape` / `NiTriShapeData`.
    - `NiTriStrips`: strips converted to triangles.
  - **Transforms:** to parent, composition, to global by node name, and applying one to a
    point. All of it in `System.Numerics`, which also removes today's per-vertex native
    `nifly.Vector3` allocations.
  - **Skin:** bone names, the inverse bind pose from `NiSkinData`, and per-triangle partition
    slots (via `NiSkinPartition.GenerateTriPartsFromTrueTriangles` / `PrepareTriParts`).
  - **Header:** type name by id, block count, `GetBlock<T>(ref)`.
- Before calling any generated member, build with `EmitCompilerGeneratedFiles=true` and read the
  generated sources. None of the generated names get guessed.
- **Adapter tests:** the test project temporarily references **both** packages. For every corpus
  file, each `NifAccess` helper must match the old helper's output. This catches decoding
  differences (half floats, byte-normal scaling, strip winding) before `NifMeshBuilder` changes.

## Phase 2: the swap

1. Change the package reference to `Nifly` with minimum `1.1.0-0`.
2. Port `NifMeshBuilder` section by section: survey, shapes, shader and effect material, alpha,
   transforms and billboards, skinning, physics XML paths, FaceGen detection. Then
   `LodNifReader`, the diagnostics, and the Character tier.
3. Delete the native-memory scaffolding (`using var` on SWIG vectors, `cMemoryOwn` comments).
   Map the typed shader-flag enums to the existing `uint` masks in one place.
4. Version 4.0.0, set at the very end. Update `CHANGELOG.md` with a migration section
   (`LoadedNif`, `TryReadBytes`, and what that means for direct `nifly` users), and update
   `NOTICE.md` and `docs/USAGE.md`.
5. Write a handoff note for houseCARL (viewprobe's two direct `nifly` uses, the
   dependency-range rationale). This is a note to act on later, not an edit. NPC2 gets its own
   migration session (see *NPC Plugin Chooser 2* above), not a note.

## Phase 3: correctness testing

| Check | How | Pass rule |
|---|---|---|
| Differential geometry | Rebuild the snapshots with the new code and diff them against Phase 0, file by file and field by field. Compare files individually, never just totals. | Indices, texture paths, flags and counts are exact. Positions are exact. Decoded normals, tangents, UVs and colours are within 1e-6. Every other difference is listed and explained: either a port bug (fix it) or an old-library bug now fixed (accept and note it, for example the `uvScale` case at `NifMeshBuilder.cs:2224`). |
| Fork parity | Run the full corpus on upstream `1.1.0` and on houseCARL's `1.1.0-housecarl.4` (a scratch copy). | Identical snapshots. Any file the fork refuses comes back as a clean null load, with no exception escaping. |
| Load coverage | Files that loaded before and fail now, and the reverse. Count `HasUnknownBlocks`. | Zero new failures, or each one explained. |
| Existing suite | `dotnet test` on Windows. | All green. |
| Rendered output | Rerun the Phase 0 scripted shots and pixel-diff them, using the `visual-test-designer` skill for masks and tolerances. Fixed weather and time, grass wind masked. Characters clothed only. | Within tolerance. Any region over tolerance gets traced to a geometry diff. |
| Concurrency | Parse the corpus in parallel (as `CharacterPreviewCache` does), plus `SceneLoadRaceTests`. | No exceptions, and snapshots identical to the single-threaded run. |
| Linux | A new core-only `net10.0` test project, run by an Ubuntu job in GitHub Actions. Fixtures are NIFs **authored in the test with Nifly itself**, so no game data reaches CI. | Parsing and mesh building pass on Ubuntu. GL rendering on Linux is out of scope. |
| Downstream | Restore and build NPC2 (in its scratch worktree) and the houseCARL-shaped scratch project against `4.0.0-nifly.N`. | Both build. NPC2 runs its NIF texture-detail path and the 3D viewer. |

## Phase 4: performance (NPC2 and CellDemo)

Every number is A/B: 3.1.1 against `4.0.0-nifly.N`, same machine, Release build, machine
confirmed idle. Runs alternate (A, B, A, B, ...), at least 5 each, reporting the median and
p95.

| Where | Scenario | Metrics |
|---|---|---|
| CellDemo | Scripted `--cell` / `--cycle --shot --exit` over the Phase 0 cell list, cold (first run after start) and warm. | Wall time to the first finished frame, parse vs build split (`ThreadParseMs`), peak working set, GC gen2 count. |
| NPC2 | NPC portrait / 3D viewer load for a fixed list of NPCs from the test profile, plus the `NifHandler` texture-detail pass. | `RenderTimings` (`ParseMs`, `BuildShapesMs`, `LoadMs`) per NPC, total batch time, peak working set. |
| Snapshot harness | The whole corpus, single-threaded and parallel. | Parse and build time per file by category (static, skinned, FaceGen, LOD), bytes allocated per file. For diagnosis only; not part of the budget. |

**Budget:** cell load and NPC load within +10% of 3.1.1.

**What to expect:** two effects work in opposite directions.
- The managed parser creates each block through reflection (`Type.GetType` +
  `Activator.CreateInstance`), so parsing alone may be slower.
- The old path made a native call per vertex for transforms, and that becomes plain C#, so mesh
  building should get faster.

**If over budget:** profile with `dotnet-trace`. Fix SkyRender-side costs locally. Offer
library-side fixes (for example, caching the block type lookup) upstream to ousnius/NiflySharp,
and to the houseCARL fork's owner, rather than patching a private copy.

## Risks to watch

- `BSDynamicTriShape` and FaceGen: if dynamic vertices are decoded wrong, heads come out wrong.
- SSE skinned partitions: the per-triangle slots drive dismemberment and occlusion
  (`PartitionOcclusionTests`).
- Skyrim LE (version 83) meshes and `NiTriStrips`, which use the older geometry path.
- `.btr` terrain LOD may need `IsTerrainFile = true`.
- Trimming or single-file publishing of a host app: the library resolves block types by name
  through reflection.
- The dependency-range trick depends on NuGet's prerelease ordering. Phase 0 proves it before
  anything relies on it.

## Phase 0 results

### Packaging check (2026-10-31, scratch, isolated `NUGET_PACKAGES`)

| Case | Stand-in SkyRender depends on | houseCARL-shaped project resolves | At runtime |
|---|---|---|---|
| Proposed | `Nifly >= 1.1.0-0` | `Nifly 1.1.0-housecarl.4` (the fork) | One `NiflySharp 1.1.0.0`, the fork: `NifLoadErrors` present. **Works.** |
| Control | `Nifly >= 1.1.0` | **`Nifly 1.1.0` (upstream), silently** | One `NiflySharp`, upstream: `NifLoadErrors` **missing**. |

The control case is worse than the restore failure the plan predicted. Source mapping limits
which feed `Nifly` is *downloaded* from, but a version already in the NuGet package cache is used
without asking any feed. Upstream `1.1.0` was in the cache, so the build:
- succeeded with no warning;
- replaced houseCARL's fork with upstream;
- dropped the fork's corruption bounds checks;
- would make any fork-only API call (such as `NifLoadErrors.IsBlockSizeMismatch`) throw
  `MissingMethodException` at runtime.

The fork-key constant only printed because the compiler inlined it.

**So the `1.1.0-0` minimum is required, not just tidy.**

Packing with it raises two warnings that SkyRender's csproj should suppress, with a comment
saying why:
- NU5104: a stable package with a prerelease dependency;
- NU1603: `1.1.0-0` doesn't exist, so `1.1.0` was resolved.

Neither is an error. nuget.org accepts the package.

### Snapshot harness

`tools/NifSnapshot` builds against 3.1.1. A smoke test of 300 character meshes from the SE
archives:
- 296 built, 4 came back empty, 322 meshes in total, about 1 second on 4 threads;
- two runs were identical on all 300 files (the output is deterministic).

Corpus output goes to `S:\Dev\_skyrender-nifly-port\`, outside every repo.

### Corpus baseline (3.1.1, niflysharp 1.3.0)

| | |
|---|---|
| Files | **41,740**: 36,333 from the 93 SE archives (vanilla, DLC, CC) and 5,407 loose files from all 201 lab mod folders (none excluded; no NSFW folders by name) |
| Meshes built | **250,377** |
| Outcome | 41,051 built, 689 empty (no renderable shape), **0 exceptions** |
| Determinism | two full runs, **41,740 of 41,740 files identical** |
| Time | 23 min (cold) and 15 min (warm), 8 threads at below-normal priority |
| Files | `baseline-3.1.1-run1.jsonl`, `-run2.jsonl`, `determinism.txt` |

Both runs went through `capture-when-idle.sh`. It reads houseCARL's `idle_lock.py status` every
15 seconds and stops the capture if houseCARL gets busy. houseCARL stayed idle throughout.

### Render and timing baseline (3.1.1)

Frozen builds are in `celldemo-3.1.1\` and `characterdemo-3.1.1\` under the port folder. The
later A/B runs alternate these against 4.0. The load order is a copy of the lab's
`DevBench-Minimal` (81 plugins: vanilla, DLC, CC; all present in the SE Data folder).

**CellDemo** (`celldemo-runs.ps1`, 5 runs, 1280×720):

| Scene | Wall median (s) | Read median (ms) | Read max (ms) | Peak memory (MB) |
|---|---|---|---|---|
| Bannered Mare (interior) | 11.92 | 1,421 | 1,761 | 1,577 |
| Dragonsreach (interior) | 13.26 | 1,788 | 1,978 | 1,769 |
| Riverwood (exterior, `--lod --ring`, SkyrimClear) | 22.5 | 7,172 | 7,298 | 2,632 |

**CharacterDemo** (5 runs, 800×1000, `--require-outfit`). All 7 NPCs passed the outfit check, so
every render is clothed.

| NPC | Load median (ms) | Models median (ms) | Models max (ms) | Upload median (ms) |
|---|---|---|---|---|
| Belethor (first; warms caches) | 890 | 85 | 92 | 380 |
| Hulda | 530 | 40 | 41 | 489 |
| Ysolda | 240 | 27 | 29 | 207 |
| Nazeem | 320 | 16 | 21 | 303 |
| Delphine | 280 | 14 | 16 | 270 |
| Balgruuf | 510 | 8 | 36 | 499 |
| Farengar | 300 | 21 | 30 | 277 |

"Models" is the NIF parse and mesh-build share: the part the port changes. It's small next to
texture upload, so the +10% budget applies to the total load time. The models column is there to
diagnose a regression, not to judge one: at 8 to 85 ms, a few milliseconds of noise looks like a
large percentage.

**Every screenshot is pixel-identical across its 5 runs**, for all 3 scenes and all 7 NPCs. The
Phase 3 render comparison can therefore start from an exact match and only needs a tolerance if
the port legitimately changes something.

NPC2's editing and rendering baselines belong to the NPC2 migration session.

## Phase 1 results (2026-10-31)

**What was added** (nothing is wired into rendering yet; the old library still does all the work):
- `src/SkyRender/Nif/LoadedNif.cs`: the public wrapper, not yet used by the API.
- `src/SkyRender/Nif/NifAccess.cs`: the adapter, internal.
- `SkyRender.csproj`: a temporary second package reference, `Nifly` with minimum `1.1.0-0`, and
  `InternalsVisibleTo NifSnapshot`.
- `NifSnapshot adapter`: a mode that runs every helper through both libraries.

The existing test suite still passes: 208 of 208.

**Adapter diff over the whole corpus:** **41,739 of 41,740 files are bit-identical** on every
helper:
- shape order, names and parent nodes;
- block types;
- vertices, normals, tangents, bitangents, UVs, colours and triangles;
- texture slots 0 to 8;
- shape-to-root transforms;
- bone lists, skin-to-bone transforms and bone world transforms;
- partition ids and per-triangle partitions.

**Differences between the libraries, and how the adapter handles each one:**

| What | Old (C++ via SWIG) | New (Nifly) | Adapter |
|---|---|---|---|
| Rotation matrices | rows are the 9 file floats in order | `NiNode.TransformToParent` builds the transpose (nif.xml names `Matrix33` column-major) | builds rows as `(M11, M21, M31)` and never uses Nifly's `TransformToParent` |
| Byte normals and tangents | `uint8_t` | `sbyte` fields | uses Nifly's getters, which cast to `byte` first, exactly as C++ does |
| Particle systems | not shapes (`NiAVObject`) | shapes (`NiParticles`) | `Shapes()` excludes `NiParticleSystem` |
| Texture paths | trimmed on load (`TrimTexturePaths`): slashes, a `textures\` prefix, nothing before it | stored as written (trimming is commented out) | `TrimTexturePath` ports the regexes, including MSVC's multi-line `^` (two CC files have newlines inside a path) |
| `BSTriShape` triangles | `GetTriangles` always returns true | n/a | returns true for every `BSTriShape` |
| SSE skinned shapes | vertices and triangles moved in from the skin partition at load; dynamic positions copied in | the same move (`NifFile.PrepareData`) | nothing to do |
| Per-triangle partitions | `skinPart->triParts` | `NiSkinPartition.triParts` is internal | computed from public data (see below) |

**Accepted differences:**
- `meshes\actors\character\character assets\hair\hairshorthumanfold.nif` is an unskinned
  `BSDynamicTriShape` with no vertex data. C++ built 643 vertices at the origin with every index
  0, which draws nothing. Nifly reports no vertices, so no mesh is built. The visible result is
  the same. Phase 3's mesh diff will show it as one fewer mesh.
- NaN UVs (9 files, from half floats) have different NaN bit patterns. Any NaN renders the
  same, so the adapter diff treats them as equal. Phase 3's mesh hashes will flag these 9 files,
  already explained.

**Not covered by the adapter:** shader, effect and alpha property fields. `NifMeshBuilder` reads
those directly off the blocks. They move in Phase 2 and are checked by Phase 3's full
`BuiltMesh` diff.

**Performance note for Phase 4:** Nifly's `BSTriShape` getters (`Normals`, `UVs` and so on)
build a new list on every call. `NifMeshBuilder` should call each one once per shape.

### Notes from the NPC2 migration session (2026-10-31)

1. **`triParts` without reflection** (done). For an SSE `BSTriShape`, Nifly's `PrepareData`
   builds the triangles by concatenating each partition's public `TrianglesCopy`, so the list is
   partition *i* repeated `TrianglesCopy.Count` times. Every other shape gets the
   rotate-and-match pass from `GenerateTriPartsFromTrueTriangles`. `NifAccess.TriParts` now
   does both from public data.
2. **Effect-shader paths are never trimmed** in C++: the `BSEffectShaderProperty` branch of
   `TrimTexturePaths` sits inside `if (textureSet)`. `NifAccess.TexturePath` already returns
   effect fields as written. The C++ build niflysharp wraps (SteveTownsend/nifly `38bfceef`)
   has no Oblivion or 10.0.1.0 exceptions in its trimming; SkyRender reads Skyrim only.
3. **Phase 2 shader traps.** Read shader values off the *concrete* block, never through
   `INiShader`:
   - `INiShader.TextureSetRef` and the shader type and flags are default interface members that
     return null or 0.
   - The SK/FO4 shader type lives on `NiObjectNET` (streams 83 to 139).
   - Flags1 is `ShaderFlags_SSPF1` or `ShaderFlags_F4SPF1`, chosen by layout.
   - `NiMaterialProperty` is not an `INiShader`.
   - Read alpha, shader and skin refs straight off `INiShape`, never through
     `GetPropertyOfType<T>` (houseCARL `nif.md`).
4. **`Load` can throw**, because `PrepareData` runs outside Nifly's own try/catch.
   `NifAccess.Load` already catches exceptions as well as checking the return code and `Valid`.
5. **Phase 4 hot spots:**
   - `NiSkinPartition.Sync` is about half of Nifly's load time for skinned meshes; FaceGen heads
     are mostly partitions.
   - `Type.GetType` runs for every block whose type isn't in the attribute map.
   - Profile these before blaming SkyRender code.
6. **Write-side bugs that NPC2 works around:** `RemoveBlock` deleting sibling blocks, NaN tangents
   from `CalcTangentSpace`, and bounding spheres that miss vertices. SkyRender never writes NIFs,
   so none of these apply here.
