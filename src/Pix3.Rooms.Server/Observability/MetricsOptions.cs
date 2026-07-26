using Microsoft.Extensions.Options;

namespace Pix3.Rooms.Server.Observability;

/// <summary>
/// Settings for the Prometheus scrape endpoint, bound from the <c>Metrics</c> configuration section.
/// </summary>
/// <remarks>
/// <code>
/// "Metrics": { "Path": "/metrics", "RequireServiceToken": false, "MaxSeriesPerMetric": 64 }
/// </code>
/// </remarks>
public sealed class MetricsOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Metrics";

    /// <summary>Default scrape route.</summary>
    public const string DefaultPath = "/metrics";

    /// <summary>Route the scrape endpoint is mapped on. A leading slash is added when missing.</summary>
    public string Path { get; set; } = DefaultPath;

    /// <summary>
    /// When true the scrape endpoint requires the same service token as the admin API. Off by default,
    /// because the endpoint is normally reachable only from the cluster's scrape network.
    /// </summary>
    public bool RequireServiceToken { get; set; }

    /// <summary>
    /// Per-family cardinality cap for the registry the composition root builds. Kept here so the cap is
    /// configurable without another options type.
    /// </summary>
    public int MaxSeriesPerMetric { get; set; } = MetricsRegistry.DefaultMaxSeriesPerMetric;

    /// <summary>
    /// Reads the <c>Metrics</c> section, leaving any key that is absent at its default.
    /// </summary>
    public static MetricsOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        IConfigurationSection section = configuration.GetSection(SectionName);
        MetricsOptions options = new();

        string? path = section.GetValue<string?>("Path");
        if (!string.IsNullOrWhiteSpace(path))
        {
            options.Path = NormalizePath(path);
        }

        bool? requireServiceToken = section.GetValue<bool?>("RequireServiceToken");
        if (requireServiceToken.HasValue)
        {
            options.RequireServiceToken = requireServiceToken.Value;
        }

        int? maxSeriesPerMetric = section.GetValue<int?>("MaxSeriesPerMetric");
        if (maxSeriesPerMetric.HasValue && maxSeriesPerMetric.Value >= 1)
        {
            options.MaxSeriesPerMetric = maxSeriesPerMetric.Value;
        }

        return options;
    }

    /// <summary>
    /// Resolves the options the endpoint should use. Precedence: an explicitly registered
    /// <see cref="MetricsOptions"/> singleton, then <c>Configure&lt;MetricsOptions&gt;()</c> if the
    /// composition root used it, then the <c>Metrics</c> configuration section, then defaults.
    /// </summary>
    public static MetricsOptions Resolve(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        MetricsOptions? registered = services.GetService<MetricsOptions>();
        if (registered is not null)
        {
            return registered;
        }

        // Only trust IOptions when someone actually configured it; the container hands out a
        // default-constructed instance otherwise, which would silently shadow appsettings.
        foreach (IConfigureOptions<MetricsOptions> _ in services.GetServices<IConfigureOptions<MetricsOptions>>())
        {
            IOptions<MetricsOptions>? configured = services.GetService<IOptions<MetricsOptions>>();
            if (configured is not null)
            {
                return configured.Value;
            }

            break;
        }

        IConfiguration? configuration = services.GetService<IConfiguration>();
        return configuration is null ? new MetricsOptions() : FromConfiguration(configuration);
    }

    private static string NormalizePath(string path)
    {
        string trimmed = path.Trim();
        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }
}
