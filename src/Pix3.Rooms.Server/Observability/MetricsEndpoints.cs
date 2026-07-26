using Pix3.Rooms.Server.Admin;
using Pix3.Rooms.Server.Rooms;

namespace Pix3.Rooms.Server.Observability;

/// <summary>Maps the Prometheus scrape endpoint. Called by the composition root.</summary>
public static class MetricsEndpoints
{
    /// <summary>
    /// Maps <c>GET /metrics</c> (or <see cref="MetricsOptions.Path"/>), rendering the registered
    /// <see cref="MetricsRegistry"/> in Prometheus text format 0.0.4.
    /// </summary>
    /// <param name="app">Route builder to map onto.</param>
    /// <remarks>
    /// Options come from <see cref="MetricsOptions.Resolve"/>. When
    /// <see cref="MetricsOptions.RequireServiceToken"/> is set, the endpoint is guarded by the same
    /// <see cref="ServiceTokenEndpointFilter"/> as the admin API.
    /// </remarks>
    public static RouteHandlerBuilder MapMetricsEndpoint(this IEndpointRouteBuilder app)
        => MapMetricsEndpoint(app, null);

    /// <summary>Maps the scrape endpoint with explicit options, bypassing configuration lookup.</summary>
    /// <param name="app">Route builder to map onto.</param>
    /// <param name="options">Options to use, or null to resolve them from the container.</param>
    public static RouteHandlerBuilder MapMetricsEndpoint(this IEndpointRouteBuilder app, MetricsOptions? options)
    {
        ArgumentNullException.ThrowIfNull(app);

        MetricsOptions resolved = options ?? MetricsOptions.Resolve(app.ServiceProvider);

        RouteHandlerBuilder builder = app
            .MapGet(resolved.Path, (IServiceProvider services) => Scrape(services))
            .WithName("PrometheusMetrics");

        if (resolved.RequireServiceToken)
        {
            builder.AddEndpointFilter<ServiceTokenEndpointFilter>();
        }

        return builder;
    }

    private static IResult Scrape(IServiceProvider services)
    {
        RoomsMetrics? metrics = services.GetService<RoomsMetrics>();
        MetricsRegistry? registry = services.GetService<MetricsRegistry>() ?? metrics?.Registry;
        if (registry is null)
        {
            // Nothing to expose is a wiring fault, not an empty scrape: say so instead of lying with 200.
            return TypedResults.Text(
                "# no MetricsRegistry or RoomsMetrics is registered in the service container\n",
                PrometheusTextFormatter.ContentType,
                null,
                StatusCodes.Status503ServiceUnavailable);
        }

        if (metrics is not null && services.GetService<IRoomManager>() is IRoomManager manager)
        {
            // Room-scoped gauges are derived, not incremented: read them straight off the registry.
            metrics.RefreshRoomGauges(manager);
        }

        return new PrometheusScrapeResult(registry);
    }

    /// <summary>Streams the exposition straight into the response body writer.</summary>
    private sealed class PrometheusScrapeResult : IResult
    {
        private readonly MetricsRegistry _registry;

        internal PrometheusScrapeResult(MetricsRegistry registry) => _registry = registry;

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            httpContext.Response.ContentType = PrometheusTextFormatter.ContentType;

            PrometheusTextFormatter.Write(_registry, httpContext.Response.BodyWriter);
            await httpContext.Response.BodyWriter.FlushAsync(httpContext.RequestAborted).ConfigureAwait(false);
        }
    }
}
