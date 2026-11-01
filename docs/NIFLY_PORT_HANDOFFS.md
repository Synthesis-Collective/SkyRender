# Upgrading a host to SkyRender 4.0 (Nifly): notes for houseCARL and NPC2

SkyRender 4.0 reads NIFs with ousnius's managed `Nifly` package instead of `niflysharp`.
CHANGELOG.md lists the API changes. This note covers what the two known hosts need to do when
they move their pins. Nothing here is urgent: a host pinned to 3.x is unaffected until it bumps.

## houseCARL

Current state (read-only survey, 2026-10-31):
- `src/housecarl-core` references `Nifly 1.1.0-housecarl.4` (the fork, from `packages/local`,
  with source mapping restricting `Nifly` to that feed).
- `_geometry-harness/viewprobe` references `housecarl-core` and `SkyRender.Character` 3.0.1.
- viewprobe calls `nifly.NifFile` directly in two places: `TexturedScene.cs:4172` and
  `UnreadableAssets.cs:288`.

When viewprobe moves to SkyRender 4.0:

1. **Package resolution works as it is.** SkyRender 4.0 asks for `Nifly >= 1.1.0-0`, which
   `1.1.0-housecarl.4` satisfies, so restore keeps the fork. There is one `NiflySharp 1.1.0.0`
   at run time, and it's the fork. Checked in a scratch copy of houseCARL's `nuget.config` and
   project shapes: SkyRender's plan, "Phase 0 results". Don't add an upstream `Nifly` reference;
   with a plain `>= 1.1.0` NuGet would silently swap the fork out.
2. **viewprobe's two direct `nifly` uses stop compiling**, because SkyRender no longer brings
   `niflysharp` in. Either:
   - port them to `NiflySharp` (the fork is already referenced through housecarl-core), or
   - add an explicit `niflysharp` 1.3.0 reference to viewprobe. Both libraries can coexist:
     different assemblies (`nifly.dll`, `NiflySharp.dll`) and namespaces.
3. **API renames**, if viewprobe uses any of these: `NifFileLoader.TryLoad` returns `LoadedNif`;
   `BuildFromFile`'s skeleton parameters take `LoadedNif`; for raw bytes behind a resolved
   (possibly in-memory) path, use `NifFileLoader.TryReadBytes`.
4. **Fork behaviour:** the fork throws `InvalidDataException` on a corrupt count. SkyRender
   catches every load exception and treats the file as unreadable (`TryLoad` returns null), so
   a malformed mesh renders as missing rather than crashing.

## NPC Plugin Chooser 2

The NPC2 migration session owns this move. The SkyRender side:
- `BackEnd/NifHandler.cs:112` called `NifFileLoader.TryLoad` for a `nifly.NifFile`. On 4.0, call
  `NifFileLoader.TryReadBytes(path, resolver)` and load the bytes with `NiflySharp.NifFile`.
- Everything NPC2 builds through `NifMeshBuilder` or the character tier keeps working; the
  skeleton plumbing uses `LoadedNif`.
