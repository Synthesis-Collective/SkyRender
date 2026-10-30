using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SkyRender.CharacterDemo;

/// <summary>
/// Optional command-line arguments. With none the demo detects the game and waits for a pick. They
/// exist for scripted checks: open an NPC straight away, save a screenshot and quit.
/// <code>
/// --data DIR            the game's Data folder (instead of detecting it)
/// --load-order FILE     a loadorder.txt / plugins.txt to use instead of the game's
/// --npc ID              open this NPC once the game data has loaded (EditorID or FormKey,
///                       for example Belethor or 013BA1:Skyrim.esm)
/// --cycle A;B;C         after the first NPC, open these in turn (a leading ! picks the next
///                       one 200 ms later, while that one is still loading): a switching test
/// --interrupt-ms N      the delay after a ! entry before the next NPC is picked (default 200)
/// --describe            log how each NPC resolves (skin, head, outfit pieces) without drawing it
/// --require-outfit      only save a screenshot when the NPC's outfit has a body-slot (32) piece
///                       and every outfit mesh was found; otherwise log why and skip it
/// --portrait            frame the head instead of the whole figure
/// --size WxH            window client size of the 3D view
/// --shot FILE.png       save the 3D view once the NPC is drawn (and, with --cycle, after each)
/// --exit                close once the last NPC is done
/// </code>
/// </summary>
internal sealed class StartupOptions
{
    public static StartupOptions Current { get; set; } = new();

    public string? DataFolder { get; private set; }
    public string? LoadOrderFile { get; private set; }
    public string? Npc { get; private set; }
    public List<string> Cycle { get; } = new();
    public int InterruptMs { get; private set; } = 200;
    public bool Describe { get; private set; }
    public bool RequireOutfit { get; private set; }
    public bool Portrait { get; private set; }
    public (int W, int H)? Size { get; private set; }
    public string? Shot { get; private set; }
    public bool Exit { get; private set; }

    /// <summary>True when the run is scripted: no message boxes.</summary>
    public bool Automated => Shot != null || Exit || Describe;

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
                case "--npc": o.Npc = Next(); break;
                case "--cycle": o.Cycle.AddRange(Next().Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                case "--interrupt-ms": o.InterruptMs = int.Parse(Next(), inv); break;
                case "--describe": o.Describe = true; break;
                case "--require-outfit": o.RequireOutfit = true; break;
                case "--portrait": o.Portrait = true; break;
                case "--size":
                    var s = Next().ToLowerInvariant().Split('x');
                    o.Size = (int.Parse(s[0], inv), int.Parse(s[1], inv));
                    break;
                case "--shot": o.Shot = Path.GetFullPath(Next()); break;
                case "--exit": o.Exit = true; break;
                default: DemoLog.Instance.Info("Ignoring unknown argument " + args[i]); break;
            }
        }
        return o;
    }
}
