using System.Buffers;
using System.Globalization;
using System.Text;

namespace Pix3.Rooms.Server.Observability;

/// <summary>
/// Renders a <see cref="MetricsRegistry"/> in the Prometheus text exposition format, version 0.0.4.
/// </summary>
/// <remarks>
/// <para>
/// Output is written straight into an <see cref="IBufferWriter{T}"/> — in production the HTTP response
/// body writer — so a scrape never materialises the whole payload as a string.
/// </para>
/// <para>
/// Escaping follows the spec: <c>\</c> and newlines are escaped in <c># HELP</c> text, and <c>\</c>,
/// newlines and <c>"</c> in label values. Carriage returns are dropped, since the exposition grammar has
/// no escape for them.
/// </para>
/// </remarks>
public static class PrometheusTextFormatter
{
    /// <summary>Content type a scrape response must carry.</summary>
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    private const string HelpPrefix = "# HELP ";
    private const string TypePrefix = "# TYPE ";
    private const string CounterType = " counter\n";
    private const string GaugeType = " gauge\n";
    private const string HistogramType = " histogram\n";
    private const string BucketSuffix = "_bucket";
    private const string SumSuffix = "_sum";
    private const string CountSuffix = "_count";
    private const string PositiveInfinity = "+Inf";
    private const string NegativeInfinity = "-Inf";
    private const string NotANumber = "NaN";

    /// <summary>Writes the whole registry as one exposition payload.</summary>
    /// <param name="registry">Registry to render.</param>
    /// <param name="destination">Where the UTF-8 bytes go; typically <c>HttpResponse.BodyWriter</c>.</param>
    public static void Write(MetricsRegistry registry, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(destination);

        Metric[] families = registry.FamiliesSnapshot;
        for (int i = 0; i < families.Length; i++)
        {
            WriteFamily(families[i], destination);
        }
    }

    /// <summary>
    /// Renders the registry into a string. Diagnostics and tests only — the HTTP path uses the
    /// <see cref="IBufferWriter{T}"/> overload.
    /// </summary>
    public static string Format(MetricsRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        ArrayBufferWriter<byte> buffer = new(4096);
        Write(registry, buffer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteFamily(Metric family, IBufferWriter<byte> writer)
    {
        WriteRaw(writer, HelpPrefix);
        WriteRaw(writer, family.Name);
        WriteRaw(writer, " ");
        WriteEscaped(writer, family.Help, escapeQuotes: false);
        WriteRaw(writer, "\n");

        WriteRaw(writer, TypePrefix);
        WriteRaw(writer, family.Name);
        WriteRaw(writer, family.Kind switch
        {
            MetricKind.Counter => CounterType,
            MetricKind.Gauge => GaugeType,
            _ => HistogramType,
        });

        Metric[] series = family.SeriesSnapshot;
        for (int i = 0; i < series.Length; i++)
        {
            switch (series[i])
            {
                case Counter counter:
                    WriteRaw(writer, counter.Name);
                    WriteLabelSet(writer, counter.LabelNamesArray, counter.LabelValuesArray);
                    WriteRaw(writer, " ");
                    WriteInt64(writer, counter.Value);
                    WriteRaw(writer, "\n");
                    break;

                case Gauge gauge:
                    WriteRaw(writer, gauge.Name);
                    WriteLabelSet(writer, gauge.LabelNamesArray, gauge.LabelValuesArray);
                    WriteRaw(writer, " ");
                    WriteDouble(writer, gauge.Value);
                    WriteRaw(writer, "\n");
                    break;

                case Histogram histogram:
                    WriteHistogramSeries(writer, histogram);
                    break;
            }
        }
    }

    private static void WriteHistogramSeries(IBufferWriter<byte> writer, Histogram histogram)
    {
        int bucketCount = histogram.BucketCount;
        long[] rented = ArrayPool<long>.Shared.Rent(bucketCount);
        try
        {
            Span<long> counts = rented.AsSpan(0, bucketCount);
            histogram.CopyBucketCountsTo(counts);

            double[] bounds = histogram.UpperBoundsArray;
            long cumulative = 0;
            for (int i = 0; i < bucketCount; i++)
            {
                cumulative += counts[i];

                WriteRaw(writer, histogram.Name);
                WriteRaw(writer, BucketSuffix);
                WriteBucketLabelSet(
                    writer,
                    histogram.LabelNamesArray,
                    histogram.LabelValuesArray,
                    i < bounds.Length ? bounds[i] : double.PositiveInfinity);
                WriteRaw(writer, " ");
                WriteInt64(writer, cumulative);
                WriteRaw(writer, "\n");
            }

            WriteRaw(writer, histogram.Name);
            WriteRaw(writer, SumSuffix);
            WriteLabelSet(writer, histogram.LabelNamesArray, histogram.LabelValuesArray);
            WriteRaw(writer, " ");
            WriteDouble(writer, histogram.Sum);
            WriteRaw(writer, "\n");

            WriteRaw(writer, histogram.Name);
            WriteRaw(writer, CountSuffix);
            WriteLabelSet(writer, histogram.LabelNamesArray, histogram.LabelValuesArray);
            WriteRaw(writer, " ");

            // Deliberately the bucket total, not Histogram.Count: a concurrent Observe between the two
            // reads must not make the exposition internally inconsistent.
            WriteInt64(writer, cumulative);
            WriteRaw(writer, "\n");
        }
        finally
        {
            ArrayPool<long>.Shared.Return(rented);
        }
    }

    private static void WriteLabelSet(IBufferWriter<byte> writer, string[] labelNames, string[] labelValues)
    {
        if (labelNames.Length == 0)
        {
            return;
        }

        WriteRaw(writer, "{");
        for (int i = 0; i < labelNames.Length; i++)
        {
            if (i > 0)
            {
                WriteRaw(writer, ",");
            }

            WriteLabelPair(writer, labelNames[i], i < labelValues.Length ? labelValues[i] : string.Empty);
        }

        WriteRaw(writer, "}");
    }

    private static void WriteBucketLabelSet(IBufferWriter<byte> writer, string[] labelNames, string[] labelValues, double upperBound)
    {
        WriteRaw(writer, "{");
        for (int i = 0; i < labelNames.Length; i++)
        {
            WriteLabelPair(writer, labelNames[i], i < labelValues.Length ? labelValues[i] : string.Empty);
            WriteRaw(writer, ",");
        }

        WriteRaw(writer, "le=\"");
        WriteDouble(writer, upperBound);
        WriteRaw(writer, "\"}");
    }

    private static void WriteLabelPair(IBufferWriter<byte> writer, string name, string value)
    {
        WriteRaw(writer, name);
        WriteRaw(writer, "=\"");
        WriteEscaped(writer, value, escapeQuotes: true);
        WriteRaw(writer, "\"");
    }

    private static void WriteEscaped(IBufferWriter<byte> writer, string text, bool escapeQuotes)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool special = c is '\\' or '\n' or '\r' || (escapeQuotes && c == '"');
            if (!special)
            {
                continue;
            }

            if (i > start)
            {
                WriteUtf8(writer, text.AsSpan(start, i - start));
            }

            switch (c)
            {
                case '\\':
                    WriteRaw(writer, "\\\\");
                    break;
                case '\n':
                    WriteRaw(writer, "\\n");
                    break;
                case '"':
                    WriteRaw(writer, "\\\"");
                    break;
                default:
                    // Carriage return: no escape exists in the 0.0.4 grammar, so it is dropped.
                    break;
            }

            start = i + 1;
        }

        if (start < text.Length)
        {
            WriteUtf8(writer, text.AsSpan(start));
        }
    }

    private static void WriteInt64(IBufferWriter<byte> writer, long value)
    {
        Span<byte> span = writer.GetSpan(24);
        if (!value.TryFormat(span, out int written, default, CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException("Failed to format a metric value.");
        }

        writer.Advance(written);
    }

    private static void WriteDouble(IBufferWriter<byte> writer, double value)
    {
        if (double.IsNaN(value))
        {
            WriteRaw(writer, NotANumber);
            return;
        }

        if (double.IsPositiveInfinity(value))
        {
            WriteRaw(writer, PositiveInfinity);
            return;
        }

        if (double.IsNegativeInfinity(value))
        {
            WriteRaw(writer, NegativeInfinity);
            return;
        }

        // Default format = shortest round-trippable form ("0.005", "1", "1E-05"); all parse as Go floats.
        Span<byte> span = writer.GetSpan(32);
        if (!value.TryFormat(span, out int written, default, CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException("Failed to format a metric value.");
        }

        writer.Advance(written);
    }

    private static void WriteRaw(IBufferWriter<byte> writer, string ascii) => WriteUtf8(writer, ascii.AsSpan());

    private static void WriteUtf8(IBufferWriter<byte> writer, ReadOnlySpan<char> text)
    {
        if (text.Length == 0)
        {
            return;
        }

        Span<byte> span = writer.GetSpan(Encoding.UTF8.GetMaxByteCount(text.Length));
        int written = Encoding.UTF8.GetBytes(text, span);
        writer.Advance(written);
    }
}
