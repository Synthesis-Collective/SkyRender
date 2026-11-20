namespace SkyRender.Viewing;

/// <summary>What one scripted input event does.</summary>
public enum MotionEventKind { RightDown, Move, RightUp, KeyDown, KeyUp }

/// <summary>One scripted input event of a <see cref="ViewMotion"/>, at its time from the motion's start.
/// X and Y are offsets from the view's centre in device pixels.</summary>
public readonly record struct MotionEvent(double AtMs, MotionEventKind Kind, int X, int Y, FlyKey Key, bool Repeat);

/// <summary>
/// A scripted motion for measuring a live view with no hand on the mouse: a steady right-drag
/// look, or a held fly key. A host turns the events into real window messages, so they take the
/// path a hand's input takes. That matters: a view can be smooth when a timer moves the camera
/// and jittery only while mouse messages arrive.
/// </summary>
public sealed record ViewMotion(string Kind, double Seconds, double PixelsPerSecond, double EventsPerSecond, FlyKey Key)
{
    /// <summary>Hold the right button and move the pointer steadily sideways.</summary>
    /// <param name="pixelsPerSecond">Pointer speed in device pixels a second.</param>
    /// <param name="eventsPerSecond">Mouse-move events a second (a common mouse reports 125 to 1000).</param>
    public static ViewMotion Look(double seconds, double pixelsPerSecond, double eventsPerSecond = 250) =>
        new("look", seconds, pixelsPerSecond, eventsPerSecond, FlyKey.Forward);

    /// <summary>Hold a fly key down, repeating as a keyboard repeats it.</summary>
    public static ViewMotion Fly(double seconds, FlyKey key = FlyKey.Right) => new("fly", seconds, 0, 0, key);

    /// <summary>The events, in time order. <paramref name="repeatDelayMs"/> and
    /// <paramref name="repeatsPerSecond"/> are the keyboard's repeat delay and rate (a held key).</summary>
    public IReadOnlyList<MotionEvent> Events(double repeatDelayMs = 500, double repeatsPerSecond = 30)
    {
        var o = new List<MotionEvent>();
        double total = Seconds * 1000;
        if (Kind == "look")
        {
            o.Add(new(0, MotionEventKind.RightDown, 0, 0, default, false));
            double pxPerMs = PixelsPerSecond / 1000.0, step = 1000.0 / EventsPerSecond;
            int n = (int)Math.Floor(total / step);
            for (int i = 1; i <= n; i++)
                o.Add(new(i * step, MotionEventKind.Move, (int)Math.Round(i * step * pxPerMs), 0, default, false));
            o.Add(new(total, MotionEventKind.RightUp, (int)Math.Round(total * pxPerMs), 0, default, false));
        }
        else
        {
            o.Add(new(0, MotionEventKind.KeyDown, 0, 0, Key, false));
            for (double t = repeatDelayMs; t < total; t += 1000.0 / repeatsPerSecond)
                o.Add(new(t, MotionEventKind.KeyDown, 0, 0, Key, true));
            o.Add(new(total, MotionEventKind.KeyUp, 0, 0, Key, false));
        }
        return o;
    }
}
