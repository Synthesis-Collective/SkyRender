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

    /// <summary>True when the run is scripted: no message boxes.</summary>
    public bool Automated => Shot != null || Exit;

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
                default: CellLog.Instance.Info("Ignoring unknown argument " + args[i]); break;
            }
        }
        return o;
    }
}
