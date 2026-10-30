# SkyRender.CharacterDemo

A WPF viewer that draws any NPC of the installed game with SkyRender.Character: pick an NPC from
the list and they are read out of the load order and drawn with their default outfit, under the
character tier's studio lighting (`RenderProfile.Character`). It is read-only: it never writes to
the game folder or the plugins.

## Requirements

- Windows, an OpenGL 3.3 capable GPU, the .NET 10 SDK.
- Skyrim Special Edition (or Anniversary Edition) installed. The game folder and the load order
  (`plugins.txt`) are found through Mutagen's game detection; if that fails, use **Browse…** to pick
  the game's `Data` folder.
- Mutagen (`Mutagen.Bethesda`, `Mutagen.Bethesda.Skyrim` 0.54.2) reads the plugins and archives.
  It is a dependency of this sample only: the SkyRender packages do not use it.

```bash
dotnet run --project samples/SkyRender.CharacterDemo -c Release
```

## Using it

- The list on the left holds every NPC_ record of the load order, once each, from its winning copy:
  EditorID and name, then FormKey and the winning plugin. The search box filters by any of them.
- Under the list: the selected NPC, the plugin that wins it, and its default outfit.
- The status line gives the load time (records, models, GPU upload); the log pane (and the log file
  under `%TEMP%\SkyRender.CharacterDemo`) gives how each part was found and what was missing.

Mouse: left-drag orbits, middle-drag pans, the wheel zooms. Click the view, then Home reframes it.

Picking another NPC cancels the load still running. The records are read on a worker thread, the
models are parsed on a worker thread by `VM_CharacterViewer.LoadAsync`, and the GPU upload goes in a
slice per frame; the previous NPC's meshes and textures are freed when the next one goes in.

Optional arguments for scripted checks (screenshots, switching tests) are listed in
[StartupOptions.cs](StartupOptions.cs), for example:

```bash
SkyRender.CharacterDemo.exe --npc Belethor --shot belethor.png --exit
SkyRender.CharacterDemo.exe --npc Irileth --cycle "Belethor;Risaad" --describe --exit
```

## How an NPC is put together

[NpcResolver.cs](NpcResolver.cs) is the whole data adapter, an `INpcMeshDataSource`. It reads the
records with Mutagen and hands SkyRender.Character plain paths (`ResolvedNpcMeshPaths` for the
character, a `MeshOverride` per outfit piece):

- **Templates.** With the *Traits* template flag the race, gender, skin, head parts, colours, weight,
  height and FaceGen come from the template; with *Inventory*, the default outfit. Chains are
  followed to the NPC that supplies the value.
- **Skin and body.** The NPC's worn armor (WNAM), else the race's skin. Its armor addons for the
  NPC's race (or the race's armor race) and gender give the body, hands, feet, tail and hair models,
  with their skin texture sets.
- **Head.** The FaceGen head the Creation Kit exports,
  `meshes\actors\character\facegendata\facegeom\<plugin>\<formid>.nif`, and its tint texture. The
  FaceGen NIF holds the head parts; their records only tell the renderer which shapes are eyes.
- **Outfit.** The default outfit (DOFT): every armor, with one mesh override per armor addon for the
  NPC's race and gender. Outfit pieces hide what is under them (body, hands, feet), and hoods and
  helmets hide the hair, as in the game.

## What it does not draw

- **Leveled lists are not rolled.** A leveled item in the outfit gives its first armor (every
  entry's, when the list has *Use All*); a leveled NPC template gives its first NPC. The game picks
  at random, so an NPC that wears or is drawn from a leveled list can look different in game.
- **Only the default outfit.** The NPC's other inventory, items equipped by scripts, distributed
  at runtime or carried in the hands (weapons) are not drawn.
- **Vanilla shapes only.** Meshes are drawn as they ship, at the NPC's weight; no body-shape
  mods, morphs or physics.
- **No animation.** The NPC stands in the models' reference pose.
- No FaceGen, no head: an NPC whose FaceGen NIF is missing (generic NPCs drawn from leveled
  lists often have none) is drawn without one.
