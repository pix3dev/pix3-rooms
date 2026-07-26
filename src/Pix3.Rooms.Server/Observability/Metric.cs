namespace Pix3.Rooms.Server.Observability;

/// <summary>What kind of Prometheus metric a <see cref="Metric"/> renders as.</summary>
public enum MetricKind : byte
{
    /// <summary>Monotonically increasing total.</summary>
    Counter = 0,

    /// <summary>Value that can go up and down.</summary>
    Gauge = 1,

    /// <summary>Bucketed distribution with a sum and a count.</summary>
    Histogram = 2,
}

/// <summary>
/// Base of every metric in the registry. One instance is either a <i>family</i> (declared with label
/// names but carrying no values) or a <i>series</i> (a concrete label combination that holds the
/// numbers). A metric declared without labels is both at once.
/// </summary>
/// <remarks>
/// Instances are created through <see cref="MetricsRegistry"/> and <see cref="Metric{TSelf}.WithLabels"/>
/// only; the constructors are deliberately not public.
/// </remarks>
public abstract class Metric
{
    private readonly string[] _labelNames;
    private readonly string[] _labelValues;

    internal Metric(string name, string help, string[] labelNames, string[] labelValues)
    {
        Name = name;
        Help = help;
        _labelNames = labelNames;
        _labelValues = labelValues;
    }

    /// <summary>Prometheus metric name, without the <c>_bucket</c>/<c>_sum</c>/<c>_count</c> suffixes.</summary>
    public string Name { get; }

    /// <summary>Single-line description rendered as <c># HELP</c>.</summary>
    public string Help { get; }

    /// <summary>Which exposition shape this metric renders as.</summary>
    public abstract MetricKind Kind { get; }

    /// <summary>Declared label names, in the order <see cref="Metric{TSelf}.WithLabels"/> expects values.</summary>
    public IReadOnlyList<string> LabelNames => _labelNames;

    /// <summary>Label values of this series; empty on a labelled family.</summary>
    public IReadOnlyList<string> LabelValues => _labelValues;

    /// <summary>True when this instance carries numbers (i.e. it is a concrete series).</summary>
    public bool IsSeries => _labelValues.Length == _labelNames.Length;

    /// <summary>Snapshot of the series to render for this family. A labelled family with no children is empty.</summary>
    public IReadOnlyList<Metric> Series => SeriesSnapshot;

    /// <summary>Series count currently registered under this family.</summary>
    public int SeriesCount => SeriesSnapshot.Length;

    internal string[] LabelNamesArray => _labelNames;

    internal string[] LabelValuesArray => _labelValues;

    /// <summary>Array-typed snapshot used by the formatter to avoid interface dispatch per sample.</summary>
    internal abstract Metric[] SeriesSnapshot { get; }
}

/// <summary>
/// Self-typed metric base: adds bounded child (series) management so <see cref="WithLabels"/> can hand
/// back the same concrete type it was called on.
/// </summary>
/// <typeparam name="TSelf">The concrete metric type.</typeparam>
/// <remarks>
/// Cardinality is capped per family. Once the cap is reached, every further label combination collapses
/// into one shared <see cref="OverflowLabelValue"/> series, so attacker-controlled label values (a room
/// id, a peer-supplied name) can never grow the registry without bound.
/// </remarks>
public abstract class Metric<TSelf> : Metric
    where TSelf : Metric<TSelf>
{
    /// <summary>Label value every series past the cardinality cap collapses into.</summary>
    public const string OverflowLabelValue = "other";

    /// <summary>Unit separator: cannot appear in a Prometheus label value we emit, so keys stay unambiguous.</summary>
    private const char KeySeparator = '\u001f';

    private readonly int _maxSeries;
    private readonly object _sync = new();
    private Dictionary<string, TSelf>? _children;
    private Metric[] _series;

    internal Metric(string name, string help, string[] labelNames, string[] labelValues, int maxSeries)
        : base(name, help, labelNames, labelValues)
    {
        _maxSeries = maxSeries < 1 ? 1 : maxSeries;

        // A series (including an unlabelled family) renders itself; a labelled family renders its children.
        _series = labelValues.Length == labelNames.Length ? [this] : [];
    }

    /// <summary>Maximum distinct label combinations retained for this family.</summary>
    public int MaxSeries => _maxSeries;

    internal override Metric[] SeriesSnapshot => Volatile.Read(ref _series);

    /// <summary>
    /// Resolves the child series for one label combination, creating it on first use. Repeat calls with
    /// the same values return the same instance, so callers can cache it and skip the lookup on hot paths.
    /// </summary>
    /// <param name="labelValues">One value per declared label name, in declaration order.</param>
    /// <returns>The series to record into; the shared overflow series once the cap is reached.</returns>
    /// <exception cref="InvalidOperationException">This instance is already a series.</exception>
    /// <exception cref="ArgumentException">Wrong number of values, or a null value.</exception>
    public TSelf WithLabels(params string[] labelValues)
    {
        ArgumentNullException.ThrowIfNull(labelValues);

        if (LabelNames.Count == 0)
        {
            throw new InvalidOperationException($"Metric '{Name}' was declared without labels.");
        }

        if (IsSeries)
        {
            throw new InvalidOperationException($"Metric '{Name}' is already a labelled series; call WithLabels on the family.");
        }

        if (labelValues.Length != LabelNames.Count)
        {
            throw new ArgumentException(
                $"Metric '{Name}' declares {LabelNames.Count} label(s) but {labelValues.Length} value(s) were supplied.",
                nameof(labelValues));
        }

        for (int i = 0; i < labelValues.Length; i++)
        {
            if (labelValues[i] is null)
            {
                throw new ArgumentException($"Label value #{i} for metric '{Name}' is null.", nameof(labelValues));
            }
        }

        string key = BuildKey(labelValues);

        lock (_sync)
        {
            _children ??= new Dictionary<string, TSelf>(StringComparer.Ordinal);
            if (_children.TryGetValue(key, out TSelf? existing))
            {
                return existing;
            }

            if (_children.Count >= _maxSeries)
            {
                return GetOrCreateOverflowLocked();
            }

            // Copy: the caller owns the params array and could mutate it afterwards.
            string[] owned = new string[labelValues.Length];
            Array.Copy(labelValues, owned, labelValues.Length);
            return CreateChildLocked(key, owned);
        }
    }

    /// <summary>Creates one child series. Implemented by each concrete metric type.</summary>
    /// <param name="labelValues">Owned array of label values; the child keeps it.</param>
    protected abstract TSelf CreateSeries(string[] labelValues);

    private TSelf GetOrCreateOverflowLocked()
    {
        string[] values = new string[LabelNames.Count];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = OverflowLabelValue;
        }

        string key = BuildKey(values);
        Dictionary<string, TSelf> children = _children ??= new Dictionary<string, TSelf>(StringComparer.Ordinal);
        return children.TryGetValue(key, out TSelf? existing) ? existing : CreateChildLocked(key, values);
    }

    private TSelf CreateChildLocked(string key, string[] labelValues)
    {
        TSelf child = CreateSeries(labelValues);
        _children!.Add(key, child);

        Metric[] previous = _series;
        Metric[] grown = new Metric[previous.Length + 1];
        Array.Copy(previous, grown, previous.Length);
        grown[previous.Length] = child;
        Volatile.Write(ref _series, grown);

        return child;
    }

    private static string BuildKey(string[] labelValues)
        => labelValues.Length == 1 ? labelValues[0] : string.Join(KeySeparator, labelValues);
}
