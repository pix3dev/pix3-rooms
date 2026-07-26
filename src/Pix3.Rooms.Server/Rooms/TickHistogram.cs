using System.Diagnostics;

namespace Pix3.Rooms.Server.Rooms;

/// <summary>
/// Fixed-size latency histogram for room tick durations: allocation-free to record, allocation-free to
/// query, and no sorting of a growing list.
/// </summary>
/// <remarks>
/// <para>
/// Buckets are linear at <see cref="ResolutionPerMillisecond"/> per millisecond up to
/// <see cref="TrackedMilliseconds"/>, plus one overflow bucket. At 20 Hz the whole budget is 50 ms, so
/// 0.25 ms buckets resolve p50/p99 far better than the numbers are worth.
/// </para>
/// <para>
/// Two windows are kept: the live one and the last complete one. Percentiles read both, so they
/// describe recent behaviour (roughly one to two window lengths) instead of the whole process
/// lifetime, and a room that was slow an hour ago does not keep its p99 pinned forever.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="Record"/> is for the owning room's tick thread only.
/// <see cref="Percentile"/> is safe from any thread: it only reads, and a bucket updated mid-scan
/// merely shifts the answer by one sample.
/// </para>
/// </remarks>
public sealed class TickHistogram
{
    /// <summary>Buckets per millisecond (0.25 ms resolution).</summary>
    public const int ResolutionPerMillisecond = 4;

    /// <summary>Milliseconds covered by the linear buckets; anything slower lands in the overflow bucket.</summary>
    public const int TrackedMilliseconds = 256;

    private const int LinearBucketCount = ResolutionPerMillisecond * TrackedMilliseconds;
    private const int OverflowIndex = LinearBucketCount;

    private readonly long[] _live = new long[LinearBucketCount + 1];
    private readonly long[] _previous = new long[LinearBucketCount + 1];
    private readonly long _windowTimestampTicks;
    private long _windowStartTimestamp;

    /// <summary>Creates a histogram whose window is <paramref name="windowSeconds"/> long.</summary>
    /// <param name="windowSeconds">Window length; clamped to [1, 3600].</param>
    /// <param name="startTimestamp">A <see cref="Stopwatch.GetTimestamp"/> value marking "now".</param>
    public TickHistogram(int windowSeconds, long startTimestamp)
    {
        int seconds = Math.Clamp(windowSeconds, 1, 3600);
        _windowTimestampTicks = Stopwatch.Frequency * seconds;
        _windowStartTimestamp = startTimestamp;
    }

    /// <summary>
    /// Records one tick duration and rotates the window when it has elapsed. Tick thread only.
    /// </summary>
    /// <param name="milliseconds">Measured tick duration.</param>
    /// <param name="timestamp">A <see cref="Stopwatch.GetTimestamp"/> value marking "now".</param>
    public void Record(double milliseconds, long timestamp)
    {
        int index;
        if (double.IsNaN(milliseconds) || milliseconds <= 0.0)
        {
            index = 0;
        }
        else
        {
            double scaled = milliseconds * ResolutionPerMillisecond;
            index = scaled >= LinearBucketCount ? OverflowIndex : (int)scaled;
        }

        _live[index]++;

        if (timestamp - _windowStartTimestamp < _windowTimestampTicks)
        {
            return;
        }

        Array.Copy(_live, _previous, _live.Length);
        Array.Clear(_live);
        _windowStartTimestamp = timestamp;
    }

    /// <summary>
    /// Upper bound of the bucket holding the requested percentile, in milliseconds. Returns 0 when
    /// nothing has been recorded, and <see cref="TrackedMilliseconds"/> for samples in the overflow
    /// bucket (read as "at least that slow").
    /// </summary>
    /// <param name="fraction">Percentile as a fraction, e.g. 0.5 or 0.99. Clamped to [0, 1].</param>
    public double Percentile(double fraction)
    {
        long[] live = _live;
        long[] previous = _previous;

        long total = 0;
        for (int i = 0; i < live.Length; i++)
        {
            total += Volatile.Read(ref live[i]) + Volatile.Read(ref previous[i]);
        }

        if (total <= 0)
        {
            return 0.0;
        }

        double clamped = Math.Clamp(fraction, 0.0, 1.0);
        long target = (long)Math.Ceiling(total * clamped);
        if (target < 1)
        {
            target = 1;
        }

        long cumulative = 0;
        for (int i = 0; i < live.Length; i++)
        {
            cumulative += Volatile.Read(ref live[i]) + Volatile.Read(ref previous[i]);
            if (cumulative < target)
            {
                continue;
            }

            return i >= OverflowIndex ? TrackedMilliseconds : (i + 1) / (double)ResolutionPerMillisecond;
        }

        return TrackedMilliseconds;
    }
}
