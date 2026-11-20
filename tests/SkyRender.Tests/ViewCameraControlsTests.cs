using FluentAssertions;
using OpenTK.Mathematics;
using SkyRender.Viewing;
using Xunit;

namespace SkyRender.Tests;

/// <summary>The view's camera controls: flying by the clock, looking about the eye, and the
/// scripted motions' timetables.</summary>
public class ViewCameraControlsTests
{
    /// <summary>A camera at the origin's side, looking level along -Z, with a clock the test turns.</summary>
    private static (ViewCameraControls Controls, OrbitCamera Camera, Action<double> Tick) Make()
    {
        var camera = new OrbitCamera { Azimuth = 0, Elevation = 0, Distance = 100, Target = Vector3.Zero };   // eye at +Z 100
        double now = 10;
        var controls = new ViewCameraControls(camera, () => now) { FlySpeed = 1000, FastMultiplier = 4 };
        return (controls, camera, dt => now += dt);
    }

    [Fact]
    public void A_held_key_moves_the_eye_by_elapsed_time_whatever_the_frame_rate()
    {
        // The same half second, drawn as 30 frames and as 5: the eye ends in the same place.
        var (smooth, smoothCamera, smoothTick) = Make();
        smooth.KeyDown(FlyKey.Forward);
        for (int i = 0; i < 30; i++) { smoothTick(0.5 / 30); smooth.Advance().Should().BeTrue(); }

        var (coarse, coarseCamera, coarseTick) = Make();
        coarse.KeyDown(FlyKey.Forward);
        for (int i = 0; i < 5; i++) { coarseTick(0.1); coarse.Advance(); }

        smoothCamera.Target.Z.Should().BeApproximately(-500f, 0.05f);        // 1,000 units a second for 0.5 s, along -Z
        coarseCamera.Target.Z.Should().BeApproximately(-500f, 0.05f);
        smoothCamera.GetEyePosition().Z.Should().BeApproximately(-400f, 0.05f);   // the eye rides with the target
    }

    [Fact]
    public void Key_repeats_do_not_move_the_eye_or_restart_the_clock()
    {
        var (controls, camera, tick) = Make();
        controls.KeyDown(FlyKey.Forward);
        tick(0.05);
        controls.KeyDown(FlyKey.Forward);            // a keyboard repeat
        camera.Target.Should().Be(Vector3.Zero);     // nothing moves between frames
        tick(0.05);
        controls.Advance();

        camera.Target.Z.Should().BeApproximately(-100f, 0.01f);   // the whole 0.1 s since the key went down
    }

    [Fact]
    public void A_stalled_frame_moves_the_eye_for_a_tenth_of_a_second_at_most()
    {
        var (controls, camera, tick) = Make();
        controls.KeyDown(FlyKey.Forward);
        tick(3.0);
        controls.Advance();

        camera.Target.Z.Should().BeApproximately(-100f, 0.01f);
    }

    [Fact]
    public void The_fast_key_multiplies_the_speed_and_does_not_fly_on_its_own()
    {
        var (controls, camera, tick) = Make();
        controls.KeyDown(FlyKey.Fast);
        controls.IsFlying.Should().BeFalse();
        tick(0.05);
        controls.Advance().Should().BeFalse();

        controls.KeyDown(FlyKey.Right);
        controls.IsFlying.Should().BeTrue();
        tick(0.05);
        controls.Advance();

        camera.Target.X.Should().BeApproximately(200f, 0.01f);    // right of a camera looking along -Z is +X; 4,000 a second for 0.05 s
        controls.KeyUp(FlyKey.Right);
        controls.IsFlying.Should().BeFalse();
    }

    [Fact]
    public void Fly_moves_are_level_sideways_straight_up_and_no_faster_on_a_diagonal()
    {
        var forward = Vector3.Normalize(new Vector3(0, -1, -1));      // looking down at 45 degrees

        ViewCameraControls.FlyMove(new HashSet<FlyKey> { FlyKey.Right }, forward, 10).Y.Should().Be(0);
        ViewCameraControls.FlyMove(new HashSet<FlyKey> { FlyKey.Up }, forward, 10).Should().Be(new Vector3(0, 10, 0));
        ViewCameraControls.FlyMove(new HashSet<FlyKey> { FlyKey.Forward, FlyKey.Right }, forward, 10).Length.Should().BeApproximately(10f, 1e-4f);
        ViewCameraControls.FlyMove(new HashSet<FlyKey> { FlyKey.Forward, FlyKey.Back }, forward, 10).Should().Be(Vector3.Zero);
        // Looking straight down, "right" still has a direction.
        ViewCameraControls.FlyMove(new HashSet<FlyKey> { FlyKey.Right }, -Vector3.UnitY, 10).Length.Should().BeApproximately(10f, 1e-4f);
    }

    [Fact]
    public void A_right_drag_turns_the_view_about_the_eye()
    {
        var (controls, camera, _) = Make();
        var eye = camera.GetEyePosition();

        controls.PointerDown(ViewButton.Right, 100, 100);
        controls.PointerMove(140, 80, 900).Should().BeTrue();          // 40 right, 20 up, at 0.25 degrees a unit
        controls.PointerUp(ViewButton.Right);

        camera.Azimuth.Should().BeApproximately(-10f, 1e-4f);
        camera.Elevation.Should().BeApproximately(-5f, 1e-4f);
        (camera.GetEyePosition() - eye).Length.Should().BeLessThan(1e-3f);   // the eye stayed; the target swung
        controls.PointerMove(300, 300, 900).Should().BeFalse();        // no drag, no move
        camera.Azimuth.Should().BeApproximately(-10f, 1e-4f);
    }

    [Fact]
    public void Losing_the_focus_drops_held_keys_and_drags()
    {
        var (controls, camera, tick) = Make();
        controls.KeyDown(FlyKey.Forward);
        controls.PointerDown(ViewButton.Left, 0, 0);

        controls.ReleaseAll();
        tick(0.05);

        controls.IsFlying.Should().BeFalse();
        controls.IsDragging.Should().BeFalse();
        controls.Advance().Should().BeFalse();
        camera.Target.Should().Be(Vector3.Zero);
    }

    [Fact]
    public void A_look_motion_is_a_right_drag_at_a_steady_speed()
    {
        var events = ViewMotion.Look(seconds: 2, pixelsPerSecond: 180, eventsPerSecond: 250).Events();

        events.Should().HaveCount(502);                                 // down, 500 moves, up
        events[0].Should().Be(new MotionEvent(0, MotionEventKind.RightDown, 0, 0, default, false));
        events[1].Kind.Should().Be(MotionEventKind.Move);
        events[1].AtMs.Should().Be(4);
        events[250].X.Should().Be(180);                                 // one second in
        events[^1].Should().Be(new MotionEvent(2000, MotionEventKind.RightUp, 360, 0, default, false));
        events.Select(e => e.AtMs).Should().BeInAscendingOrder();
    }

    [Fact]
    public void A_fly_motion_holds_the_key_as_a_keyboard_repeats_it()
    {
        var events = ViewMotion.Fly(seconds: 1, FlyKey.Right).Events(repeatDelayMs: 500, repeatsPerSecond: 20);

        events[0].Should().Be(new MotionEvent(0, MotionEventKind.KeyDown, 0, 0, FlyKey.Right, false));
        events.Skip(1).Take(events.Count - 2).Should().OnlyContain(e => e.Kind == MotionEventKind.KeyDown && e.Repeat);
        events.Skip(1).Take(events.Count - 2).Select(e => e.AtMs).Should().Equal(500, 550, 600, 650, 700, 750, 800, 850, 900, 950);
        events[^1].Should().Be(new MotionEvent(1000, MotionEventKind.KeyUp, 0, 0, FlyKey.Right, false));
    }
}
