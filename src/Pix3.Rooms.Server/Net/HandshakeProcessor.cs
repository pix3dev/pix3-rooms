using System.Diagnostics.CodeAnalysis;
using MemoryPack;
using Pix3.Rooms.Protocol;
using Pix3.Rooms.Server.Auth;
using Pix3.Rooms.Server.Rooms;

namespace Pix3.Rooms.Server.Net;

/// <summary>
/// Turns the first frame on a socket into a room membership: version check, join throttle, token
/// validation, room lookup, join, <c>WelcomeEvent</c>. Stateless and shared by every connection.
/// </summary>
/// <remarks>
/// <para>
/// The <b>first</b> frame must be <c>HelloRequest</c>; anything else is
/// <see cref="RejectCode.BadRequest"/> and close 4007. A version mismatch must surface as a typed
/// <c>RejectEvent</c>, never as a decoder error, so the version is compared before anything else is
/// trusted.
/// </para>
/// <para>
/// <c>WelcomeEvent</c> is queued right after <c>IRoom.TryJoin</c> returns, on this thread. Room logic is
/// single-threaded and drains its queue at tick start, so the room's own join fan-out (<c>RoomVarsEvent</c>,
/// the snapshot, <c>PeerJoinedEvent</c>) lands on the next tick — after this frame, which is the order
/// the protocol requires.
/// </para>
/// </remarks>
public sealed class HandshakeProcessor
{
    /// <summary>Longest accepted display name; longer names are truncated, not rejected.</summary>
    public const int MaxDisplayNameLength = 32;

    /// <summary>Longest accepted room id, in characters.</summary>
    public const int MaxRoomIdLength = 128;

    /// <summary>Name used when neither the token nor the client supplied a usable one.</summary>
    public const string DefaultDisplayName = "player";

    /// <summary>Name used for guest identities with no usable name.</summary>
    public const string DefaultGuestDisplayName = "guest";

    private readonly IRoomTokenValidator _tokenValidator;
    private readonly IRoomManager _roomManager;
    private readonly IpConnectionLimiter _ipLimiter;
    private readonly NetMetrics _metrics;
    private readonly ILogger<HandshakeProcessor> _logger;

    /// <summary>Creates the processor. One instance per process.</summary>
    public HandshakeProcessor(
        IRoomTokenValidator tokenValidator,
        IRoomManager roomManager,
        IpConnectionLimiter ipLimiter,
        NetMetrics metrics,
        ILogger<HandshakeProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(tokenValidator);
        ArgumentNullException.ThrowIfNull(roomManager);
        ArgumentNullException.ThrowIfNull(ipLimiter);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        _tokenValidator = tokenValidator;
        _roomManager = roomManager;
        _ipLimiter = ipLimiter;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>
    /// Processes the first frame of a session. On true the client is a room member and
    /// <c>WelcomeEvent</c> is queued; on false the caller closes with <paramref name="reject"/> and
    /// <paramref name="reason"/>.
    /// </summary>
    /// <param name="connection">The connection being handshaken. Its identity is published here.</param>
    /// <param name="frame">The complete first frame, TypeId byte included.</param>
    /// <param name="room">The joined room, on success.</param>
    /// <param name="reject">Why the handshake failed; <see cref="RejectCode.None"/> on success.</param>
    /// <param name="reason">Human-readable detail for the client. Never leaks server internals.</param>
    public bool TryProcess(
        ClientConnection connection,
        ReadOnlySpan<byte> frame,
        [NotNullWhen(true)] out IRoom? room,
        out RejectCode reject,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(connection);

        room = null;
        reject = RejectCode.BadRequest;
        reason = "";

        if (frame.Length < 1 || frame[0] != MessageTypeIds.HelloRequest)
        {
            return Fail(RejectCode.BadRequest, "the first frame must be a HelloRequest", out reject, out reason);
        }

        HelloRequest? hello;
        try
        {
            hello = MemoryPackSerializer.Deserialize<HelloRequest>(frame.Slice(1));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Undecodable HelloRequest from {RemoteIp}", connection.RemoteIp);
            return Fail(RejectCode.BadRequest, "the HelloRequest could not be decoded", out reject, out reason);
        }

        if (hello is null)
        {
            return Fail(RejectCode.BadRequest, "the HelloRequest payload was empty", out reject, out reason);
        }

        if (hello.ProtocolVersion != ProtocolVersion.Current)
        {
            return Fail(
                RejectCode.ProtocolVersionMismatch,
                $"this server speaks protocol version {ProtocolVersion.Current}, the client asked for {hello.ProtocolVersion}",
                out reject,
                out reason);
        }

        if (!TryResolveRoomId(hello.RoomId, connection.RequestedRoomId, out string roomId, out string roomIdError))
        {
            return Fail(RejectCode.BadRequest, roomIdError, out reject, out reason);
        }

        if (!_ipLimiter.TryAcquireJoin(connection.RemoteIp))
        {
            _metrics.Increment(NetCounter.JoinThrottleBreaches);
            _logger.LogWarning("Throttled a join attempt from {RemoteIp} for room {RoomId}", connection.RemoteIp, roomId);
            return Fail(RejectCode.RateLimited, "too many join attempts; try again shortly", out reject, out reason);
        }

        if (string.IsNullOrEmpty(hello.Token))
        {
            return Fail(RejectCode.InvalidToken, "no room token was supplied", out reject, out reason);
        }

        // Declared nullable so the validator's [MaybeNullWhen(false)] contract needs no null-forgiving
        // operator; flow analysis knows it is non-null once TryValidate returns true.
        if (!_tokenValidator.TryValidate(hello.Token, roomId, out RoomTokenClaims? claims, out RejectCode tokenReject))
        {
            // Keep the wording coarse: the client learns which of the three token failures it hit and
            // nothing else.
            return Fail(
                tokenReject == RejectCode.None ? RejectCode.InvalidToken : tokenReject,
                DescribeTokenFailure(tokenReject),
                out reject,
                out reason);
        }

        if (!_roomManager.TryGet(roomId, out IRoom? candidate))
        {
            return Fail(RejectCode.RoomNotFound, "no such room on this server", out reject, out reason);
        }

        // The room's PeerJoinedEvent fan-out reads DisplayName, so it must be final before TryJoin.
        connection.ApplyIdentity(ResolveDisplayName(claims, hello.DisplayName));

        bool joined;
        try
        {
            joined = candidate.TryJoin(connection, out RejectCode joinReject);
            if (!joined)
            {
                return Fail(
                    joinReject == RejectCode.None ? RejectCode.RoomFull : joinReject,
                    DescribeJoinFailure(joinReject),
                    out reject,
                    out reason);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Room {RoomId} threw while admitting client {ClientId}", roomId, connection.ClientId);
            return Fail(RejectCode.InternalError, "the room could not admit this client", out reject, out reason);
        }

        if (!TrySendWelcome(connection, candidate))
        {
            SafeLeave(candidate, connection.ClientId);
            return Fail(RejectCode.InternalError, "the welcome could not be delivered", out reject, out reason);
        }

        room = candidate;
        reject = RejectCode.None;
        reason = "";
        _metrics.Increment(NetCounter.HandshakesSucceeded);
        return true;
    }

    /// <summary>
    /// Reconciles the room id from the query string with the one in <c>HelloRequest</c>. Either may be
    /// omitted, but if both are present they must agree — a disagreement means a confused or hostile
    /// client, not a default worth guessing.
    /// </summary>
    public static bool TryResolveRoomId(string helloRoomId, string queryRoomId, out string roomId, out string error)
    {
        ArgumentNullException.ThrowIfNull(helloRoomId);
        ArgumentNullException.ThrowIfNull(queryRoomId);

        string fromHello = helloRoomId.Trim();
        string fromQuery = queryRoomId.Trim();

        if (fromHello.Length > 0 && fromQuery.Length > 0 && !string.Equals(fromHello, fromQuery, StringComparison.Ordinal))
        {
            roomId = "";
            error = "the room id in the URL and in the HelloRequest disagree";
            return false;
        }

        roomId = fromHello.Length > 0 ? fromHello : fromQuery;
        if (roomId.Length == 0)
        {
            error = "no room id was supplied";
            return false;
        }

        if (roomId.Length > MaxRoomIdLength)
        {
            roomId = "";
            error = $"the room id is longer than {MaxRoomIdLength} characters";
            return false;
        }

        error = "";
        return true;
    }

    /// <summary>
    /// Picks the name the room will show. A name asserted by the token issuer wins over the
    /// client-supplied one, because only the issuer's is authenticated.
    /// </summary>
    public static string ResolveDisplayName(RoomTokenClaims claims, string requestedName)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(requestedName);

        string issued = claims.DisplayName ?? "";
        string candidate = issued.Length > 0 ? issued : requestedName;
        string sanitized = SanitizeDisplayName(candidate);
        if (sanitized.Length > 0)
        {
            return sanitized;
        }

        return claims.IsGuest ? DefaultGuestDisplayName : DefaultDisplayName;
    }

    /// <summary>
    /// Strips control characters (they would corrupt any UI that renders the name), collapses
    /// surrounding whitespace and truncates to <see cref="MaxDisplayNameLength"/>. Returns the input
    /// unchanged, without allocating, when it is already clean.
    /// </summary>
    public static string SanitizeDisplayName(string candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        ReadOnlySpan<char> trimmed = candidate.AsSpan().Trim();
        bool clean = trimmed.Length <= MaxDisplayNameLength;
        for (int i = 0; clean && i < trimmed.Length; i++)
        {
            if (char.IsControl(trimmed[i]))
            {
                clean = false;
            }
        }

        if (clean)
        {
            return trimmed.Length == candidate.Length ? candidate : new string(trimmed);
        }

        Span<char> buffer = stackalloc char[MaxDisplayNameLength];
        int written = 0;
        for (int i = 0; i < trimmed.Length && written < MaxDisplayNameLength; i++)
        {
            char c = trimmed[i];
            if (char.IsControl(c))
            {
                continue;
            }

            buffer[written++] = c;
        }

        // A truncation must not end on a lone high surrogate, or the name is invalid UTF-16.
        if (written > 0 && char.IsHighSurrogate(buffer[written - 1]))
        {
            written--;
        }

        return new string(buffer.Slice(0, written)).Trim();
    }

    private bool TrySendWelcome(ClientConnection connection, IRoom room)
    {
        RoomConfig config = room.Config;
        uint serverTick;
        try
        {
            serverTick = room.SnapshotStats().ServerTick;
        }
        catch (Exception ex)
        {
            // A stats hiccup must not cost the client its session; the tick is advisory here.
            _logger.LogWarning(ex, "Room {RoomId} could not report its tick for a welcome", config.RoomId);
            serverTick = 0;
        }

        var welcome = new WelcomeEvent
        {
            ClientId = connection.ClientId,
            RoomId = config.RoomId,
            TickHz = (byte)Math.Clamp(config.TickHz, 0, byte.MaxValue),
            ServerTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ServerTick = serverTick,
            AoiRadius = config.AoiRadius,
            MaxPlayers = (ushort)Math.Clamp(config.MaxPlayers, 0, ushort.MaxValue),
            ProtocolVersion = ProtocolVersion.Current,
        };

        OutboundFrame frame;
        try
        {
            frame = FramePool.EncodeControl(MessageTypeIds.WelcomeEvent, welcome);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not encode the WelcomeEvent for client {ClientId}", connection.ClientId);
            return false;
        }

        if (connection.TryEnqueue(frame))
        {
            return true;
        }

        FramePool.Return(frame.Buffer);
        _logger.LogWarning("Client {ClientId} could not be sent its WelcomeEvent", connection.ClientId);
        return false;
    }

    private void SafeLeave(IRoom room, uint clientId)
    {
        try
        {
            room.Leave(clientId, LeaveReason.Error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Room {RoomId} threw while rolling back the join of client {ClientId}", room.Config.RoomId, clientId);
        }
    }

    private bool Fail(RejectCode code, string detail, out RejectCode reject, out string reason)
    {
        reject = code;
        reason = detail;
        _metrics.Increment(NetCounter.HandshakesRejected);
        _metrics.OnReject(code);
        return false;
    }

    private static string DescribeTokenFailure(RejectCode reject) => reject switch
    {
        RejectCode.TokenExpired => "the room token has expired",
        RejectCode.TokenRoomMismatch => "the room token was issued for a different room",
        _ => "the room token is not valid",
    };

    private static string DescribeJoinFailure(RejectCode reject) => reject switch
    {
        RejectCode.RoomClosing => "the room is shutting down",
        RejectCode.RoomFull => "the room is full",
        RejectCode.SessionReplaced => "this identity is already connected",
        _ => "the room refused the join",
    };
}
