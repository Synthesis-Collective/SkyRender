# SkyRender.CellDemo

A WPF viewer that draws any cell of the installed game with SkyRender: pick an interior, or a
worldspace and an exterior cell, and it is read out of the load order and drawn at full detail.
It is read-only: it never writes to the game folder or the plugins.

## Requirements

- Windows, an OpenGL 3.3 capable GPU, the .NET 10 SDK.
- Skyrim Special Edition (or Anniversary Edition) installed. The game folder and the load order
  (`plugins.txt`) are found through Mutagen's game detection; if that fails, use **Browse…** to pick
  the game's `Data` folder.
- Mutagen (`Mutagen.Bethesda`, `Mutagen.Bethesda.Skyrim` 0.54.2) reads the plugins and archives.
  It is a dependency of this sample only: the SkyRender packages do not use it.

```bash
dotnet run --project samples/SkyRender.CellDemo -c Release
```

## Using it

- **Interiors** tab: every interior cell, filtered by EditorID or name as you type.
- **Exteriors** tab: choose the worldspace, then the cell; the search box takes an EditorID, a name
  or grid coordinates (`5,-11`).
- Under the lists: the selected cell and the plugin that wins it (the last one in the load order
  with a copy of the cell record).
- **Distant LOD** (off by default): the worldspace's terrain, object, tree and water LOD beyond the
  loaded cells, picked for the starting view. It adds several seconds to a load.
- **Neighbouring cells** (off by default): the eight cells around an exterior, as the game loads
  around the player.
- The status line gives the load time; the log pane (and the log file under
  `%TEMP%\SkyRender.CellDemo`) gives each stage's timing and what was left out.

Mouse: left-drag orbits, right-drag looks around, middle-drag pans, the wheel zooms. Click the
view, then W A S D fly, Q / E go down and up, Shift is faster, Home resets the view. An exterior
starts in a three-quarter view of the cell; an interior starts where `coc` would put the player
(its `COCMarkerHeading`), or just inside a load door looking into the room.

Picking another cell cancels the load still running. A load runs on a worker thread (the records,
model parsing, archive extraction); the GL upload then goes in a slice per frame, and the previous
cell's meshes and textures are freed first.

Optional arguments for scripted checks (screenshots, switching tests, map tiles) are listed in
[StartupOptions.cs](StartupOptions.cs), for example:

```bash
SkyRender.CellDemo.exe --cell WhiterunBanneredMare --shot mare.png --exit
SkyRender.CellDemo.exe --world Tamriel --grid 4,-12 --ring --lod --eye 15000,-47000,10000 --yaw 76 --pitch 68 --shot riverwood.png --exit
SkyRender.CellDemo.exe --world WhiterunWorld --grid 5,-2 --ring --tile market.png --tile-rect 20480,-12288,28672,-4096
```

The 3D view is SkyRender's own view host, `SkyRender.Wpf.SkyRenderView`: it paces the frames,
moves the camera (the mouse and fly controls above, flying by the clock) and draws only while
something changed. The line above the view is its readout: frames a second, the median frame
time, the mean of the slowest 1%, the worst, and the render call, over the last two seconds; it
reads "idle" while nothing moves.

`--host native` (the default) hosts the view in a native child window; `--host wpf` hosts it in a
texture shared with WPF (`GLWpfControl`). `--continuous` draws at every refresh.

For frame times with no hand on the mouse:

```bash
SkyRender.CellDemo.exe --world Tamriel --grid 30,20 --orbit 20 --fps-log frames.csv --exit
SkyRender.CellDemo.exe --world WhiterunWorld --grid 5,-2 --ring --size 1600x900 --host native --bench out
```

The first turns the camera about its target for 20 seconds, writes one row per frame
(`SkyRender.Viewing.FrameStats.CsvHeader` names the columns) and closes. The second brings the
window to the front and logs four steps from the same starting view: still, a scripted orbit, a
right-drag look and a held-key fly. The look and the fly are posted as real mouse and key messages,
so they take the path a hand's input takes ([BenchRun.cs](BenchRun.cs)). It writes `out/look.csv`
and the rest, and one row per step in `out/bench.tsv`. Keep your hands off the mouse and keyboard
while it runs.

The last example above opens no window. It draws a top-down map tile of the rectangle (world units: west,
south, east, north) at 16 units a pixel over a transparent background, and writes `market.txt`
with the time of the first tile and of the same tile once the scene is loaded
([TileRun.cs](TileRun.cs)).

## What it draws

- **Placed objects** (REFR) of the cell: every copy of the cell in the load order contributes its
  references, a later copy of a reference replaces an earlier one, and a deleted one is removed. An
  exterior adds the worldspace's persistent references that stand in the loaded cells. Statics,
  furniture, doors, containers, trees, flora, clutter and lights' models, with their NIF materials
  (`NifMaterialBinder`): textures, specular, glow, cube and 2D environment maps, alpha, effect
  shaders (fire, smoke, light shafts) and billboards. Placement follows the engine
  (`ReferencePlacement`). Snow and similar single-pass coverings (STAT material objects) draw as
  SkyRender's projected material.
- **Terrain** (the winning LAND, with its texture layers, normals and vertex colours), **water**
  (each cell's own height and water type, else the worldspace's) and **grass** (the landscape
  textures' GRAS, placed by the engine's rules).
- **Exterior light**: the most likely weather of the cell's region, else of the worldspace's
  climate, else SkyrimClear; its day colours, fog, sky and image space; the sun where the climate
  puts it at noon, casting shadows.
- **Interior light**: the cell's lighting (XCLL, with what it inherits from its lighting template),
  its image space, and its placed lights as point lights (the 32 nearest the camera; shadow-casting
  lights cast shadows).
- **Distant LOD** on request (`DistantLodLoader`).

## What it does not draw

- **NPCs and creatures** (ACHR references), and anything animated: the scene is static.
- References that start disabled, or whose enable parent (when it is in the cell) hides them;
  editor markers.
- References a later plugin moves to another cell are drawn where the cell's own copies put them
  (the demo does not scan every cell of the load order for moves).
- Interior water, sky and clouds, the weather's transitions and other times of day, eye adaptation,
  bloom, point lights in exteriors, placed lights' flicker and spot cones.
- Distant LOD is picked once, for the starting view; it does not follow the camera.
- Grass uses the engine's default `[Grass]` settings, not the user's INI.
