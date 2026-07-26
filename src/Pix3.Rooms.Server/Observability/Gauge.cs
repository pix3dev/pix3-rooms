namespace Pix3.Rooms.Server.Observability;

/// <summary>
/// A value that moves in both directions (player counts, entity counts, queue depths). Reads and writes
/// are lock-free; <see cref="Add"/> uses a compare-exchange loop so concurrent increments never lose an
/// update.
/// </summary>
public sealed class Gauge : Metric<Gauge>
{
    private double _value;

    internal Gauge(string name, string help, string[] labelNames, string[] labelValues, int maxSeries)
        : base(name, help, labelNames, labelValues, maxSeries)
    {
    }

    /// <inheritdoc />
    public override MetricKind Kind => MetricKind.Gauge;

    /// <summary>Current value.</summary>
    public double Value => Volatile.Read(ref _value);

    /// <summary>Replaces the value.</summary>
    public void Set(double value) => Interlocked.Exchange(ref _value, value);

    /// <summary>Adds one.</summary>
    public void Inc() => Add(1d);

    /// <summary>Adds <paramref name="amount"/>.</summary>
    public void Inc(double amount) => Add(amount);

    /// <summary>Subtracts one.</summary>
    public void Dec() => Add(-1d);

    /// <summary>Subtracts <paramref name="amount"/>.</summary>
    public void Dec(double amount) => Add(-amount);

    /// <summary>Adds <paramref name="amount"/> (negative to subtract). Zero is a no-op.</summary>
    public void Add(double amount)
    {
        if (amount == 0d)
        {
            return;
        }

        double current = Volatile.Read(ref _value);
        while (true)
        {
            double updated = current + amount;
            double witnessed = Interlocked.CompareExchange(ref _value, updated, current);

            // Equals, not ==, so a NaN gauge still converges instead of spinning forever.
            if (witnessed.Equals(current))
            {
                return;
            }

            current = witnessed;
        }
    }

    /// <inheritdoc />
    protected override Gauge CreateSeries(string[] labelValues)
        => new(Name, Help, LabelNamesArray, labelValues, MaxSeries);
}
