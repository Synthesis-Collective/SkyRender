using System.Windows.Threading;
using FluentAssertions;
using SkyRender.Viewing;
using SkyRender.Wpf;
using Xunit;

namespace SkyRender.Tests;

/// <summary>The WPF view host's parts that need no window: the scripted input's window messages
/// and the frame pacer's contract with the UI thread.</summary>
public class ViewHostTests
{
    [Fact]
    public void A_look_event_becomes_the_message_a_right_drag_sends()
    {
        var down = MotionPlayer.ToMessage(new MotionEvent(0, MotionEventKind.RightDown, 0, 0, default, false), 800, 450);
        var move = MotionPlayer.ToMessage(new MotionEvent(12, MotionEventKind.Move, 30, 0, default, false), 800, 450);
        var up = MotionPlayer.ToMessage(new MotionEvent(20, MotionEventKind.RightUp, 60, 0, default, false), 800, 450);

        down.Should().Be(new MotionPlayer.TimedMessage(0, 0x0204, (IntPtr)0x2, (IntPtr)((450 << 16) | 800)));    // WM_RBUTTONDOWN, MK_RBUTTON
        move.Should().Be(new MotionPlayer.TimedMessage(12, 0x0200, (IntPtr)0x2, (IntPtr)((450 << 16) | 830)));   // WM_MOUSEMOVE with the button held
        up.Should().Be(new MotionPlayer.TimedMessage(20, 0x0205, IntPtr.Zero, (IntPtr)((450 << 16) | 860)));     // WM_RBUTTONUP
    }

    [Fact]
    public void A_fly_event_becomes_key_messages_with_the_repeat_and_release_bits()
    {
        var first = MotionPlayer.ToMessage(new MotionEvent(0, MotionEventKind.KeyDown, 0, 0, FlyKey.Right, false), 0, 0);
        var repeat = MotionPlayer.ToMessage(new MotionEvent(500, MotionEventKind.KeyDown, 0, 0, FlyKey.Right, true), 0, 0);
        var release = MotionPlayer.ToMessage(new MotionEvent(900, MotionEventKind.KeyUp, 0, 0, FlyKey.Right, false), 0, 0);

        first.Should().Be(new MotionPlayer.TimedMessage(0, 0x0100, (IntPtr)'D', (IntPtr)1L));                    // WM_KEYDOWN, repeat count 1
        repeat.LParam.Should().Be((IntPtr)(1L | 1L << 30));                                                      // the key was already down
        release.Msg.Should().Be(0x0101);                                                                         // WM_KEYUP
        release.LParam.Should().Be((IntPtr)(1L | 1L << 30 | 1L << 31));
        MotionPlayer.ToMessage(new MotionEvent(0, MotionEventKind.KeyDown, 0, 0, FlyKey.Fast, false), 0, 0).WParam.Should().Be((IntPtr)0x10);   // VK_SHIFT
    }

    /// <summary>Runs <paramref name="body"/> against a pacer whose frames are posted to a dispatcher
    /// thread of the test's own.</summary>
    private static void WithPacer(Action frame, Action<FramePacer> body)
    {
        Dispatcher? dispatcher = null;
        using var started = new ManualResetEventSlim();
        var ui = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            started.Set();
            Dispatcher.Run();
        }) { IsBackground = true };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        started.Wait();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // The clock source: the test does not depend on a desktop compositor being there.
        var pacer = new FramePacer(dispatcher!, frame, () => watch.Elapsed.TotalMilliseconds, PacerSource.Clock);
        try { body(pacer); }
        finally
        {
            pacer.Dispose();
            dispatcher!.InvokeShutdown();
        }
    }

    [Fact]
    public void The_pacer_draws_one_frame_for_many_requests_and_none_unasked()
    {
        int frames = 0;
        using var drawn = new ManualResetEventSlim();
        WithPacer(() => { Interlocked.Increment(ref frames); drawn.Set(); }, pacer =>
        {
            Thread.Sleep(150);
            frames.Should().Be(0, "nothing is posted while no frame is wanted");

            for (int i = 0; i < 20; i++) pacer.Request();
            drawn.Wait(2000).Should().BeTrue();
            Thread.Sleep(150);

            frames.Should().Be(1, "many requests before a frame is drawn make one frame");
            pacer.Source.Should().Be(PacerSource.Clock);
        });
    }

    [Fact]
    public void The_pacer_keeps_one_frame_in_flight_and_draws_again_when_asked_from_a_frame()
    {
        int frames = 0, inFlight = 0, overlaps = 0;
        FramePacer? self = null;
        using var done = new ManualResetEventSlim();
        WithPacer(() =>
        {
            if (Interlocked.Increment(ref inFlight) > 1) Interlocked.Increment(ref overlaps);
            Thread.Sleep(5);
            if (Interlocked.Increment(ref frames) < 10) self!.Request();     // a view that still wants frames asks from inside its frame
            else done.Set();
            Interlocked.Decrement(ref inFlight);
        }, pacer =>
        {
            self = pacer;
            pacer.Request();
            done.Wait(5000).Should().BeTrue();
            Thread.Sleep(100);

            frames.Should().Be(10);
            overlaps.Should().Be(0);
        });
    }
}
