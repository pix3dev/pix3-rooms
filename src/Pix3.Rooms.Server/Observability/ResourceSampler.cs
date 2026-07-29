using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Pix3.Rooms.Server.Observability;

/// <summary>What this process itself is consuming, as one sample.</summary>
/// <param name="Pid">Process id, so an operator can match the numbers against <c>systemctl status</c>.</param>
/// <param name="CpuPercent">
/// CPU use since the previous sample, normalized against <see cref="HostResources.CpuCount"/> — 100 means
/// every core saturated. Null only before a baseline exists.
/// </param>
/// <param name="CpuSecondsTotal">Total CPU seconds burned since start (user + kernel).</param>
/// <param name="WorkingSetBytes">Resident set size.</param>
/// <param name="PrivateMemoryBytes">Committed private bytes.</param>
/// <param name="ManagedHeapBytes">Bytes the GC currently considers allocated.</param>
/// <param name="HeapLimitBytes">
/// The GC's hard heap limit. On the shipped unit this is derived from <c>MemoryMax</c>, so it is the number
/// that actually decides when this process dies of memory, not the host's RAM.
/// </param>
/// <param name="ThreadCount">OS threads in the process.</param>
/// <param name="Gen2Collections">Full collections since start; a rising count on an idle server is a smell.</param>
public sealed record ProcessResources(
    int Pid,
    double? CpuPercent,
    double CpuSecondsTotal,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    long ManagedHeapBytes,
    long? HeapLimitBytes,
    int ThreadCount,
    int Gen2Collections);

/// <summary>
/// The host's load and headroom. Every field is nullable because production reads them from
/// <c>/proc</c>: on a platform without it (a developer's Windows box) the honest answer is "unknown",
/// not a fabricated zero.
/// </summary>
/// <param name="Hostname">Machine name, which is also how a caller tells two hosts apart.</param>
/// <param name="Os">OS description.</param>
/// <param name="CpuCount">Logical processors visible to this process.</param>
/// <param name="CpuPercent">Host-wide CPU busy percentage since the previous sample.</param>
/// <param name="Load1">1-minute load average.</param>
/// <param name="Load5">5-minute load average.</param>
/// <param name="Load15">15-minute load average.</param>
/// <param name="MemoryTotalBytes">Physical RAM.</param>
/// <param name="MemoryAvailableBytes">
/// RAM available without swapping (<c>MemAvailable</c>), which is the free-memory number worth acting on —
/// <c>MemFree</c> looks alarming on any healthy Linux box because the page cache is doing its job.
/// </param>
/// <param name="DiskTotalBytes">Size of the filesystem the application runs from.</param>
/// <param name="DiskFreeBytes">Free space on that filesystem.</param>
/// <param name="UptimeSeconds">Seconds since boot.</param>
public sealed record HostResources(
    string Hostname,
    string Os,
    int CpuCount,
    double? CpuPercent,
    double? Load1,
    double? Load5,
    double? Load15,
    long? MemoryTotalBytes,
    long? MemoryAvailableBytes,
    long? DiskTotalBytes,
    long? DiskFreeBytes,
    double? UptimeSeconds);

/// <summary>One resource reading: this process, and the host it shares with its neighbours.</summary>
/// <param name="Process">Process-level numbers.</param>
/// <param name="Host">Host-level numbers.</param>
public sealed record ResourceSnapshot(ProcessResources Process, HostResources Host);

/// <summary>
/// Samples process and host resource use for <c>GET /admin/stats</c>.
/// </summary>
/// <remarks>
/// <para>
/// Both CPU figures are <b>deltas between calls</b>, because a cumulative counter divided by uptime is an
/// average over the whole run and says nothing about right now. The baseline is taken in the constructor,
/// which is why the composition root resolves this eagerly at startup: the first dashboard poll should
/// report a real percentage instead of a dash.
/// </para>
/// <para>
/// Nothing here is on the tick path — it runs once per admin request — so the idiomatic control-path style
/// applies: allocations, LINQ-free but ordinary code, and file reads.
/// </para>
/// <para>
/// Every host reading comes from <c>/proc</c>, which the shipped unit can still read: <c>ProtectProc=invisible</c>
/// hides other processes' directories, not <c>/proc/stat</c>, <c>/proc/meminfo</c>, <c>/proc/loadavg</c> or
/// <c>/proc/uptime</c>. A read that fails is reported as null rather than throwing — an operator asking for
/// the dashboard must never get a 500 because the kernel interface moved.
/// </para>
/// </remarks>
public sealed class ResourceSampler
{
    /// <summary>Kernel-provided host CPU accounting.</summary>
    public const string ProcStatPath = "/proc/stat";

    /// <summary>Kernel-provided memory accounting.</summary>
    public const string ProcMemInfoPath = "/proc/meminfo";

    /// <summary>Kernel-provided load averages.</summary>
    public const string ProcLoadAvgPath = "/proc/loadavg";

    /// <summary>Kernel-provided seconds since boot.</summary>
    public const string ProcUptimePath = "/proc/uptime";

    /// <summary>
    /// Shortest window a fresh percentage is computed over. Two polls in the same instant would divide by a
    /// near-zero elapsed time and produce noise; below this the previous answer is repeated instead.
    /// </summary>
    private static readonly TimeSpan MinimumWindow = TimeSpan.FromMilliseconds(200);

    private readonly object _sync = new();

    private DateTimeOffset _lastSampledAt;
    private TimeSpan _lastProcessCpu;
    private long _lastHostBusyTicks;
    private long _lastHostTotalTicks;
    private bool _hasHostBaseline;
    private double? _lastProcessPercent;
    private double? _lastHostPercent;

    /// <summary>Creates the sampler and takes the first baseline, so the first real sample has a window.</summary>
    public ResourceSampler()
    {
        _lastSampledAt = DateTimeOffset.UtcNow;
        _lastProcessCpu = ReadProcessCpu();
        _hasHostBaseline = TryReadHostCpuTicks(out long busyTicks, out long totalTicks);
        _lastHostBusyTicks = busyTicks;
        _lastHostTotalTicks = totalTicks;
    }

    /// <summary>Takes a reading and re-bases the CPU window on it.</summary>
    public ResourceSnapshot Sample()
    {
        using Process process = Process.GetCurrentProcess();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        TimeSpan processCpu = process.TotalProcessorTime;
        bool hostRead = TryReadHostCpuTicks(out long hostBusy, out long hostTotal);

        double? processPercent;
        double? hostPercent;

        lock (_sync)
        {
            TimeSpan elapsed = now - _lastSampledAt;
            if (elapsed >= MinimumWindow)
            {
                processPercent = ComputeProcessCpuPercent(processCpu - _lastProcessCpu, elapsed, Environment.ProcessorCount);
                hostPercent = hostRead && _hasHostBaseline
                    ? ComputeCpuPercent(hostBusy - _lastHostBusyTicks, hostTotal - _lastHostTotalTicks)
                    : null;

                _lastSampledAt = now;
                _lastProcessCpu = processCpu;
                if (hostRead)
                {
                    _lastHostBusyTicks = hostBusy;
                    _lastHostTotalTicks = hostTotal;
                    _hasHostBaseline = true;
                }

                _lastProcessPercent = processPercent;
                _lastHostPercent = hostPercent;
            }
            else
            {
                // Too soon to say anything new; repeating the last answer beats inventing one.
                processPercent = _lastProcessPercent;
                hostPercent = _lastHostPercent;
            }
        }

        GCMemoryInfo gcInfo = GC.GetGCMemoryInfo();

        ProcessResources processResources = new(
            Environment.ProcessId,
            processPercent,
            Math.Round(processCpu.TotalSeconds, 3),
            process.WorkingSet64,
            process.PrivateMemorySize64,
            GC.GetTotalMemory(false),
            gcInfo.TotalAvailableMemoryBytes > 0 ? gcInfo.TotalAvailableMemoryBytes : null,
            process.Threads.Count,
            GC.CollectionCount(2));

        return new ResourceSnapshot(processResources, ReadHost(hostPercent));
    }

    /// <summary>
    /// Turns a CPU-time delta into a percentage of the host's total capacity over the same wall-clock
    /// window. 100 means every core was saturated by this process, not one core.
    /// </summary>
    /// <param name="cpuDelta">CPU time consumed during the window.</param>
    /// <param name="elapsed">Wall-clock length of the window.</param>
    /// <param name="cpuCount">Logical processors the window's capacity is measured against.</param>
    /// <returns>The percentage, or null when the window or the core count is unusable.</returns>
    public static double? ComputeProcessCpuPercent(TimeSpan cpuDelta, TimeSpan elapsed, int cpuCount)
    {
        if (elapsed <= TimeSpan.Zero || cpuCount <= 0)
        {
            return null;
        }

        double percent = cpuDelta.TotalSeconds / (elapsed.TotalSeconds * cpuCount) * 100d;
        return Clamp(percent);
    }

    /// <summary>Busy share of a <c>/proc/stat</c> jiffy delta, as a percentage.</summary>
    /// <param name="busyDelta">Non-idle jiffies during the window.</param>
    /// <param name="totalDelta">All jiffies during the window.</param>
    /// <returns>The percentage, or null when the window carries no jiffies (or the counters reset).</returns>
    public static double? ComputeCpuPercent(long busyDelta, long totalDelta)
    {
        if (totalDelta <= 0 || busyDelta < 0)
        {
            return null;
        }

        return Clamp((double)busyDelta / totalDelta * 100d);
    }

    /// <summary>
    /// Parses the aggregate <c>cpu</c> line of <c>/proc/stat</c> into busy and total jiffies.
    /// </summary>
    /// <remarks>
    /// Busy is total minus <c>idle</c> and <c>iowait</c>: a core waiting on disk is not doing work, and
    /// counting it as busy would make a quiet server look loaded. <c>guest</c> and <c>guest_nice</c> are
    /// already included in <c>user</c>/<c>nice</c> by the kernel and are therefore skipped.
    /// </remarks>
    /// <param name="content">Contents of <c>/proc/stat</c> (only the first line is read).</param>
    /// <param name="busyTicks">Non-idle jiffies since boot.</param>
    /// <param name="totalTicks">All jiffies since boot.</param>
    /// <returns>False when the first line is not the aggregate <c>cpu</c> line.</returns>
    public static bool TryParseCpuTicks(string? content, out long busyTicks, out long totalTicks)
    {
        busyTicks = 0;
        totalTicks = 0;

        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        int lineEnd = content.IndexOf('\n');
        string line = lineEnd < 0 ? content : content[..lineEnd];
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // "cpu" is the all-core aggregate; "cpu0" is one core and must not be mistaken for it.
        if (parts.Length < 5 || !string.Equals(parts[0], "cpu", StringComparison.Ordinal))
        {
            return false;
        }

        long total = 0;
        long idle = 0;
        int fields = Math.Min(parts.Length - 1, 8);
        for (int i = 0; i < fields; i++)
        {
            if (!long.TryParse(parts[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) || value < 0)
            {
                return false;
            }

            total += value;

            // Field order: user nice system idle iowait irq softirq steal.
            if (i is 3 or 4)
            {
                idle += value;
            }
        }

        busyTicks = total - idle;
        totalTicks = total;
        return totalTicks > 0;
    }

    /// <summary>Parses <c>MemTotal</c> and <c>MemAvailable</c> out of <c>/proc/meminfo</c>, in bytes.</summary>
    /// <param name="content">Contents of <c>/proc/meminfo</c>.</param>
    /// <param name="totalBytes">Physical RAM, or null when absent.</param>
    /// <param name="availableBytes">Available RAM, or null when absent.</param>
    /// <returns>True when at least one of the two was found.</returns>
    public static bool TryParseMemoryInfo(string? content, out long? totalBytes, out long? availableBytes)
    {
        totalBytes = null;
        availableBytes = null;

        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        foreach (string rawLine in content.Split('\n'))
        {
            if (totalBytes is not null && availableBytes is not null)
            {
                break;
            }

            int colon = rawLine.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            string key = rawLine[..colon];
            bool isTotal = string.Equals(key, "MemTotal", StringComparison.Ordinal);
            bool isAvailable = !isTotal && string.Equals(key, "MemAvailable", StringComparison.Ordinal);
            if (!isTotal && !isAvailable)
            {
                continue;
            }

            // Values are "<number> kB"; the unit is always kB on Linux, but parse defensively anyway.
            string[] parts = rawLine[(colon + 1)..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0 ||
                !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long amount) ||
                amount < 0)
            {
                continue;
            }

            long bytes = parts.Length > 1 && string.Equals(parts[1], "kB", StringComparison.OrdinalIgnoreCase)
                ? amount * 1024L
                : amount;

            if (isTotal)
            {
                totalBytes = bytes;
            }
            else
            {
                availableBytes = bytes;
            }
        }

        return totalBytes is not null || availableBytes is not null;
    }

    /// <summary>Parses the three load averages out of <c>/proc/loadavg</c>.</summary>
    /// <param name="content">Contents of <c>/proc/loadavg</c>.</param>
    /// <param name="one">1-minute average.</param>
    /// <param name="five">5-minute average.</param>
    /// <param name="fifteen">15-minute average.</param>
    /// <returns>False when the line does not carry three parseable numbers.</returns>
    public static bool TryParseLoadAverage(string? content, out double one, out double five, out double fifteen)
    {
        one = 0d;
        five = 0d;
        fifteen = 0d;

        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        string[] parts = content.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 3
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out one)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out five)
            && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out fifteen);
    }

    /// <summary>Parses seconds since boot out of <c>/proc/uptime</c>.</summary>
    /// <param name="content">Contents of <c>/proc/uptime</c>.</param>
    /// <param name="seconds">Seconds since boot.</param>
    /// <returns>False when the first field is not a number.</returns>
    public static bool TryParseUptimeSeconds(string? content, out double seconds)
    {
        seconds = 0d;

        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        string[] parts = content.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 1
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
    }

    private HostResources ReadHost(double? hostPercent)
    {
        TryParseMemoryInfo(TryReadFile(ProcMemInfoPath), out long? memoryTotal, out long? memoryAvailable);

        double? load1 = null;
        double? load5 = null;
        double? load15 = null;
        if (TryParseLoadAverage(TryReadFile(ProcLoadAvgPath), out double one, out double five, out double fifteen))
        {
            load1 = one;
            load5 = five;
            load15 = fifteen;
        }

        double? uptime = TryParseUptimeSeconds(TryReadFile(ProcUptimePath), out double bootSeconds)
            ? Math.Round(bootSeconds, 0)
            : null;

        ReadDisk(out long? diskTotal, out long? diskFree);

        return new HostResources(
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            Environment.ProcessorCount,
            hostPercent,
            load1,
            load5,
            load15,
            memoryTotal,
            memoryAvailable,
            diskTotal,
            diskFree,
            uptime);
    }

    private static void ReadDisk(out long? totalBytes, out long? freeBytes)
    {
        totalBytes = null;
        freeBytes = null;

        try
        {
            string root = Path.GetPathRoot(AppContext.BaseDirectory) ?? "/";
            DriveInfo drive = new(root);
            if (!drive.IsReady)
            {
                return;
            }

            totalBytes = drive.TotalSize;
            freeBytes = drive.AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // A hardened or exotic mount that refuses statfs is reported as unknown, not as an error.
        }
    }

    private static TimeSpan ReadProcessCpu()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            return process.TotalProcessorTime;
        }
        catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException or NotSupportedException)
        {
            return TimeSpan.Zero;
        }
    }

    private static bool TryReadHostCpuTicks(out long busyTicks, out long totalTicks)
        => TryParseCpuTicks(TryReadFile(ProcStatPath), out busyTicks, out totalTicks);

    private static string? TryReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static double Clamp(double percent)
    {
        if (double.IsNaN(percent) || percent < 0d)
        {
            return 0d;
        }

        return Math.Round(percent > 100d ? 100d : percent, 2);
    }
}
