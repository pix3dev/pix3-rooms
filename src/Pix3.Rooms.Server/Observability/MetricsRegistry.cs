using System.Diagnostics.CodeAnalysis;

namespace Pix3.Rooms.Server.Observability;

/// <summary>
/// The process-wide set of metric families, rendered by <see cref="PrometheusTextFormatter"/>.
/// Registration is a boot-time, lock-guarded operation; recording is lock-free.
/// </summary>
/// <remarks>
/// <para>
/// Families are stored in a copy-on-write array so a scrape can iterate them without taking a lock and
/// without allocating.
/// </para>
/// <para>
/// Every family caps how many label combinations it keeps
/// (<see cref="MaxSeriesPerMetric"/>); past the cap all further combinations collapse into a single
/// <c>other</c> series. Untrusted values (room ids, peer names, message type bytes) are therefore safe
/// to use as labels: a hostile client cannot grow the registry.
/// </para>
/// </remarks>
public sealed class MetricsRegistry
{
    /// <summary>Default per-family cardinality cap.</summary>
    public const int DefaultMaxSeriesPerMetric = 64;

    private static readonly string[] NoLabels = [];

    private readonly object _sync = new();
    private readonly Dictionary<string, Metric> _byName = new(StringComparer.Ordinal);
    private Metric[] _families = [];

    /// <summary>Creates a registry with the default cardinality cap.</summary>
    public MetricsRegistry()
        : this(DefaultMaxSeriesPerMetric)
    {
    }

    /// <summary>Creates a registry with an explicit per-family cardinality cap.</summary>
    /// <param name="maxSeriesPerMetric">Maximum label combinations retained per family; at least 1.</param>
    public MetricsRegistry(int maxSeriesPerMetric)
    {
        if (maxSeriesPerMetric < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSeriesPerMetric), maxSeriesPerMetric, "At least one series per metric is required.");
        }

        MaxSeriesPerMetric = maxSeriesPerMetric;
    }

    /// <summary>Per-family cardinality cap applied to every metric created here.</summary>
    public int MaxSeriesPerMetric { get; }

    /// <summary>Registered families, newest last. Snapshot; safe to iterate while others record.</summary>
    public IReadOnlyList<Metric> Families => Volatile.Read(ref _families);

    /// <summary>Number of registered families.</summary>
    public int FamilyCount => Volatile.Read(ref _families).Length;

    /// <summary>Declares a counter.</summary>
    /// <param name="name">Prometheus metric name, conventionally ending in <c>_total</c>.</param>
    /// <param name="help">One-line description.</param>
    /// <param name="labelNames">Optional label names; values are bound with <see cref="Metric{TSelf}.WithLabels"/>.</param>
    public Counter CreateCounter(string name, string help, params string[] labelNames)
    {
        string[] labels = ValidateDeclaration(name, help, labelNames, forHistogram: false);
        Counter counter = new(name, help, labels, NoLabels, MaxSeriesPerMetric);
        Register(counter);
        return counter;
    }

    /// <summary>Declares a gauge.</summary>
    /// <param name="name">Prometheus metric name.</param>
    /// <param name="help">One-line description.</param>
    /// <param name="labelNames">Optional label names.</param>
    public Gauge CreateGauge(string name, string help, params string[] labelNames)
    {
        string[] labels = ValidateDeclaration(name, help, labelNames, forHistogram: false);
        Gauge gauge = new(name, help, labels, NoLabels, MaxSeriesPerMetric);
        Register(gauge);
        return gauge;
    }

    /// <summary>Declares a histogram with fixed bucket bounds.</summary>
    /// <param name="name">Prometheus metric name, conventionally carrying its unit (e.g. <c>_seconds</c>).</param>
    /// <param name="help">One-line description.</param>
    /// <param name="upperBounds">Inclusive bucket upper bounds, ascending and finite. <c>+Inf</c> is implicit.</param>
    /// <param name="labelNames">Optional label names; <c>le</c> is reserved and rejected.</param>
    public Histogram CreateHistogram(string name, string help, ReadOnlySpan<double> upperBounds, params string[] labelNames)
    {
        string[] labels = ValidateDeclaration(name, help, labelNames, forHistogram: true);
        double[] bounds = ValidateBuckets(name, upperBounds);
        Histogram histogram = new(name, help, bounds, labels, NoLabels, MaxSeriesPerMetric);
        Register(histogram);
        return histogram;
    }

    /// <summary>Looks up a registered family by name.</summary>
    public bool TryGet(string name, [MaybeNullWhen(false)] out Metric metric)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_sync)
        {
            return _byName.TryGetValue(name, out metric);
        }
    }

    /// <summary>Array snapshot for the formatter: no interface dispatch, no allocation.</summary>
    internal Metric[] FamiliesSnapshot => Volatile.Read(ref _families);

    private void Register(Metric metric)
    {
        lock (_sync)
        {
            if (!_byName.TryAdd(metric.Name, metric))
            {
                throw new InvalidOperationException($"Metric '{metric.Name}' is already registered.");
            }

            Metric[] previous = _families;
            Metric[] grown = new Metric[previous.Length + 1];
            Array.Copy(previous, grown, previous.Length);
            grown[previous.Length] = metric;
            Volatile.Write(ref _families, grown);
        }
    }

    private static string[] ValidateDeclaration(string name, string help, string[] labelNames, bool forHistogram)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(help);
        ArgumentNullException.ThrowIfNull(labelNames);

        if (!IsValidMetricName(name))
        {
            throw new ArgumentException($"'{name}' is not a valid Prometheus metric name.", nameof(name));
        }

        if (help.Length == 0)
        {
            throw new ArgumentException($"Metric '{name}' needs a help text.", nameof(help));
        }

        if (labelNames.Length == 0)
        {
            return NoLabels;
        }

        string[] labels = new string[labelNames.Length];
        for (int i = 0; i < labelNames.Length; i++)
        {
            string label = labelNames[i];
            if (label is null || !IsValidLabelName(label))
            {
                throw new ArgumentException($"Metric '{name}' has an invalid label name at index {i}.", nameof(labelNames));
            }

            if (label.StartsWith("__", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Label '{label}' on metric '{name}' uses the reserved '__' prefix.", nameof(labelNames));
            }

            if (forHistogram && string.Equals(label, "le", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Label 'le' on histogram '{name}' is reserved for bucket bounds.", nameof(labelNames));
            }

            for (int j = 0; j < i; j++)
            {
                if (string.Equals(labels[j], label, StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Metric '{name}' declares label '{label}' twice.", nameof(labelNames));
                }
            }

            labels[i] = label;
        }

        return labels;
    }

    private static double[] ValidateBuckets(string name, ReadOnlySpan<double> upperBounds)
    {
        if (upperBounds.Length == 0)
        {
            throw new ArgumentException($"Histogram '{name}' needs at least one bucket bound.", nameof(upperBounds));
        }

        double[] bounds = new double[upperBounds.Length];
        for (int i = 0; i < upperBounds.Length; i++)
        {
            double bound = upperBounds[i];
            if (!double.IsFinite(bound))
            {
                throw new ArgumentException($"Histogram '{name}' bucket bound #{i} must be finite; +Inf is implicit.", nameof(upperBounds));
            }

            if (i > 0 && bound <= bounds[i - 1])
            {
                throw new ArgumentException($"Histogram '{name}' bucket bounds must ascend strictly.", nameof(upperBounds));
            }

            bounds[i] = bound;
        }

        return bounds;
    }

    private static bool IsValidMetricName(string name)
    {
        if (name.Length == 0)
        {
            return false;
        }

        char first = name[0];
        if (!IsAsciiLetter(first) && first != '_' && first != ':')
        {
            return false;
        }

        for (int i = 1; i < name.Length; i++)
        {
            char c = name[i];
            if (!IsAsciiLetter(c) && !IsAsciiDigit(c) && c != '_' && c != ':')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidLabelName(string label)
    {
        if (label.Length == 0)
        {
            return false;
        }

        char first = label[0];
        if (!IsAsciiLetter(first) && first != '_')
        {
            return false;
        }

        for (int i = 1; i < label.Length; i++)
        {
            char c = label[i];
            if (!IsAsciiLetter(c) && !IsAsciiDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetter(char c) => (uint)((c | 0x20) - 'a') <= 'z' - 'a';

    private static bool IsAsciiDigit(char c) => (uint)(c - '0') <= 9u;
}
