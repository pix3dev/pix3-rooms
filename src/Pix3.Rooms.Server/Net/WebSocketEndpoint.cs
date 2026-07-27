using System.Net.WebSockets;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Pix3.Rooms.Server.Auth;

namespace Pix3.Rooms.Server.Net;

/// <summary>
/// The <c>/ws</c> handler. Rejects anything that is not a WebSocket upgrade, applies the whole pre-auth
/// gate <b>before</b> accepting, then hands the socket to a <see cref="ClientConnection"/> for its lifetime.
/// </summary>
/// <remarks>
/// <para>
/// The composition root maps it, for example
/// <c>app.MapGet("/ws", (WebSocketEndpoint e, HttpContext c) =&gt; e.HandleAsync(c))</c>, after
/// <c>app.UseWebSockets()</c>. Nothing in this class registers itself.
/// </para>
/// <para>
/// <b>Order of admission control matters.</b> Origin, then the process-wide connection cap, then the per-IP
/// connect-rate bucket, then the per-IP pre-auth cap, then the per-IP connection cap — cheapest and most
/// selective first, and every one of them before the upgrade, so a refused client costs one HTTP response
/// and never a socket, a buffer or a task.
/// </para>
/// <para>
/// <b>No token in the query string.</b> Only <c>?room=</c> is read here. A token in a URL is written to
/// access logs, proxy logs and <c>Referer</c> headers on every hop; it travels in the first frame instead.
/// </para>
/// </remarks>
public sealed class WebSocketEndpoint
{
    /// <summary>Query-string parameter carrying the room id: <c>/ws?room=&lt;id&gt;</c>.</summary>
    public const string RoomQueryParameter = "room";

    /// <summary>Seconds suggested to a client that was refused for a capacity reason.</summary>
    public const int RetryAfterSeconds = 5;

    /// <summary>
    /// Transport keepalive: how often a protocol-level ping is sent on an otherwise silent socket.
    /// </summary>
    /// <remarks>
    /// 15 s so pings keep flowing through a throttled browser tab, where application timers are cut to once
    /// per second and then once per <i>minute</i>. The application-level idle timeout covers a peer that is
    /// alive but not playing; this covers a peer that is playing but silent.
    /// </remarks>
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a keepalive ping may go unanswered before the socket is considered dead.
    /// </summary>
    /// <remarks>
    /// Without a timeout, pings are written into the void: a mobile socket whose radio dropped stays "open"
    /// until TCP eventually gives up, which on some paths is minutes, and for all of that time the room
    /// holds a member and its entities. The interval alone detects nothing — the timeout is what turns it
    /// into liveness detection.
    /// </remarks>
    private static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(15);

    private readonly NetOptions _netOptions;
    private readonly QuotaOptions _quotas;
    private readonly IOriginPolicy _originPolicy;
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
        IOriginPolicy originPolicy,
        IpConnectionLimiter ipLimiter,
        ConnectionSupervisor supervisor,
        HandshakeProcessor handshakeProcessor,
        NetMetrics metrics,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(netOptions);
        ArgumentNullException.ThrowIfNull(quotas);
        ArgumentNullException.ThrowIfNull(originPolicy);
        ArgumentNullException.ThrowIfNull(ipLimiter);
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(handshakeProcessor);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _netOptions = netOptions;
        _quotas = quotas;
        _originPolicy = originPolicy;
        _ipLimiter = ipLimiter;
        _supervisor = supervisor;
        _handshakeProcessor = handshakeProcessor;
        _metrics = metrics;
        _logger = loggerFactory.CreateLogger<WebSocketEndpoint>();
        _connectionLogger = loggerFactory.CreateLogger<ClientConnection>();
    }

    /// <summary>
    /// Serves one request: upgrade check, origin allowlist, address resolution, capacity and pre-auth
    /// checks, accept, then run the session to completion. Returns when the socket is closed and its room
    /// membership released.
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

        // Before anything else, and before any per-IP accounting: a cross-site upgrade must not even get to
        // consume a quota slot. This is the whole defence against cross-site WebSocket hijacking — the
        // upgrade is exempt from the same-origin policy but still carries the visitor's cookies.
        string? origin = ReadOrigin(context);
        if (!_originPolicy.IsAllowed(origin))
        {
            _metrics.Increment(NetCounter.ConnectionsRejectedOrigin);
            _logger.LogWarning("Refused an upgrade from origin {Origin}", origin);
            await WriteProblemAsync(
                context,
                StatusCodes.Status403Forbidden,
                "This origin is not allowed to open a socket.").ConfigureAwait(false);
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

        PreAuthLease? preAuthLease = null;
        bool ipSlotHeld = false;
        try
        {
            // Churn, not concurrency: an address that opens and abandons sockets in a loop never exceeds the
            // connection cap, yet every cycle costs an accept, a buffer and a task.
            if (!_ipLimiter.TryAcquireNewConnection(remoteIp))
            {
                _metrics.Increment(NetCounter.ConnectionsRejectedConnectRate);
                _logger.LogWarning("Refused {RemoteIp}: it is opening connections too quickly", remoteIp);
                context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString();
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status429TooManyRequests,
                    "Too many connection attempts from this address.").ConfigureAwait(false);
                return;
            }

            if (!_ipLimiter.TryAcquirePreAuth(remoteIp, out preAuthLease))
            {
                _metrics.Increment(NetCounter.ConnectionsRejectedPreAuthCap);
                _logger.LogWarning(
                    "Refused {RemoteIp}: it already holds {Cap} unauthenticated connection(s)",
                    remoteIp,
                    _netOptions.MaxPreAuthConnectionsPerIp);
                context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString();
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status429TooManyRequests,
                    "Too many pending handshakes from this address.").ConfigureAwait(false);
                return;
            }

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
            await RunSessionAsync(context, remoteIp, preAuthLease).ConfigureAwait(false);
        }
        finally
        {
            // Idempotent: the connection releases this the instant it authenticates, so this call only does
            // real work for a socket that never got that far.
            preAuthLease?.Release();

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
            // Over-long values are treated as absent; the HelloCommand is the authoritative source and
            // will produce a typed rejection if it disagrees.
            return trimmed.Length <= HandshakeProcessor.MaxRoomIdLength ? trimmed : "";
        }

        return "";
    }

    /// <summary>
    /// The request's <c>Origin</c>, or null when it was not sent. A browser always sends it on an upgrade;
    /// a non-browser client does not, and has no ambient credentials to hijack.
    /// </summary>
    public static string? ReadOrigin(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        StringValues header = context.Request.Headers[HeaderNames.Origin];
        if (header.Count == 0)
        {
            return null;
        }

        // More than one Origin header is not a thing a browser does; refusing to guess which one to trust is
        // safer than picking, and the policy rejects a value it cannot normalise.
        return header.Count == 1 ? header[0] : "";
    }

    private async Task RunSessionAsync(HttpContext context, string remoteIp, PreAuthLease preAuthLease)
    {
        string roomId = ReadRoomQuery(context);

        WebSocket socket;
        try
        {
            // NEVER enable permessage-deflate here. Two independent reasons, both of which have bitten
            // real servers: a zlib context is 64-316 KiB PER CONNECTION (at 4096 connections that is up to
            // 1.3 GiB of pure compression state), and context takeover makes a frame depend on the frames
            // before it, which breaks both memcpy-many fan-out and the move to self-contained WebTransport
            // datagrams that the whole hot plane is designed around. The protocol lists it as actively
            // refused, with a handshake test asserting no Sec-WebSocket-Extensions in the 101. Leaving
            // DangerousEnableCompression unset is what keeps that true — the temptation to "save bandwidth"
            // on a plane that is already quantized to 8 bytes per entity is not worth any of it.
            socket = await context.WebSockets
                .AcceptWebSocketAsync(new WebSocketAcceptContext
                {
                    KeepAliveInterval = KeepAliveInterval,
                    KeepAliveTimeout = KeepAliveTimeout,
                })
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
            preAuthLease,
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
