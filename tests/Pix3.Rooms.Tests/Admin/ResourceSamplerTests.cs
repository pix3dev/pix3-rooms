using Pix3.Rooms.Server.Observability;

namespace Pix3.Rooms.Tests.Admin;

/// <summary>
/// The <c>/proc</c> parsers and the CPU arithmetic behind <c>GET /admin/stats</c>.
/// </summary>
/// <remarks>
/// These are unit-tested against literal kernel text rather than against the live files, because the
/// files differ per host and per platform: a test that read the real <c>/proc</c> would prove nothing on
/// a developer's machine and would be untestable for the cases that matter (a truncated line, a
/// per-core-only <c>cpu0</c> first line, a counter that went backwards).
/// </remarks>
public sealed class ResourceSamplerTests
{
    [Fact]
    public void Cpu_ticks_come_from_the_aggregate_line_and_exclude_idle_and_iowait()
    {
        const string procStat = """
            cpu  100 20 30 500 40 5 5 0 0 0
            cpu0 50 10 15 250 20 2 3 0 0 0
            intr 12345
            """;

        Assert.True(ResourceSampler.TryParseCpuTicks(procStat, out long busy, out long total));

        // total = 100+20+30+500+40+5+5+0 = 700; idle+iowait = 540; busy = 160.
        Assert.Equal(700, total);
        Assert.Equal(160, busy);
    }

    [Fact]
    public void A_first_line_that_is_not_the_aggregate_cpu_line_is_refused()
    {
        Assert.False(ResourceSampler.TryParseCpuTicks("cpu0 50 10 15 250 20\ncpu  1 2 3 4 5", out _, out _));
        Assert.False(ResourceSampler.TryParseCpuTicks("cpu  1 2", out _, out _));
        Assert.False(ResourceSampler.TryParseCpuTicks(null, out _, out _));
    }

    [Fact]
    public void Host_cpu_percent_is_the_busy_share_of_the_window()
    {
        Assert.Equal(25d, ResourceSampler.ComputeCpuPercent(50, 200));
        Assert.Equal(0d, ResourceSampler.ComputeCpuPercent(0, 200));

        // A window with no jiffies, or counters that went backwards after a reset, has no answer.
        Assert.Null(ResourceSampler.ComputeCpuPercent(10, 0));
        Assert.Null(ResourceSampler.ComputeCpuPercent(-5, 200));
    }

    [Fact]
    public void Process_cpu_percent_is_normalized_against_the_core_count()
    {
        TimeSpan window = TimeSpan.FromSeconds(10);

        // Ten CPU-seconds over ten wall-seconds is one saturated core: 100% on a single-core host,
        // 25% on a four-core one. The production host is single-core, so the distinction is load-bearing.
        Assert.Equal(100d, ResourceSampler.ComputeProcessCpuPercent(TimeSpan.FromSeconds(10), window, 1));
        Assert.Equal(25d, ResourceSampler.ComputeProcessCpuPercent(TimeSpan.FromSeconds(10), window, 4));

        Assert.Null(ResourceSampler.ComputeProcessCpuPercent(TimeSpan.FromSeconds(1), TimeSpan.Zero, 1));
        Assert.Null(ResourceSampler.ComputeProcessCpuPercent(TimeSpan.FromSeconds(1), window, 0));
    }

    [Fact]
    public void Percentages_are_clamped_into_zero_to_one_hundred()
    {
        // Sampling skew (a delta measured across a suspended process) must not report 140% of a core.
        Assert.Equal(100d, ResourceSampler.ComputeProcessCpuPercent(TimeSpan.FromSeconds(14), TimeSpan.FromSeconds(10), 1));
        Assert.Equal(0d, ResourceSampler.ComputeProcessCpuPercent(TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(10), 1));
    }

    [Fact]
    public void Memory_info_reports_total_and_available_in_bytes()
    {
        const string procMemInfo = """
            MemTotal:        4030524 kB
            MemFree:          204848 kB
            MemAvailable:    1892160 kB
            Buffers:           50000 kB
            """;

        Assert.True(ResourceSampler.TryParseMemoryInfo(procMemInfo, out long? total, out long? available));

        Assert.Equal(4030524L * 1024, total);
        // MemAvailable, not MemFree: on a healthy Linux box MemFree is small because the page cache is used.
        Assert.Equal(1892160L * 1024, available);
    }

    [Fact]
    public void Memory_info_survives_a_file_that_only_carries_one_of_the_two_keys()
    {
        Assert.True(ResourceSampler.TryParseMemoryInfo("MemTotal: 1024 kB\n", out long? total, out long? available));
        Assert.Equal(1024L * 1024, total);
        Assert.Null(available);

        Assert.False(ResourceSampler.TryParseMemoryInfo("Slab: 1024 kB\n", out _, out _));
        Assert.False(ResourceSampler.TryParseMemoryInfo(null, out _, out _));
    }

    [Fact]
    public void Load_average_reads_the_three_windows()
    {
        Assert.True(ResourceSampler.TryParseLoadAverage("0.52 0.31 0.20 2/431 12345", out double one, out double five, out double fifteen));

        Assert.Equal(0.52d, one);
        Assert.Equal(0.31d, five);
        Assert.Equal(0.20d, fifteen);

        Assert.False(ResourceSampler.TryParseLoadAverage("0.52 0.31", out _, out _, out _));
        Assert.False(ResourceSampler.TryParseLoadAverage(null, out _, out _, out _));
    }

    [Fact]
    public void Uptime_reads_the_first_field()
    {
        Assert.True(ResourceSampler.TryParseUptimeSeconds("28498.11 55212.63", out double seconds));
        Assert.Equal(28498.11d, seconds, 2);

        Assert.False(ResourceSampler.TryParseUptimeSeconds("not-a-number", out _));
        Assert.False(ResourceSampler.TryParseUptimeSeconds(null, out _));
    }

    [Fact]
    public void A_sample_reports_this_process_and_never_throws_off_linux()
    {
        ResourceSampler sampler = new();

        ResourceSnapshot snapshot = sampler.Sample();

        Assert.Equal(Environment.ProcessId, snapshot.Process.Pid);
        Assert.True(snapshot.Process.WorkingSetBytes > 0);
        Assert.True(snapshot.Process.ManagedHeapBytes > 0);
        Assert.True(snapshot.Process.ThreadCount > 0);
        Assert.Equal(Environment.MachineName, snapshot.Host.Hostname);
        Assert.Equal(Environment.ProcessorCount, snapshot.Host.CpuCount);
    }

    [Fact]
    public void Two_samples_inside_the_minimum_window_repeat_the_previous_percentages()
    {
        ResourceSampler sampler = new();

        ResourceSnapshot first = sampler.Sample();
        ResourceSnapshot second = sampler.Sample();

        // Back-to-back calls divide by a near-zero window, so the second answer must be the first's, not
        // a fresh division. (Both may be null on a platform without /proc, which is still equality.)
        Assert.Equal(first.Process.CpuPercent, second.Process.CpuPercent);
        Assert.Equal(first.Host.CpuPercent, second.Host.CpuPercent);
    }
}
