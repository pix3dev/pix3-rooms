using Microsoft.AspNetCore.Http.HttpResults;
using Pix3.Rooms.Server.Observability;
using Pix3.Rooms.Server.Rooms;

namespace Pix3.Rooms.Server.Admin;

/// <summary>Maps the unauthenticated liveness endpoint. Called by the composition root.</summary>
public static class HealthEndpoints
{
    /// <summary>Route the liveness endpoint is mapped on.</summary>
    public const string HealthPath = "/health";

    /// <summary>Status string reported while the process serves requests.</summary>
    public const string HealthyStatus = "ok";

    /// <summary>
    /// Maps <c>GET /health</c>. Deliberately outside the admin group: liveness must answer without a
    /// service token, and it answers 200 whenever the process is up — it reports room and connection
    /// counts, it does not judge them.
    /// </summary>
    /// <param name="app">Route builder to map onto.</param>
    public static RouteHandlerBuilder MapHealthEndpoint(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app
            .MapGet(HealthPath, (IServiceProvider services) => Health(services))
            .WithName("Health");
    }

    private static Ok<HealthResponse> Health(IServiceProvider services)
    {
        // Optional lookups: a half-composed container must still answer liveness.
        int rooms = services.GetService<IRoomManager>()?.RoomCount ?? 0;

        RoomsMetrics? metrics = services.GetService<RoomsMetrics>();
        int connections = metrics is null ? 0 : ToCount(metrics.ConnectionsActive.Value);

        return TypedResults.Ok(new HealthResponse(
            HealthyStatus,
            ServerRuntimeInfo.UptimeSeconds,
            ServerRuntimeInfo.Version,
            rooms,
            connections));
    }

    private static int ToCount(double gaugeValue)
    {
        if (double.IsNaN(gaugeValue) || gaugeValue <= 0d)
        {
            return 0;
        }

        return gaugeValue >= int.MaxValue ? int.MaxValue : (int)gaugeValue;
    }
}
