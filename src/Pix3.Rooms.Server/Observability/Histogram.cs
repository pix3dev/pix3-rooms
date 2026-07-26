using System.Diagnostics;

namespace Pix3.Rooms.Server.Observability;

/// <summary>
/// A fixed-bucket distribution: one <see cref="long"/> counter per bucket plus a running sum and count.
/// <see cref="Observe"/> allocates nothing and takes no lock, so a room may call it every tick.
/// </summary>
/// <remarks>
/// Bucket bounds are inclusive upper bounds in ascending order, exactly as Prometheus expects
/// (<c>le</c>). The implicit <c>+Inf</c> bucket is always present and is not part of
/// <see cref="UpperBounds"/>.
/// </remarks>
public sealed class Histogram : Metric<Histogram>
{
    private readonly double[] _upperBounds;

    /// <summary>Per-bucket counts; the last slot is the implicit <c>+Inf</c> bucket.</summary>
    private readonly long[] _bucketCounts;

    private long _count;
    private double _sum;

    internal Histogram(string name, string help, double[] upperBounds, string[] labelNames, string[] labelValues, int maxSeries)
        : base(name, help, labelNames, labelValues, maxSeries)
    {
        _upperBounds = upperBounds;
        _bucketCounts = new long[upperBounds.Length + 1];
    }

    /// <summary>
    /// Buckets tuned for room tick durations at 10–60 Hz: sub-millisecond resolution up to the 50 ms
    /// budget of a 20 Hz room, then coarse buckets for pathological ticks.
    /// </summary>
    public static ReadOnlySpan<double> DefaultTickDurationBuckets =>
    [
        0.0005, 0.001, 0.002, 0.004, 0.008, 0.016, 0.025, 0.05, 0.1, 0.25, 0.5, 1.0,
    ];

    /// <inheritdoc />
    public override MetricKind Kind => MetricKind.Histogram;

    /// <summary>Inclusive bucket upper bounds, ascending. Excludes the implicit <c>+Inf</c> bucket.</summary>
    public IReadOnlyList<double> UpperBounds => _upperBounds;

    /// <summary>Number of buckets including the implicit <c>+Inf</c> bucket.</summary>
    public int BucketCount => _bucketCounts.Length;

    /// <summary>Total number of observations.</summary>
    public long Count => Interlocked.Read(ref _count);

    /// <summary>Sum of all observed values.</summary>
    public double Sum => Volatile.Read(ref _sum);

    /// <summary>
    /// Records one observation. <see cref="double.NaN"/> is ignored rather than poisoning the sum.
    /// </summary>
    public void Observe(double value)
    {
        if (double.IsNaN(value))
        {
            return;
        }

        double[] bounds = _upperBounds;
        int index = bounds.Length;
        for (int i = 0; i < bounds.Length; i++)
        {
            if (value <= bounds[i])
            {
                index = i;
                break;
            }
        }

        Interlocked.Increment(ref _bucketCounts[index]);
        Interlocked.Increment(ref _count);
        AddToSum(value);
    }

    /// <summary>Records an elapsed <see cref="Stopwatch"/> tick delta as seconds.</summary>
    /// <param name="stopwatchTicks">Difference between two <see cref="Stopwatch.GetTimestamp"/> readings.</param>
    public void ObserveStopwatchTicks(long stopwatchTicks)
    {
        if (stopwatchTicks < 0)
        {
            return;
        }

        Observe(stopwatchTicks * SecondsPerStopwatchTick);
    }

    /// <summary>
    /// Copies the raw (non-cumulative) bucket counts, including the trailing <c>+Inf</c> bucket.
    /// </summary>
    /// <param name="destination">At least <see cref="BucketCount"/> elements long.</param>
    public void CopyBucketCountsTo(Span<long> destination)
    {
        long[] counts = _bucketCounts;
        if (destination.Length < counts.Length)
        {
            throw new ArgumentException(
                $"Histogram '{Name}' needs {counts.Length} slots but got {destination.Length}.",
                nameof(destination));
        }

        for (int i = 0; i < counts.Length; i++)
        {
            destination[i] = Interlocked.Read(ref counts[i]);
        }
    }

    /// <inheritdoc />
    protected override Histogram CreateSeries(string[] labelValues)
        => new(Name, Help, _upperBounds, LabelNamesArray, labelValues, MaxSeries);

    internal double[] UpperBoundsArray => _upperBounds;

    private static readonly double SecondsPerStopwatchTick = 1d / Stopwatch.Frequency;

    private void AddToSum(double value)
    {
        double current = Volatile.Read(ref _sum);
        while (true)
        {
            double updated = current + value;
            double witnessed = Interlocked.CompareExchange(ref _sum, updated, current);
            if (witnessed.Equals(current))
            {
                return;
            }

            current = witnessed;
        }
    }
}
