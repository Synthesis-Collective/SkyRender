using System.Diagnostics;
using OpenTK.Mathematics;

namespace SkyRender.Viewing;

/// <summary>A mouse button as the view's controls name it.</summary>
public enum ViewButton { Left, Middle, Right }

/// <summary>A fly key: along the view, sideways level with the ground, up and down, and faster.</summary>
public enum FlyKey { Forward, Back, Left, Right, Up, Down, Fast }

/// <summary>
/// Mouse and fly controls for an <see cref="OrbitCamera"/>, the same in every host: left-drag
/// orbits, middle-drag pans, right-drag looks around from where the eye stands, the wheel zooms,
/// and held fly keys move the eye <b>by the clock</b>: a distance for the time since the last
/// frame, in every frame while a key is down.
///
/// <para>Moving once per key-repeat message instead ties flying to the keyboard's repeat rate
/// (about 30 steps a second after a half-second delay) whatever the frame rate, which reads as
/// stutter. So this class keeps the set of keys that are down (<see cref="KeyDown"/> /
/// <see cref="KeyUp"/>) and the host calls <see cref="Advance"/> at the start of each frame.</para>
///
/// <para>No UI types: a host translates its own events into these calls. Not thread-safe; call it
/// from the thread that draws.</para>
/// </summary>
public sealed class ViewCameraControls
{
    private readonly HashSet<FlyKey> _held = new();
    private readonly Func<double> _clockSeconds;
    private double _flyAt;
    private bool _looking;
    private float _lookX, _lookY;

    /// <param name="camera">The camera the controls move.</param>
    /// <param name="clockSeconds">A monotonic clock in seconds; the default is a stopwatch.</param>
    public ViewCameraControls(OrbitCamera camera, Func<double>? clockSeconds = null)
    {
        Camera = camera;
        if (clockSeconds is null)
        {
            var watch = Stopwatch.StartNew();
            clockSeconds = () => watch.Elapsed.TotalSeconds;
        }
        _clockSeconds = clockSeconds;
    }

    public OrbitCamera Camera { get; set; }

    /// <summary>Fly speed in world units a second.</summary>
    public float FlySpeed { get; set; } = 1500f;

    /// <summary>How many times faster the eye flies while <see cref="FlyKey.Fast"/> is held.</summary>
    public float FastMultiplier { get; set; } = 4f;

    /// <summary>Right-drag look, in degrees a unit of pointer movement.</summary>
    public float LookDegreesPerUnit { get; set; } = 0.25f;

    /// <summary>The longest time one <see cref="Advance"/> moves for, so a stalled frame does not
    /// throw the eye across the scene.</summary>
    public double MaxStepSeconds { get; set; } = 0.1;

    /// <summary>True while a movement key is down: the view needs a frame at every refresh.</summary>
    public bool IsFlying => _held.Any(k => k != FlyKey.Fast);

    /// <summary>True while a drag is under way.</summary>
    public bool IsDragging { get; private set; }

    public void PointerDown(ViewButton button, float x, float y)
    {
        IsDragging = true;
        if (button == ViewButton.Right)
        {
            _looking = true;
            _lookX = x;
            _lookY = y;
        }
        else
        {
            _looking = false;
            Camera.OnMouseDown(x, y, button == ViewButton.Left, button == ViewButton.Middle);
        }
    }

    /// <summary>The pointer moved. <paramref name="viewportHeight"/> is the view's height in the
    /// units of <paramref name="x"/> and <paramref name="y"/>, so a pan tracks the cursor.</summary>
    /// <returns>True when the camera moved.</returns>
    public bool PointerMove(float x, float y, float viewportHeight)
    {
        if (!IsDragging) return false;
        if (_looking)
        {
            // Look around: turn the view about the eye (the orbit's target moves, the eye stays).
            var eye = Camera.GetEyePosition();
            Camera.Azimuth -= (x - _lookX) * LookDegreesPerUnit;
            Camera.Elevation = Math.Clamp(Camera.Elevation + (y - _lookY) * LookDegreesPerUnit, -89f, 89f);
            Camera.Target = eye - (Camera.GetEyePosition() - Camera.Target);
            _lookX = x;
            _lookY = y;
            return true;
        }
        Camera.OnMouseMove(x, y, viewportHeight);
        return true;
    }

    public void PointerUp(ViewButton button)
    {
        if (button == ViewButton.Right) _looking = false;
        else Camera.OnMouseUp();
        IsDragging = false;
    }

    public void Wheel(float delta) => Camera.OnMouseWheel(delta);

    /// <summary>A fly key went down. A repeat of a key already down changes nothing.</summary>
    public void KeyDown(FlyKey key)
    {
        bool wasFlying = IsFlying;
        _held.Add(key);
        if (!wasFlying && IsFlying) _flyAt = _clockSeconds();   // the move starts now, not at the last frame
    }

    public void KeyUp(FlyKey key) => _held.Remove(key);

    /// <summary>Forget every held key and drag: the view lost the focus, so the releases will not come.</summary>
    public void ReleaseAll()
    {
        _held.Clear();
        _looking = false;
        IsDragging = false;
        Camera.OnMouseUp();
    }

    /// <summary>Move the eye for the time since the last move, for the fly keys held. Call once at
    /// the start of each frame.</summary>
    /// <returns>True when the camera moved.</returns>
    public bool Advance()
    {
        if (!IsFlying) return false;
        double now = _clockSeconds();
        float dt = (float)Math.Clamp(now - _flyAt, 0, MaxStepSeconds);
        _flyAt = now;
        var forward = Vector3.Normalize(Camera.Target - Camera.GetEyePosition());
        float speed = FlySpeed * (_held.Contains(FlyKey.Fast) ? FastMultiplier : 1f);
        var move = FlyMove(_held, forward, speed * dt);
        if (move == Vector3.Zero) return false;
        Camera.Target += move;                                   // the eye rides with the target
        return true;
    }

    /// <summary>The eye's move for the keys held: along the view, sideways level with the ground
    /// (Y is up), and straight up or down. Opposite keys cancel; a diagonal is no faster.</summary>
    public static Vector3 FlyMove(IReadOnlySet<FlyKey> held, Vector3 forward, float distance)
    {
        var right = Vector3.Cross(forward, Vector3.UnitY);
        right = right.LengthSquared < 1e-6f ? Vector3.UnitX : Vector3.Normalize(right);
        var m = Vector3.Zero;
        if (held.Contains(FlyKey.Forward)) m += forward;
        if (held.Contains(FlyKey.Back)) m -= forward;
        if (held.Contains(FlyKey.Right)) m += right;
        if (held.Contains(FlyKey.Left)) m -= right;
        if (held.Contains(FlyKey.Up)) m += Vector3.UnitY;
        if (held.Contains(FlyKey.Down)) m -= Vector3.UnitY;
        return m.LengthSquared < 1e-6f ? Vector3.Zero : Vector3.Normalize(m) * distance;
    }
}
