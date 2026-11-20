using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SkyRender.CellDemo;

/// <summary>
/// Optional command-line arguments. With none the demo detects the game and waits for a pick. They
/// exist for scripted checks: open a cell straight away, set the camera, save a screenshot and quit.
/// <code>
/// --data DIR            the game's Data folder (instead of detecting it)
/// --load-order FILE     a loadorder.txt / plugins.txt to use instead of the game's
/// --cell EDITORID       open this interior, or exterior cell, once the game data has loaded
/// --world EDITORID      with --grid: the worldspace of an exterior
/// --grid X,Y            with --world: the exterior cell at these grid coordinates
/// --lod, --ring         turn the two toggles on
/// --weather EDITORID    exteriors: this weather instead of the region's or climate's
/// --eye X,Y,Z           camera position (Skyrim units, Z up), with --yaw / --pitch in degrees
///                       (yaw 0 = north, clockwise; pitch positive looks down) and --fov (vertical)
/// --size WxH            window client size of the 3D view
/// --shot FILE.png       save the 3D view once the cell is drawn (and, with --cycle, after each one)
/// --cycle A;B;C         after the first cell, open these in turn (the same forms as --cell, or
///                       World@X,Y; a leading ! picks the next one while that one is still
///                       loading): a switching test
/// --exit                close once the last screenshot is saved
/// --orbit SECONDS       once the cell is drawn, turn the camera steadily about its target for this
///                       long (no hand on the mouse), then carry on (--shot, --cycle, --exit)
/// --orbit-speed DEG     degrees a second for --orbit (default 20)
/// --fps-log FILE.csv    write one row per frame of the --orbit (or, without one, of the whole run);
///                       SkyRender.Viewing.FrameStats.CsvHeader names the columns
/// --host native|wpf     how the 3D view is hosted (SkyRender.Wpf.ViewHostMode): a native child window
///                       paced to the desktop compositor, or a texture shared with WPF (GLWpfControl).
///                       Default: SkyRenderView.DefaultMode
/// --continuous          draw at every refresh instead of only when something changed
/// --bench DIR           a frame-time run with no hand on the mouse: once the cell is drawn, bring the
///                       window to the front and log four motions to DIR (still.csv, orbit.csv, look.csv,
///                       fly.csv, bench.tsv), then close. Look and fly are posted as real mouse and key
///                       messages (BenchRun.cs)
/// --bench-seconds N     seconds of each moving step (default 8; still is 5)
/// --bench-tag TEXT      a label for this run's rows in bench.tsv
/// --tile FILE.png       no window: render a top-down map tile (TopDownCamera) of --world / --grid
///                       through the offscreen renderer, cold and then warm, and write the picture
///                       and FILE.txt with the timings. --ring loads the eight cells around it too
/// --tile-rect W,S,E,N   the tile's rectangle in world units (default: the --grid cell)
/// --tile-scale N        world units a pixel (default 16)
/// --tile-height F,C     lowest and highest height drawn (default -40000,60000)
/// --tile-repeat N       warm renders to time (default 10)
/// --tile-opaque         a grey background instead of a transparent one
/// --tile-no-grass       leave the grass out
/// </code>
/// </summary>
internal sealed class StartupOptions
{
    public static StartupOptions Current { get; set; } = new();

    public string? DataFolder { get; private set; }
    public string? LoadOrderFile { get; private set; }
    public string? Cell { get; private set; }
    public string? World { get; private set; }
    public (int X, int Y)? Grid { get; private set; }
    public string? Weather { get; private set; }
    public bool Lod { get; private set; }
    public bool Ring { get; private set; }
    public System.Numerics.Vector3? Eye { get; private set; }
    public float Yaw { get; private set; }
    public float Pitch { get; private set; }
    public float? Fov { get; private set; }
    public (int W, int H)? Size { get; private set; }
    public string? Shot { get; private set; }
    public List<string> Cycle { get; } = new();
    public bool Exit { get; private set; }
    public double? Orbit { get; private set; }
    public float OrbitSpeed { get; private set; } = 20f;
    public string? FpsLog { get; private set; }
    public SkyRender.Wpf.ViewHostMode? Host { get; private set; }
    public bool Continuous { get; private set; }
    public string? Bench { get; private set; }
    public double BenchSeconds { get; private set; } = 8;
    public string BenchTag { get; private set; } = "";
    public string? Tile { get; private set; }
    public (float West, float South, float East, float North)? TileRect { get; private set; }
    public float TileScale { get; private set; } = 16f;
    public (float Floor, float Ceiling)? TileHeight { get; private set; }
    public int TileRepeat { get; private set; } = 10;
    public bool TileOpaque { get; private set; }
    public bool TileNoGrass { get; private set; }

    /// <summary>True when the run is scripted: no message boxes.</summary>
    public bool Automated => Shot != null || Exit || Tile != null || Orbit != null || Bench != null;

    public static StartupOptions Parse(string[] args)
    {
        var o = new StartupOptions();
        var inv = CultureInfo.InvariantCulture;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("missing value after " + args[i]);
            switch (args[i].ToLowerInvariant())
            {
                case "--data": o.DataFolder = Next(); break;
                case "--load-order": o.LoadOrderFile = Next(); break;
                case "--cell": o.Cell = Next(); break;
                case "--world": o.World = Next(); break;
                case "--grid":
                    var g = Next().Split(',');
                    o.Grid = (int.Parse(g[0], inv), int.Parse(g[1], inv));
                    break;
                case "--weather": o.Weather = Next(); break;
                case "--lod": o.Lod = true; break;
                case "--ring": o.Ring = true; break;
                case "--eye":
                    var e = Next().Split(',');
                    o.Eye = new System.Numerics.Vector3(float.Parse(e[0], inv), float.Parse(e[1], inv), float.Parse(e[2], inv));
                    break;
                case "--yaw": o.Yaw = float.Parse(Next(), inv); break;
                case "--pitch": o.Pitch = float.Parse(Next(), inv); break;
                case "--fov": o.Fov = float.Parse(Next(), inv); break;
                case "--size":
                    var s = Next().ToLowerInvariant().Split('x');
                    o.Size = (int.Parse(s[0], inv), int.Parse(s[1], inv));
                    break;
                case "--shot": o.Shot = Path.GetFullPath(Next()); break;
                case "--cycle": o.Cycle.AddRange(Next().Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                case "--exit": o.Exit = true; break;
                case "--orbit": o.Orbit = double.Parse(Next(), inv); break;
                case "--orbit-speed": o.OrbitSpeed = float.Parse(Next(), inv); break;
                case "--fps-log": o.FpsLog = Path.GetFullPath(Next()); break;
                case "--host":
                    o.Host = Next().ToLowerInvariant() switch
                    {
                        "native" => SkyRender.Wpf.ViewHostMode.NativeWindow,
                        "wpf" => SkyRender.Wpf.ViewHostMode.WpfTexture,
                        var other => throw new ArgumentException("--host takes native or wpf, not " + other),
                    };
                    break;
                case "--continuous": o.Continuous = true; break;
                case "--bench": o.Bench = Path.GetFullPath(Next()); break;
                case "--bench-seconds": o.BenchSeconds = double.Parse(Next(), inv); break;
                case "--bench-tag": o.BenchTag = Next(); break;
                case "--tile": o.Tile = Path.GetFullPath(Next()); break;
                case "--tile-rect":
                    var tr = Next().Split(',');
                    o.TileRect = (float.Parse(tr[0], inv), float.Parse(tr[1], inv), float.Parse(tr[2], inv), float.Parse(tr[3], inv));
                    break;
                case "--tile-scale": o.TileScale = float.Parse(Next(), inv); break;
                case "--tile-height":
                    var th = Next().Split(',');
                    o.TileHeight = (float.Parse(th[0], inv), float.Parse(th[1], inv));
                    break;
                case "--tile-repeat": o.TileRepeat = int.Parse(Next(), inv); break;
                case "--tile-opaque": o.TileOpaque = true; break;
                case "--tile-no-grass": o.TileNoGrass = true; break;
                default: CellLog.Instance.Info("Ignoring unknown argument " + args[i]); break;
            }
        }
        return o;
    }
}
