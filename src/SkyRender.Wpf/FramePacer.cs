using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace SkyRender.Wpf;

/// <summary>What a native view's frames are timed by.</summary>
internal enum PacerSource
{
    /// <summary>Each desktop composition (<c>DwmFlush</c>): frames stay in step with the display.</summary>
    Compositor,
    /// <summary>The pacer's own clock at the display's refresh period, because the compositor is
    /// not composing at that rate.</summary>
    Clock,
}

/// <summary>
/// Paces a native view's frames. A child window's <c>SwapBuffers</c> does not wait for the
/// vertical blank under the desktop compositor, so the wait happens here, on a background thread,
/// which then posts one frame to the UI thread at <see cref="DispatcherPriority.Render"/>. That
/// priority is above input, so a stream of mouse messages cannot hold frames back. (Frames queued
/// below input, at Background priority, were measured waiting 50 to 300 ms in a scene that draws
/// in under a millisecond.) Nothing sleeps on the UI thread, nothing is posted while no frame is
/// wanted, and one frame is in flight at a time.
///
/// <para><b>What it waits for.</b> While the compositor composes at the display's rate, each
/// frame waits for a composition (<c>DwmFlush</c>), which keeps frames in step with the display.
/// The compositor does not always do that: on a desktop whose compositor has gone idle or slow
/// (measured: one composition in three seconds, <c>DwmFlush</c> returning every 46.6 ms on a
/// 60 Hz display), a view that waits for it runs at 21 frames a second whatever it draws. So when
/// two compositions in a row come more than a period and a half apart, the pacer keeps time itself
/// with a high-resolution timer at the display's refresh period, and goes back to the compositor
/// once that has kept the rate for half a second of frames.</para>
/// </summary>
internal sealed class FramePacer : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly Action _frame;
    private readonly Func<double> _nowMs;
    private readonly PacerSource? _forced;
    private readonly AutoResetEvent _wake = new(false), _composed = new(false), _flushWake = new(false);
    private readonly Thread _thread, _flushThread;
    private volatile bool _want, _inFlight, _stop;
    private volatile int _onTime, _slow;          // compositions in a row that kept, or missed, the display's rate
    private long _watchUntil;                     // the compositor is watched until this Stopwatch timestamp
    private IntPtr _timer;
    private double _periodMs = 1000.0 / 60;

    /// <summary>When the frame now being drawn was posted to the UI thread.</summary>
    public double PostedAtMs { get; private set; } = double.NaN;

    /// <summary>What frames are timed by at the moment.</summary>
    public PacerSource Source { get; private set; } = PacerSource.Compositor;

    /// <param name="forced">Diagnostic: keep to one source whatever the compositor does.</param>
    public FramePacer(Dispatcher ui, Action frame, Func<double> nowMs, PacerSource? forced = null)
    {
        _ui = ui;
        _frame = frame;
        _nowMs = nowMs;
        _forced = forced;
        if (forced is { } f) Source = f;
        if (CompositorTiming.Read() is { RefreshPeriodMs: > 2 and < 100 } timing) _periodMs = timing.RefreshPeriodMs;
        _timer = CreateWaitableTimerEx(IntPtr.Zero, null, 0x00000002 /* CREATE_WAITABLE_TIMER_HIGH_RESOLUTION */, 0x1F0003);
        _thread = new Thread(Loop) { IsBackground = true, Name = "SkyRender frame pacer", Priority = ThreadPriority.AboveNormal };
        _flushThread = new Thread(FlushLoop) { IsBackground = true, Name = "SkyRender compositor watch", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _flushThread.Start();
    }

    /// <summary>Ask for a frame at the next beat; many asks before it is drawn make one frame.</summary>
    public void Request()
    {
        _want = true;
        _wake.Set();
    }

    private void Loop()
    {
        var clock = Stopwatch.StartNew();
        double next = 0;
        bool idle = true;
        while (!_stop)
        {
            if (!_want || _inFlight)
            {
                if (!_want && !_inFlight) idle = true;
                _wake.WaitOne(50);
                continue;
            }
            // Keep the compositor watched while frames are wanted, and for half a second after, so
            // an on-demand view that parks between mouse messages does not restart the watch each time.
            Volatile.Write(ref _watchUntil, Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2);
            _flushWake.Set();
            if (_forced is null)
            {
                if (Source == PacerSource.Compositor && _slow >= 2)
                {
                    Source = PacerSource.Clock;
                    next = clock.Elapsed.TotalMilliseconds;
                }
                else if (Source == PacerSource.Clock && _onTime >= 30) Source = PacerSource.Compositor;
            }
            if (Source == PacerSource.Compositor)
            {
                if (idle) _composed.Reset();                    // a composition signalled before the pause is not this frame's
                WaitForComposition();
            }
            else ClockWait(clock, ref next);
            idle = false;
            if (_stop || !_want) continue;
            // _want stays set until the frame runs (Draw clears it): an ask that comes after the
            // post but before the frame is drawn is the same frame.
            _inFlight = true;
            PostedAtMs = _nowMs();
            try { _ui.BeginInvoke(DispatcherPriority.Render, Draw); }
            catch (InvalidOperationException) { return; }       // the dispatcher has shut down
        }
    }

    /// <summary>Watches the compositor while frames are wanted: signals each composition and counts
    /// how many in a row kept the display's rate, and how many in a row did not.</summary>
    private void FlushLoop()
    {
        var clock = Stopwatch.StartNew();
        double last = -1;
        while (!_stop)
        {
            if (Stopwatch.GetTimestamp() > Volatile.Read(ref _watchUntil))
            {
                last = -1;
                _onTime = 0;
                _slow = 0;
                _flushWake.WaitOne(200);
                continue;
            }
            if (DwmFlush() != 0)                                 // no compositor to wait for
            {
                _onTime = 0;
                _slow = 2;
                Thread.Sleep(100);
                continue;
            }
            double now = clock.Elapsed.TotalMilliseconds;
            if (last >= 0)
            {
                if (now - last < _periodMs * 1.5) { _onTime++; _slow = 0; }
                else { _slow++; _onTime = 0; }
            }
            last = now;
            _composed.Set();
        }
    }

    /// <summary>Wait for the next composition, but for a period and a quarter at most: now and then
    /// a composition comes two or three periods late (measured: 43 to 57 ms on a healthy 60 Hz
    /// desktop, a few times in 1,400 frames), and a frame drawn a little off the beat is better
    /// than one that late.</summary>
    private void WaitForComposition()
    {
        long due = -(long)(_periodMs * 1.25 * 10_000);          // relative, in 100 ns units
        if (_timer != IntPtr.Zero && SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
        {
            _waitHandles[0] = _composed.SafeWaitHandle.DangerousGetHandle();
            _waitHandles[1] = _timer;
            WaitForMultipleObjects(2, _waitHandles, false, 200);
            _composed.Reset();
        }
        else _composed.WaitOne((int)Math.Ceiling(_periodMs * 3));
    }

    private readonly IntPtr[] _waitHandles = new IntPtr[2];

    /// <summary>Wait for the next beat of the pacer's own clock, with a high-resolution waitable
    /// timer (the process's timer resolution is left as it is).</summary>
    private void ClockWait(Stopwatch clock, ref double next)
    {
        double now = clock.Elapsed.TotalMilliseconds;
        if (next < now - _periodMs) next = now;                 // resumed after a pause: start a new beat
        double wait = next - now;
        if (wait > 0.05)
        {
            long due = -(long)(wait * 10_000);                  // relative, in 100 ns units
            if (_timer != IntPtr.Zero && SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) WaitForSingleObject(_timer, 200);
            else Thread.Sleep((int)Math.Ceiling(wait));
        }
        next += _periodMs;
    }

    private void Draw()
    {
        _want = false;                                          // an ask from inside the frame is the next one
        try { _frame(); }
        finally
        {
            _inFlight = false;
            _wake.Set();
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _flushWake.Set();
        _thread.Join(500);
        _flushThread.Join(500);
        if (_timer != IntPtr.Zero) CloseHandle(_timer);
        _timer = IntPtr.Zero;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll")] private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr routine, IntPtr arg, bool resume);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles, bool waitAll, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
