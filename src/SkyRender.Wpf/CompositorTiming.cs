using System.Runtime.InteropServices;

namespace SkyRender.Wpf;

/// <summary>
/// The desktop compositor's own counters (<c>DwmGetCompositionTimingInfo</c>): the display's
/// refresh period, how many refreshes have passed and how many frames the compositor has
/// composed. Read it before and after a stretch of frames to see whether the display kept its
/// rate while the view did not, which tells a late view from a slow desktop.
/// </summary>
public readonly record struct CompositorTiming(double RefreshHz, double RefreshPeriodMs, ulong Refreshes, ulong ComposedFrames)
{
    /// <summary>The counters now, or null when the compositor does not answer.</summary>
    public static CompositorTiming? Read()
    {
        // DWM_TIMING_INFO is 292 bytes, packed to 1: cbSize 0, rateRefresh 4 (numerator, denominator),
        // qpcRefreshPeriod 12, cRefresh 36, cFrame 56.
        var buffer = new byte[292];
        BitConverter.TryWriteBytes(buffer, (uint)buffer.Length);
        if (DwmGetCompositionTimingInfo(IntPtr.Zero, buffer) != 0) return null;
        uint numerator = BitConverter.ToUInt32(buffer, 4), denominator = BitConverter.ToUInt32(buffer, 8);
        ulong period = BitConverter.ToUInt64(buffer, 12);
        return new CompositorTiming(denominator == 0 ? 0 : (double)numerator / denominator,
            period * 1000.0 / System.Diagnostics.Stopwatch.Frequency, BitConverter.ToUInt64(buffer, 36), BitConverter.ToUInt64(buffer, 56));
    }

    /// <summary>
    /// The median time between desktop compositions over <paramref name="count"/> of them, in
    /// milliseconds (it blocks for that long; call it off the UI thread). On a healthy desktop this
    /// is the display's refresh period. Much more means the compositor is not composing at the
    /// display's rate, and anything paced by it (WPF's frame tick, <c>DwmFlush</c>) is slow with it.
    /// Null when there is no compositor to wait for.
    /// </summary>
    public static double? MeasureCompositionMs(int count = 30)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var gaps = new List<double>();
        double last = -1;
        for (int i = 0; i <= count; i++)
        {
            if (DwmFlush() != 0) return null;
            double now = watch.Elapsed.TotalMilliseconds;
            if (last >= 0) gaps.Add(now - last);
            last = now;
        }
        gaps.Sort();
        return gaps[gaps.Count / 2];
    }

    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, byte[] timingInfo);
}
