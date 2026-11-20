using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Media;
using OpenTK.Graphics.OpenGL4;
using SkyRender.Viewing;

namespace SkyRender.CellDemo;

/// <summary>
/// <c>--bench DIR</c>: frame times of the 3D view with no hand on the mouse, the same run for
/// either host (<c>--host native|wpf</c>), so the two can be compared on one scene.
///
/// <para>Once the cell is drawn the window comes to the front, a short look and fly are played
/// unlogged (so first-use costs are not counted), and then four steps are logged, each from the
/// same starting view: <b>still</b> (5 s, nothing moves), <b>orbit</b> (the camera turns by the
/// clock, as <c>--orbit</c> does), <b>look</b> (the right button held and the mouse moved steadily,
/// 45 degrees a second, 250 mouse messages a second) and <b>fly</b> (the D key held, repeating at
/// the keyboard's own rate). Look and fly are posted as real window messages
/// (<see cref="SkyRender.Wpf.SkyRenderView.PlayMotion"/>), so they take the path a hand's input
/// takes; a view can be smooth under a timer and jittery only while mouse messages arrive.</para>
///
/// <para>Each step writes <c>STEP.csv</c> (one row per frame) and one row of <c>bench.tsv</c>.</para>
/// </summary>
public partial class MainWindow
{
    private const string BenchHeader =
        "tag\thost\tstep\tframes\tseconds\tmedian_ms\tlow1_ms\tworst_ms\tover25\tover50\tstart_median_ms\tstart_low1_ms\tstart_worst_ms\tstart_over25\t" +
        "foreground\tazimuth_change\teye_moved\trefresh_hz\trefreshes\tcomposed\tcomposition_ms\tview\tgl_renderer\tnote";

    private bool _benchStarted;
    private TaskCompletionSource? _benchOrbit;

    private bool IsForeground => GetForegroundWindow() == new WindowInteropHelper(this).Handle;

    private async Task RunBenchAsync(int width, int height)
    {
        var inv = CultureInfo.InvariantCulture;
        try
        {
            string dir = _options.Bench!;
            Directory.CreateDirectory(dir);
            double seconds = _options.BenchSeconds;
            // 45 degrees a second of look, as pointer speed in device pixels.
            double lookPixels = 45.0 / _view.Controls.LookDegreesPerUnit * VisualTreeHelper.GetDpi(this).DpiScaleX;
            string glRenderer = GL.GetString(StringName.Renderer) + " / " + GL.GetString(StringName.Version);

            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
            await Task.Delay(500);
            _view.FocusView();
            // The desktop compositor's own pace just before the run: the display's refresh period when it
            // is healthy. Rows measured while it is not are marked by this column.
            double? compositionMs = await Task.Run(() => SkyRender.Wpf.CompositorTiming.MeasureCompositionMs(30));
            _log.Info(string.Create(inv, $"Bench: the desktop compositor composes every {compositionMs:0.0} ms (median of 30)."));
            _log.Info($"Bench: host {_view.Stats.HostLabel}, view {width} x {height} px, window in the foreground: {IsForeground}.");

            // Warm-up, unlogged.
            await Motion(ViewMotion.Look(1, lookPixels));
            await Motion(ViewMotion.Fly(1));

            var rows = new List<string>();
            async Task Step(string name, Func<Task<string>> run)
            {
                if (_scene is { } scene) FrameCamera(scene.Scene.Data);    // every step starts from the same view
                await Task.Delay(400);
                _view.FocusView();
                bool foregroundBefore = IsForeground;
                float azimuth = _camera.Azimuth;
                var eye = _camera.GetEyePosition();
                var before = SkyRender.Wpf.CompositorTiming.Read();
                _view.StartFrameLog(Path.Combine(dir, name + ".csv"));
                string note = await run();
                await Task.Delay(50);
                var r = _view.StopFrameLog()!.Value;
                var after = SkyRender.Wpf.CompositorTiming.Read();
                string compositor = before is { } b && after is { } a ? string.Create(inv, $"{a.RefreshHz:0.##}\t{a.Refreshes - b.Refreshes}\t{a.ComposedFrames - b.ComposedFrames}\t{compositionMs:0.0}") : "\t\t\t";
                _log.Info($"Bench {name}: {r.Present.Words()}. {note}");
                rows.Add(string.Create(inv,
                    $"{_options.BenchTag}\t{_view.Stats.HostLabel}\t{name}\t{r.Present.Count}\t{r.Present.TotalMs / 1000:0.00}\t{r.Present.MedianMs:0.00}\t{r.Present.Low1Ms:0.00}\t{r.Present.WorstMs:0.00}\t{r.Present.Over25}\t{r.Present.Over50}\t" +
                    $"{r.Start.MedianMs:0.00}\t{r.Start.Low1Ms:0.00}\t{r.Start.WorstMs:0.00}\t{r.Start.Over25}\t" +
                    $"{foregroundBefore}/{IsForeground}\t{_camera.Azimuth - azimuth:0.0}\t{(_camera.GetEyePosition() - eye).Length:0}\t{compositor}\t{width}x{height}\t{glRenderer}\t{note}"));
            }

            await Step("still", async () => { await Task.Delay(5000); return ""; });
            await Step("orbit", async () => { await OrbitAsync(seconds); return ""; });
            await Step("look", () => Motion(ViewMotion.Look(seconds, lookPixels)));
            await Step("fly", () => Motion(ViewMotion.Fly(seconds)));

            string table = Path.Combine(dir, "bench.tsv");
            if (!File.Exists(table)) File.WriteAllText(table, BenchHeader + Environment.NewLine);
            File.AppendAllLines(table, rows);
        }
        catch (Exception ex)
        {
            _log.LogError("The bench run failed", ex);
        }
        Close();
    }

    private Task<string> Motion(ViewMotion motion)
    {
        var done = new TaskCompletionSource<string>();
        _view.PlayMotion(motion, said => done.TrySetResult(said));
        return done.Task;
    }

    /// <summary>The <c>--orbit</c> turn, for the bench: by the clock, at <c>--orbit-speed</c>.</summary>
    private Task OrbitAsync(double seconds)
    {
        _benchOrbit = new TaskCompletionSource();
        _orbitSeconds = seconds;
        _orbitStartAzimuth = _camera.Azimuth;
        _orbitStartSeconds = _frameClock.Elapsed.TotalSeconds;
        _view.Invalidate("orbit");
        return _benchOrbit.Task;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
