using System.Net.WebSockets;
using Microsoft.Extensions.Primitives;

namespace Pix3.Rooms.Server.Net;

/// <summary>
/// The <c>/ws</c> handler. Rejects anything that is not a WebSocket upgrade, applies admission control
/// <b>before</b> accepting, then hands the socket to a <see cref="ClientConnection"/> for its lifetime.
/// </summary>
/// <remarks>
/// The composition root maps it, for example
/// <c>app.MapGet("/ws", (WebSocketEndpoint e, HttpContext c) =&gt; e.HandleAsync(c))</c>, after
/// <c>app.UseWebSockets()</c>. Nothing in this class registers itself.
/// </remarks>
public sealed class WebSocketEndpoint
{
    /// <summary>Query-string parameter carrying the room id: <c>/ws?room=&lt;id&gt;</c>.</summary>
    public const string RoomQueryParameter = "room";

    /// <summary>Seconds suggested to a client that was refused for a capacity reason.</summary>
    public const int RetryAfterSeconds = 5;

    /// <summary>
    /// Transport keepalive. The protocol has no server-to-client ping, so liveness of a *silent* peer is
    /// detected by WebSocket control pings; the application-level idle timeout covers a peer that is
    /// alive but not playing.
    /// </summary>
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    private readonly NetOptions _netOptions;
    private readonly QuotaOptions _quotas;
    private readonly IpConnectionLimiter _ipLimiter;
    private readonly ConnectionSupervisor _supervisor;
    private readonly HandshakeProcessor _handshakeProcessor;
    private readonly NetMetrics _metrics;
    private readonly ILogger<WebSocketEndpoint> _logger;
    private readonly ILogger<ClientConnection> _connectionLogger;

    /// <summary>Creates the endpoint. One instance per process.</summary>
    public WebSocketEndpoint(
        NetOptions netOptions,
        QuotaOptions quotas,
        IpConnectionLimiter ipLimiter,
        ConnectionSupervisor supervisor,
        HandshakeProcessor handshakeProcessor,
        NetMetrics metrics,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(netOptions);
        ArgumentNullException.ThrowIfNull(quotas);
        ArgumentNullException.ThrowIfNull(ipLimiter);
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(handshakeProcessor);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _netOptions = netOptions;
        _quotas = quotas;
        _ipLimiter = ipLimiter;
        _supervisor = supervisor;
        _handshakeProcessor = handshakeProcessor;
        _metrics = metrics;
        _logger = loggerFactory.CreateLogger<WebSocketEndpoint>();
        _connectionLogger = loggerFactory.CreateLogger<ClientConnection>();
    }

    /// <summary>
    /// Serves one request: upgrade check, address resolution, capacity checks, accept, then run the
    /// session to completion. Returns when the socket is closed and its room membership released.
    /// </summary>
    public async Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Makes the deadline sweep independent of whether the supervisor was registered as a hosted
        // service — a socket must never be able to sit past its handshake deadline unnoticed.
        _supervisor.EnsureStarted();

        if (!context.WebSockets.IsWebSocketRequest)
        {
            _metrics.Increment(NetCounter.ConnectionsRejectedNotWebSocket);
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "This endpoint accepts WebSocket upgrade requests only.").ConfigureAwait(false);
            return;
        }

        string remoteIp = RemoteIpResolver.Resolve(context, _netOptions.TrustForwardedHeaders);

        if (!_supervisor.TryReserveSlot())
        {
            _metrics.Increment(NetCounter.ConnectionsRejectedServerCap);
            _logger.LogWarning(
                "Refused {RemoteIp}: the server is at its connection cap of {Cap}",
                remoteIp,
                _netOptions.MaxTotalConnections);
            context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString();
            await WriteProblemAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "The server is at capacity. Try again shortly.").ConfigureAwait(false);
            return;
        }

        bool ipSlotHeld = false;
        try
        {
            if (!_ipLimiter.TryAcquire(remoteIp))
            {
                _metrics.Increment(NetCounter.ConnectionsRejectedIpCap);
                _logger.LogWarning(
                    "Refused {RemoteIp}: it already holds {Cap} connection(s)",
                    remoteIp,
                    _quotas.MaxConnectionsPerIp);
                context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString();
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status429TooManyRequests,
                    "Too many connections from this address.").ConfigureAwait(false);
                return;
            }

            ipSlotHeld = true;
            await RunSessionAsync(context, remoteIp).ConfigureAwait(false);
        }
        finally
        {
            if (ipSlotHeld)
            {
                _ipLimiter.Release(remoteIp);
            }

            _supervisor.ReleaseSlot();
        }
    }

    /// <summary>Reads and length-caps the room id from the query string. Empty when absent or oversized.</summary>
    public static string ReadRoomQuery(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Request.Query.TryGetValue(RoomQueryParameter, out StringValues values))
        {
            return "";
        }

        for (int i = 0; i < values.Count; i++)
        {
            string? value = values[i];
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            string trimmed = value.Trim();
            // Over-long values are treated as absent; the HelloRequest is the authoritative source and
            // will produce a typed rejection if it disagrees.
            return trimmed.Length <= HandshakeProcessor.MaxRoomIdLength ? trimmed : "";
        }

        return "";
    }

    private async Task RunSessionAsync(HttpContext context, string remoteIp)
    {
        string roomId = ReadRoomQuery(context);

        WebSocket socket;
        try
        {
            socket = await context.WebSockets
                .AcceptWebSocketAsync(new WebSocketAcceptContext { KeepAliveInterval = KeepAliveInterval })
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "The upgrade from {RemoteIp} did not complete", remoteIp);
            return;
        }

        _metrics.Increment(NetCounter.ConnectionsAccepted);

        var connection = new ClientConnection(
            socket,
            remoteIp,
            roomId,
            _netOptions,
            _quotas,
            _metrics,
            _handshakeProcessor,
            _connectionLogger);

        using (socket)
        {
            _supervisor.Register(connection);
            try
            {
                await connection.RunAsync(context.RequestAborted).ConfigureAwait(false);
            }
            finally
            {
                _supervisor.Unregister(connection);
            }
        }
    }

    private static async Task WriteProblemAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/plain; charset=utf-8";
        try
        {
            await context.Response.WriteAsync(message, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller hung up before it read the refusal; nothing to do.
        }
    }
}
