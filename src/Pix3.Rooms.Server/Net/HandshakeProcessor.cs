using System.Diagnostics.CodeAnalysis;
using MemoryPack;
using Pix3.Rooms.Protocol;
using Pix3.Rooms.Server.Auth;
using Pix3.Rooms.Server.Rooms;

namespace Pix3.Rooms.Server.Net;

/// <summary>
/// Turns the first frame on a socket into a room membership: version negotiation, join throttle, token
/// validation, room lookup, resume-or-join, <c>WelcomeEvent</c>. Stateless and shared by every connection.
/// </summary>
/// <remarks>
/// <para>
/// The <b>first</b> frame must be <c>HelloCommand</c>; anything else is
/// <see cref="RejectCode.BadRequest"/> and close 4007. A version mismatch must surface as a typed
/// <c>RejectedEvent</c>, never as a decoder error, so the version is compared before anything else is
/// trusted.
/// </para>
/// <para>
/// <b>Version negotiation is by range, not equality.</b> Only a client below
/// <see cref="ProtocolVersion.MinSupported"/> is rejected; anything at or above it runs at
/// <c>min(client, Current)</c>, and that negotiated number — not <see cref="ProtocolVersion.Current"/> — is
/// what <c>WelcomeEvent</c> echoes. Strict equality is right for a shipped game and wrong for a platform
/// hosting other people's bundles: it would break every published game on the day the fabric ships a new
/// version.
/// </para>
/// <para>
/// <b>Resume is tried before join.</b> A 16-byte key is the <i>only</i> credential for re-attaching to a
/// dropped session, and a client never names the id it wants — so a leaked or guessed id buys nothing. A
/// failed resume degrades silently to a fresh join: a stale, wrong or expired key is simply not a resume,
/// not a new error path.
/// </para>
/// <para>
/// <c>WelcomeEvent</c> is queued right after the room hands back its <c>JoinGrant</c>, on this thread. Room
/// logic is single-threaded and drains its queue at tick start, so the room's own join fan-out
/// (<c>RoomVarsChangedEvent</c>, the snapshot, <c>PeerJoinedEvent</c>) lands on the next tick — after this
/// frame, which is the order the protocol requires.
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

    /// <summary>Length of a resume key, in bytes. Anything else is not a resume attempt at all.</summary>
    public const int ResumeKeyLength = 16;

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
    /// <param name="connection">The connection being handshaken. Its identity and id are published here.</param>
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

        if (frame.Length < 1 || frame[0] != MessageTypeIds.HelloCommand)
        {
            return Fail(RejectCode.BadRequest, "the first frame must be a HelloCommand", out reject, out reason);
        }

        HelloCommand? hello;
        try
        {
            hello = MemoryPackSerializer.Deserialize<HelloCommand>(frame.Slice(1));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Undecodable HelloCommand from {RemoteIp}", connection.RemoteIp);
            return Fail(RejectCode.BadRequest, "the HelloCommand could not be decoded", out reject, out reason);
        }

        if (hello is null)
        {
            return Fail(RejectCode.BadRequest, "the HelloCommand payload was empty", out reject, out reason);
        }

        // Range check, not equality: a client announcing a version we have never heard of is served at ours.
        if (!ProtocolVersion.IsSupported(hello.ProtocolVersion))
        {
            _metrics.Increment(NetCounter.ProtocolVersionMismatches);
            return Fail(
                RejectCode.ProtocolVersionMismatch,
                $"this server needs protocol version {ProtocolVersion.MinSupported} or newer; "
                + $"the client speaks {hello.ProtocolVersion}. Update the game build.",
                out reject,
                out reason);
        }

        ushort negotiatedVersion = ProtocolVersion.Negotiate(hello.ProtocolVersion);

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
            // Counted as a missing token here rather than in the validator, because the validator is never
            // reached: the reason still has to land in auth_failures_total{reason}.
            _metrics.OnAuthFailure(AuthFailureCause.MissingToken);
            return Fail(RejectCode.InvalidToken, "no room token was supplied", out reject, out reason);
        }

        // Declared nullable so the validator's [MaybeNullWhen(false)] contract needs no null-forgiving
        // operator; flow analysis knows it is non-null once TryValidate returns true.
        if (!_tokenValidator.TryValidate(hello.Token, roomId, out RoomTokenClaims? claims, out RejectCode tokenReject))
        {
            // Keep the wording coarse: the client learns which of the three token failures it hit and
            // nothing else. The fine-grained cause went to the metrics sink, never to the wire.
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

        // The room's PeerJoinedEvent fan-out reads DisplayName, so it must be final before the join.
        connection.ApplyIdentity(ResolveDisplayName(claims, hello.DisplayName));

        if (!TryAdmit(connection, candidate, hello.ResumeKey, out JoinGrant grant, out RejectCode admitReject))
        {
            return Fail(
                admitReject == RejectCode.None ? RejectCode.RoomFull : admitReject,
                DescribeJoinFailure(admitReject),
                out reject,
                out reason);
        }

        if (!TrySendWelcome(connection, candidate, in grant, negotiatedVersion))
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
    /// Reconciles the room id from the query string with the one in <c>HelloCommand</c>. Either may be
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
            error = "the room id in the URL and in the HelloCommand disagree";
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

    /// <summary>
    /// Resume first, then a fresh join. Both paths end with the connection holding the id the room's grant
    /// reports, before that member becomes visible to anyone.
    /// </summary>
    private bool TryAdmit(
        ClientConnection connection,
        IRoom room,
        byte[]? resumeKey,
        out JoinGrant grant,
        out RejectCode reject)
    {
        grant = default;
        reject = RejectCode.None;

        // A key of the wrong length is not a resume attempt; it is noise, and noise falls through to a
        // fresh join rather than earning an error.
        if (resumeKey is { Length: ResumeKeyLength })
        {
            bool resumed;
            try
            {
                resumed = room.TryResume(connection, resumeKey, out grant, out RejectCode resumeReject);
                if (!resumed && resumeReject != RejectCode.None)
                {
                    // A real refusal (the room is closing or full), not "no such pending session". Surfacing
                    // it beats silently retrying a join that will hit the same wall.
                    reject = resumeReject;
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Room {RoomId} threw while resuming a session from {RemoteIp}",
                    room.Config.RoomId,
                    connection.RemoteIp);
                reject = RejectCode.InternalError;
                return false;
            }

            if (resumed)
            {
                // The grant carries the ORIGINAL client id. Adopting it here — before the member is
                // published to peers and before any inbound frame is attributed — is what makes the resumed
                // session the same session: its entities, its known set and its peers' view all key on it.
                connection.AdoptClientId(grant.ClientId);
                _metrics.Increment(NetCounter.ResumesSucceeded);
                return true;
            }

            _metrics.Increment(NetCounter.ResumesFallbackToJoin);
        }

        // Only now, with a validated token and a resolved room, does this socket consume an id from the
        // monotonic allocator. The room keys its membership on connection.ClientId, so the id has to exist
        // before TryJoin, not after.
        connection.AllocateClientId();

        try
        {
            if (!room.TryJoin(connection, out grant, out RejectCode joinReject))
            {
                reject = joinReject;
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Room {RoomId} threw while admitting client {ClientId}",
                room.Config.RoomId,
                connection.ClientId);
            reject = RejectCode.InternalError;
            return false;
        }

        // The grant is the authority even on the fresh path, where it echoes the id just allocated.
        connection.AdoptClientId(grant.ClientId);
        return true;
    }

    /// <summary>
    /// Builds and queues the <c>WelcomeEvent</c> from the room's immutable config plus the grant.
    /// </summary>
    /// <remarks>
    /// Every value here comes from one of exactly two places: <see cref="IRoom.Config"/>, which is an
    /// immutable record fixed at creation, or the <see cref="JoinGrant"/> the room just handed back. Nothing
    /// is read out of live room state — this runs on a socket thread while the room's own thread is ticking,
    /// and a torn read of a mutable field is exactly the kind of bug that only shows up under load.
    /// </remarks>
    private bool TrySendWelcome(
        ClientConnection connection,
        IRoom room,
        in JoinGrant grant,
        ushort negotiatedVersion)
    {
        RoomConfig config = room.Config;

        // The key is the only credential a resume ever presents, so a room that mints the wrong length has
        // silently disabled reconnect for this session. Logged rather than fatal: the client still gets a
        // working session, it just cannot resume it.
        byte[] resumeKey = grant.ResumeKey ?? [];
        if (resumeKey.Length != ResumeKeyLength)
        {
            _logger.LogWarning(
                "Room {RoomId} issued a {Length}-byte resume key for client {ClientId}; the protocol requires {Required}",
                config.RoomId,
                resumeKey.Length,
                grant.ClientId,
                ResumeKeyLength);
        }

        var welcome = new WelcomeEvent
        {
            ClientId = grant.ClientId,
            RoomId = config.RoomId,
            TickHz = (byte)Math.Clamp(config.TickHz, 0, byte.MaxValue),
            ServerTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ServerTick = grant.ServerTick,
            AoiRadius = config.AoiRadius,
            MaxPlayers = (ushort)Math.Clamp(config.MaxPlayers, 0, ushort.MaxValue),
            ProtocolVersion = negotiatedVersion,
            WorldOriginX = config.WorldOriginX,
            WorldOriginY = config.WorldOriginY,
            WorldSize = config.WorldSize,
            Mode = (byte)config.Mode,
            MaxVisibleEntities = (ushort)Math.Clamp(config.MaxVisibleEntities, 0, ushort.MaxValue),
            HostClientId = grant.HostClientId,
            ResumeKey = resumeKey,
            Resumed = grant.Resumed,
        };

        OutboundFrame frame;
        try
        {
            frame = FramePool.EncodeControl(MessageTypeIds.WelcomeEvent, welcome);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not encode the WelcomeEvent for client {ClientId}", grant.ClientId);
            return false;
        }

        // Control lane: the welcome is the one frame the whole session depends on, and a client that never
        // sees it has no id, no world bounds and no resume key.
        if (connection.TryEnqueue(frame, FrameLane.Control))
        {
            return true;
        }

        // OWNERSHIP: the enqueue failed, so the buffer is still ours to return.
        FramePool.Return(frame.Buffer);
        _logger.LogWarning("Client {ClientId} could not be sent its WelcomeEvent", grant.ClientId);
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
        RejectCode.InternalError => "the room could not admit this client",
        _ => "the room refused the join",
    };
}
