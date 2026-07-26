namespace Pix3.Rooms.Server.Observability;

/// <summary>
/// A monotonically increasing 64-bit total. Increments are a single <see cref="Interlocked"/> add, so
/// hot paths can count without locks or allocations.
/// </summary>
public sealed class Counter : Metric<Counter>
{
    private long _value;

    internal Counter(string name, string help, string[] labelNames, string[] labelValues, int maxSeries)
        : base(name, help, labelNames, labelValues, maxSeries)
    {
    }

    /// <inheritdoc />
    public override MetricKind Kind => MetricKind.Counter;

    /// <summary>Current total.</summary>
    public long Value => Interlocked.Read(ref _value);

    /// <summary>Adds one.</summary>
    public void Inc() => Interlocked.Increment(ref _value);

    /// <summary>Adds <paramref name="amount"/>.</summary>
    /// <param name="amount">Non-negative increment; zero is a no-op.</param>
    /// <exception cref="ArgumentOutOfRangeException">A counter may never decrease.</exception>
    public void Add(long amount)
    {
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, $"Counter '{Name}' cannot decrease.");
        }

        if (amount == 0)
        {
            return;
        }

        Interlocked.Add(ref _value, amount);
    }

    /// <inheritdoc />
    protected override Counter CreateSeries(string[] labelValues)
        => new(Name, Help, LabelNamesArray, labelValues, MaxSeries);
}
