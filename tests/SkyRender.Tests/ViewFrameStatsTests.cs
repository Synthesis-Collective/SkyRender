using System.IO;
using FluentAssertions;
using SkyRender.Viewing;
using Xunit;

namespace SkyRender.Tests;

/// <summary>The live view's frame-time maths and log (SkyRender.Viewing.FrameStats), with known answers.</summary>
public class ViewFrameStatsTests
{
    private static FrameSample Sample(long frame, double presentMs, double intervalMs, bool resumed = false, double reasonAgeMs = 0,
        double presentCallMs = 0.02, double gpuMs = 0.5) =>
        new(frame, presentMs - 1, presentMs, intervalMs, intervalMs, 0.8, presentCallMs, gpuMs, resumed, "input", reasonAgeMs, 0.2, 3,
            0, 0, 0, 10f, 20f, 1f, 2f, 3f, 1600, 900);

    [Fact]
    public void Summary_of_a_steady_run_with_two_hitches_names_them()
    {
        // 198 frames at 16.7 ms, one at 30 and one at 60: the median stays at the display's period,
        // and the 1% low (the slowest 2 of 200) is the mean of the two hitches.
        var intervals = Enumerable.Repeat(16.7, 198).Append(30.0).Append(60.0).ToList();

        var s = IntervalSummary.Of(intervals);

        s.Count.Should().Be(200);
        s.MedianMs.Should().Be(16.7);
        s.Low1Ms.Should().BeApproximately(45.0, 1e-9);
        s.WorstMs.Should().Be(60.0);
        s.Over25.Should().Be(2);
        s.Over50.Should().Be(1);
        s.MeanFps.Should().BeApproximately(200 * 1000.0 / (198 * 16.7 + 90), 1e-6);
    }

    [Fact]
    public void Summary_of_fewer_than_a_hundred_intervals_takes_the_single_slowest_as_the_low()
    {
        var s = IntervalSummary.Of(new[] { 16.0, 17.0, 16.5, 40.0 });

        s.Low1Ms.Should().Be(40.0);
        s.MedianMs.Should().Be(17.0);          // the upper of the two middle values
        s.Over25.Should().Be(1);
        s.Over50.Should().Be(0);
    }

    [Fact]
    public void Summary_of_nothing_is_empty_and_says_so()
    {
        var s = IntervalSummary.Of(Array.Empty<double>());

        s.Count.Should().Be(0);
        s.MeanFps.Should().Be(0);
        s.Words().Should().Be("no frames");
    }

    [Fact]
    public void The_log_writes_one_row_a_frame_and_leaves_an_idle_pause_out_of_the_summary()
    {
        string path = Path.Combine(Path.GetTempPath(), "SkyRender.Tests", Guid.NewGuid().ToString("N"), "frames.csv");
        var stats = new FrameStats { HostLabel = "native/compositor on-demand" };
        try
        {
            stats.StartLog(path, nowMs: 1000);
            stats.Add(Sample(0, 1016.7, 0, resumed: true));                         // the first frame has no interval
            stats.Add(Sample(1, 1033.4, 16.7));
            stats.Add(Sample(2, 1050.1, 16.7));
            // The view sat still for 2 s, then one mouse message asked for a frame 3 ms before it began.
            stats.Add(Sample(3, 3050.1, 2000, resumed: true, reasonAgeMs: 3));
            stats.Add(Sample(4, 3090.1, 40.0, presentCallMs: double.NaN, gpuMs: double.NaN));
            var result = stats.StopLog();

            result.Should().NotBeNull();
            result!.Value.AfterIdle.Should().Be(1);
            result.Value.Present.Count.Should().Be(3);
            result.Value.Present.WorstMs.Should().Be(40.0);
            result.Value.Present.Over25.Should().Be(1);
            result.Value.Words().Should().Contain("1 frame(s) after an idle pause not counted");

            var lines = File.ReadAllLines(path);
            lines.Should().HaveCount(6);
            lines[0].Should().Be(FrameStats.CsvHeader);
            lines[2].Should().Be("1,33.400,16.700,16.700,0.800,0.020,0.500,native/compositor on-demand,0,input,0.000,0.200,3,0,0,0,10,20,1,2,3,1600,900");
            // A host with no buffer swap to time, and a GPU time not in yet, leave their columns empty.
            lines[5].Should().StartWith("4,2090.100,40.000,40.000,0.800,,,native/compositor on-demand,");
            lines[0].Split(',').Length.Should().Be(lines[2].Split(',').Length);
            File.ReadAllText(Path.ChangeExtension(path, ".summary.txt")).Should().Contain("3 frames over");
            stats.Logging.Should().BeFalse();
            stats.StopLog().Should().BeNull();
        }
        finally
        {
            stats.StopLog();
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void A_resumed_frame_that_waited_on_its_own_request_is_a_frame_time_not_a_pause()
    {
        // Resumed, but the request itself waited 180 ms of the 200: the view was asked and was late.
        FrameStats.AfterIdle(Sample(1, 500, 200, resumed: true, reasonAgeMs: 180)).Should().BeFalse();
        FrameStats.AfterIdle(Sample(1, 500, 200, resumed: true, reasonAgeMs: 3)).Should().BeTrue();
        FrameStats.AfterIdle(Sample(1, 500, 200, resumed: false, reasonAgeMs: 3)).Should().BeFalse();
    }

    [Fact]
    public void The_readout_reads_idle_when_no_frame_was_drawn_lately_and_the_rate_when_frames_are_coming()
    {
        var stats = new FrameStats { HostLabel = "wpf continuous" };
        stats.Readout(0).Should().Be("idle (nothing to draw)  |  wpf continuous");

        for (int i = 0; i < 60; i++) stats.Add(Sample(i, 1000 + i * 16.7, i == 0 ? 0 : 16.7));
        double last = 1000 + 59 * 16.7;

        stats.Readout(last + 5).Should().StartWith("60 fps  16.7 ms  1% low 16.7 ms  worst 16.7 ms  render 0.8 ms");
        stats.Readout(last + 600).Should().StartWith("idle (nothing to draw)");
        stats.Recent(last + 5)!.Value.Count.Should().Be(59);
    }
}
