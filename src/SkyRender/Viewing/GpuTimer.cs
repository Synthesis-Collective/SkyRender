using OpenTK.Graphics.OpenGL4;

namespace SkyRender.Viewing;

/// <summary>
/// GPU time of each frame's draw, from two GL timestamp queries read back a few frames later so
/// the read never waits on the GPU. NaN while a result is not in, or where the driver has no timer
/// queries. Call with the view's GL context current.
/// </summary>
public sealed class GpuTimer
{
    private const int Depth = 4;
    private readonly int[] _q = new int[Depth * 2];
    private readonly bool[] _issued = new bool[Depth];
    private int _slot;
    private bool _ok, _tried;

    /// <summary>Mark the start of the frame's draw.</summary>
    public void Begin()
    {
        if (!_tried)
        {
            _tried = true;
            GL.GetQuery(QueryTarget.Timestamp, GetQueryParam.QueryCounterBits, out int bits);
            _ok = bits > 0 && GL.GetError() == ErrorCode.NoError;
            if (_ok) GL.GenQueries(_q.Length, _q);
        }
        if (_ok) GL.QueryCounter(_q[_slot * 2], QueryCounterTarget.Timestamp);
    }

    /// <summary>Mark the draw's end and return the GPU time of the oldest frame still in flight,
    /// if it has arrived.</summary>
    public double End()
    {
        if (!_ok) return double.NaN;
        GL.QueryCounter(_q[_slot * 2 + 1], QueryCounterTarget.Timestamp);
        _issued[_slot] = true;
        _slot = (_slot + 1) % Depth;
        if (!_issued[_slot]) return double.NaN;                     // the oldest slot is the one about to be reused
        GL.GetQueryObject(_q[_slot * 2 + 1], GetQueryObjectParam.QueryResultAvailable, out int ready);
        if (ready == 0) { _issued[_slot] = false; return double.NaN; }
        GL.GetQueryObject(_q[_slot * 2], GetQueryObjectParam.QueryResult, out long a);
        GL.GetQueryObject(_q[_slot * 2 + 1], GetQueryObjectParam.QueryResult, out long b);
        _issued[_slot] = false;
        return (b - a) / 1e6;
    }
}
