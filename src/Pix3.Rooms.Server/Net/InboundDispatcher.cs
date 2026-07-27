using MemoryPack;
using Pix3.Rooms.Protocol;
using Pix3.Rooms.Server.Rooms;

namespace Pix3.Rooms.Server.Net;

/// <summary>
/// Decides what happens to each frame a joined client sends: rate limits, structural validation, then a
/// hand-off to the room's inbound queue. One instance per connection, driven only by that connection's
/// receive loop — so its rate-limit state needs no synchronisation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hot path.</b> <see cref="Dispatch"/> runs once per inbound frame, i.e. per tick per client at 600
/// clients. It allocates nothing on the <c>EntityUpdatePacket</c> path: span reads for validation, one
/// pooled buffer for the hand-off, and a struct <see cref="InboundMessage"/>. MemoryPack only ever
/// touches the low-rate control frames.
/// </para>
/// <para>
/// <b>Only the TypeId is decoded here</b>, with two deliberate exceptions. <c>PingCommand</c> is answered
/// on the socket thread precisely so a latency probe does not have to wait for a room tick, and
/// <c>EmitSignalCommand</c> is decoded far enough to read its <c>Target</c> byte — the three signal quotas
/// differ by two orders of magnitude between targets (20/s to the server, 2/s to all peers), so they cannot
/// be applied without knowing which one a frame is asking for. Everything else stays opaque bytes; the room
/// owns interpreting them.
/// </para>
/// <para>
/// <b>What is deliberately NOT enforced here.</b> Per-entity and per-room limits — cold-prop rate and size
/// per entity, entities per owner, room-var count and size, chat length, the entity-kind allowlist — need
/// state only the room has. Duplicating a weaker copy of them at the edge would create two answers to the
/// same question.
/// </para>
/// </remarks>
public sealed class InboundDispatcher
{
    private readonly ClientConnection _connection;
    private readonly IRoom _room;
    private readonly NetMetrics _metrics;
    private readonly ILogger _logger;
    private readonly int _maxPayloadBytes;
    private readonly int _maxEntityUpdatesPerFrame;
    private readonly int _maxConsecutiveProtocolErrors;

    private TokenBucket _messageBucket;
    private TokenBucket _byteBucket;
    private TokenBucket _spawnBucket;
    private TokenBucket _chatBucket;
    private TokenBucket _signalToServerBucket;
    private TokenBucket _signalToAoiBucket;
    private TokenBucket _signalToAllBucket;
    private TokenBucket _resyncBucket;
    private TokenBucket _teleportBucket;

    private int _consecutiveProtocolErrors;
    private long _forwarded;
    private long _dropped;
    private long _protocolErrors;

    /// <summary>Creates the dispatcher for one joined connection.</summary>
    public InboundDispatcher(
        ClientConnection connection,
        IRoom room,
        NetOptions netOptions,
        QuotaOptions quotas,
        NetMetrics metrics,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(netOptions);
        ArgumentNullException.ThrowIfNull(quotas);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        _connection = connection;
        _room = room;
        _metrics = metrics;
        _logger = logger;
        _maxPayloadBytes = quotas.MaxPayloadBytes;
        _maxEntityUpdatesPerFrame = quotas.MaxEntityUpdatesPerFrame;
        _maxConsecutiveProtocolErrors = netOptions.MaxConsecutiveProtocolErrors;

        _messageBucket = TokenBucket.PerSecond(quotas.MaxMessagesPerSecond);
        _byteBucket = TokenBucket.PerSecond(quotas.MaxBytesPerSecond);
        _spawnBucket = TokenBucket.PerMinute(quotas.MaxSpawnsPerMinute);
        _chatBucket = TokenBucket.PerMinute(quotas.MaxChatPerMinute);
        _signalToServerBucket = TokenBucket.PerSecond(quotas.MaxSignalsToServerPerSecond);
        _signalToAoiBucket = TokenBucket.PerSecond(quotas.MaxSignalsToAoiPerSecond);
        _signalToAllBucket = TokenBucket.PerSecond(quotas.MaxSignalsToAllPerSecond);
        _resyncBucket = TokenBucket.PerSecond(quotas.MaxResyncPerSecond);
        _teleportBucket = TokenBucket.PerMinute(quotas.MaxTeleportsPerMinute);
    }

    /// <summary>Frames handed to the room.</summary>
    public long ForwardedMessages => _forwarded;

    /// <summary>Frames dropped because the room's inbound queue was full.</summary>
    public long DroppedMessages => _dropped;

    /// <summary>Frames rejected as malformed, unknown or illegal.</summary>
    public long ProtocolErrors => _protocolErrors;

    /// <summary>Consecutive protocol errors right now; reset by any frame that is handled cleanly.</summary>
    public int ConsecutiveProtocolErrors => _consecutiveProtocolErrors;

    /// <summary>
    /// Handles one complete frame, TypeId byte included. False means the connection is being closed and
    /// the receive loop must stop; the close has already been requested with the right reject code.
    /// </summary>
    public bool Dispatch(ReadOnlySpan<byte> frame)
    {
        int length = frame.Length;
        if (length > _maxPayloadBytes)
        {
            _metrics.Increment(NetCounter.InboundOversized);
            _metrics.Increment(NetCounter.QuotaPayloadBreaches);
            _connection.RequestClose(RejectCode.PayloadTooLarge, $"a message exceeded {_maxPayloadBytes} bytes");
            return false;
        }

        if (length < 1)
        {
            // An empty binary frame carries no TypeId. Not worth a session, but it is not nothing either.
            _metrics.Increment(NetCounter.InboundMalformed);
            return CountProtocolError(0, "an empty binary frame carries no TypeId");
        }

        byte typeId = frame[0];

        _metrics.Increment(NetCounter.InboundMessages);
        _metrics.Add(NetCounter.InboundBytes, length);

        // Counted before any quota or validity decision: messages_in_total{type} is meant to show what a
        // client sends, and this is the only place in the process that still knows a frame's TypeId.
        _metrics.OnInbound(typeId);

        if (!_messageBucket.TryConsume())
        {
            _metrics.Increment(NetCounter.QuotaMessageRateBreaches);
            _connection.RequestClose(RejectCode.RateLimited, "too many messages per second");
            return false;
        }

        if (!_byteBucket.TryConsume(length))
        {
            _metrics.Increment(NetCounter.QuotaByteRateBreaches);
            _connection.RequestClose(RejectCode.RateLimited, "too many bytes per second");
            return false;
        }

        switch (typeId)
        {
            case MessageTypeIds.EntityUpdatePacket:
                return DispatchEntityUpdatePacket(frame);

            case MessageTypeIds.PingCommand:
                return DispatchPing(frame);

            case MessageTypeIds.LeaveCommand:
                _metrics.Increment(NetCounter.LeaveCommands);
                _connection.MarkVoluntaryLeave();
                _connection.RequestClose(RejectCode.None, "the client left the room");
                return false;

            case MessageTypeIds.SpawnEntityRequest:
                if (!_spawnBucket.TryConsume())
                {
                    // A quota breach on a single request is not worth the session: drop and count.
                    _metrics.Increment(NetCounter.QuotaSpawnBreaches);
                    return true;
                }

                return Forward(typeId, frame);

            case MessageTypeIds.SendChatCommand:
                if (!_chatBucket.TryConsume())
                {
                    _metrics.Increment(NetCounter.QuotaChatBreaches);
                    return true;
                }

                return Forward(typeId, frame);

            case MessageTypeIds.EmitSignalCommand:
                return DispatchEmitSignal(frame);

            case MessageTypeIds.ResyncCommand:
                if (!_resyncBucket.TryConsume())
                {
                    // A resync is the one request that can ask the server for real work — a full snapshot and
                    // a restarted continuation cursor — so an excess one is dropped rather than served.
                    _metrics.Increment(NetCounter.QuotaResyncBreaches);
                    return true;
                }

                _metrics.Increment(NetCounter.ResyncRequests);
                return Forward(typeId, frame);

            case MessageTypeIds.SetClientPrefsCommand:
                _metrics.Increment(NetCounter.ClientPrefsUpdates);
                return Forward(typeId, frame);

            case MessageTypeIds.DespawnEntityCommand:
            case MessageTypeIds.SetEntityPropsCommand:
            case MessageTypeIds.SetRoomVarCommand:
                // Cold-prop rate/size, room-var count/size and ownership are per-entity and per-room
                // decisions; the room makes them with state this edge does not have.
                return Forward(typeId, frame);

            case MessageTypeIds.HelloCommand:
                return CountProtocolError(typeId, "a second HelloCommand arrived after the handshake");

            default:
                return DispatchUnhandled(typeId);
        }
    }

    private bool DispatchUnhandled(byte typeId)
    {
        if (typeId >= MessageTypeIds.AppRangeFirst)
        {
            // 192-255 belongs to the game, and this server promises never to interpret it. Ignoring it
            // is the specified behaviour, not an error, so the error streak is not touched.
            _metrics.Increment(NetCounter.InboundAppRangeIgnored);
            return true;
        }

        if (IsServerToClientOnly(typeId))
        {
            _metrics.Increment(NetCounter.InboundServerOnlyTypeId);
            return CountProtocolError(typeId, "this TypeId is server-to-client only");
        }

        _metrics.Increment(NetCounter.InboundUnknownTypeId);
        return CountProtocolError(typeId, "unknown TypeId");
    }

    /// <summary>
    /// Validates a client's <c>EntityUpdatePacket</c> at the edge, so neither the room nor the replication
    /// table ever sees a truncated record or a mask a client is not allowed to set. All span reads, no
    /// allocation.
    /// </summary>
    /// <remarks>
    /// There is deliberately <b>no quantized-range check</b> here: a <c>u16 QX</c>, a <c>u8 QRot</c> and an
    /// <c>i16 QVx</c> are valid quantized values <i>by construction</i> — every bit pattern maps to a real
    /// world coordinate inside the room's bounds. The checks that do matter are structural (does the record
    /// fit), policy (is the mask legal for a client) and ownership (which is the room's, keyed by the
    /// record's <c>NetId</c> including its generation bits).
    /// </remarks>
    private bool DispatchEntityUpdatePacket(ReadOnlySpan<byte> frame)
    {
        if (!HotWire.TryReadEntityUpdatePacket(frame, out _, out int count, out ReadOnlySpan<byte> records))
        {
            _metrics.Increment(NetCounter.InboundMalformed);
            return CountProtocolError(MessageTypeIds.EntityUpdatePacket, "malformed EntityUpdatePacket");
        }

        if (count > _maxEntityUpdatesPerFrame)
        {
            // Client batching bug rather than an attack: drop the frame, keep the session.
            _metrics.Increment(NetCounter.QuotaEntityUpdateBreaches);
            return true;
        }

        int cursor = 0;
        for (int i = 0; i < count; i++)
        {
            if (!HotWire.TryReadOwnerUpdateRecord(records.Slice(cursor), out _, out byte mask, out _, out int bytesRead))
            {
                _metrics.Increment(NetCounter.InboundMalformed);
                return CountProtocolError(MessageTypeIds.EntityUpdatePacket, "truncated OwnerUpdateRecord");
            }

            if (!HotWire.IsClientMaskLegal(mask))
            {
                _metrics.Increment(NetCounter.InboundMalformed);
                return CountProtocolError(MessageTypeIds.EntityUpdatePacket, "illegal client delta mask");
            }

            if ((mask & DeltaMask.Teleport) != 0)
            {
                _metrics.Increment(NetCounter.TeleportBitsSeen);
                if (!_teleportBucket.TryConsume())
                {
                    // Soft quota: counted, never enforced. Under client authority a respawn genuinely is a
                    // discontinuity, so dropping the record would break real gameplay; at Level 2 the server
                    // owns position and the bit is stripped instead of rationed. Build the dataset now.
                    _metrics.Increment(NetCounter.QuotaTeleportBreaches);
                }
            }

            cursor += bytesRead;
        }

        return Forward(MessageTypeIds.EntityUpdatePacket, frame);
    }

    /// <summary>
    /// Applies the per-target signal quota, then forwards. The frame is decoded only far enough to read
    /// <c>Target</c>; the room decodes it properly and owns name/payload limits and focus scoping.
    /// </summary>
    private bool DispatchEmitSignal(ReadOnlySpan<byte> frame)
    {
        EmitSignalCommand? signal;
        try
        {
            signal = MemoryPackSerializer.Deserialize<EmitSignalCommand>(frame.Slice(1));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Client {ClientId} sent an undecodable EmitSignalCommand", _connection.ClientId);
            _metrics.Increment(NetCounter.InboundMalformed);
            return CountProtocolError(MessageTypeIds.EmitSignalCommand, "undecodable EmitSignalCommand");
        }

        if (signal is null)
        {
            _metrics.Increment(NetCounter.InboundMalformed);
            return CountProtocolError(MessageTypeIds.EmitSignalCommand, "empty EmitSignalCommand payload");
        }

        switch ((SignalTarget)signal.Target)
        {
            case SignalTarget.Server:
                if (!_signalToServerBucket.TryConsume())
                {
                    _metrics.Increment(NetCounter.QuotaSignalToServerBreaches);
                    return true;
                }

                break;

            case SignalTarget.AoiPeers:
                if (!_signalToAoiBucket.TryConsume())
                {
                    _metrics.Increment(NetCounter.QuotaSignalToAoiBreaches);
                    return true;
                }

                break;

            case SignalTarget.AllPeers:
            case SignalTarget.SinglePeer:
                // Both share the tightest bucket in the table. AllPeers is a 600x amplifier — one emit becomes
                // one control frame per member with no AOI filtering — and SinglePeer is the same unfiltered
                // path with one recipient, so it is rationed at conversation rate rather than tick rate.
                if (!_signalToAllBucket.TryConsume())
                {
                    _metrics.Increment(NetCounter.QuotaSignalToAllBreaches);
                    return true;
                }

                break;

            default:
                // An undefined target cannot be rate-limited correctly, and forwarding it would ask the room
                // to guess a routing. Refusing is the only answer that keeps the amplifier bounded.
                _metrics.Increment(NetCounter.InboundMalformed);
                return CountProtocolError(MessageTypeIds.EmitSignalCommand, "undefined signal target");
        }

        return Forward(MessageTypeIds.EmitSignalCommand, frame);
    }

    private bool DispatchPing(ReadOnlySpan<byte> frame)
    {
        PingCommand? ping;
        try
        {
            ping = MemoryPackSerializer.Deserialize<PingCommand>(frame.Slice(1));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Client {ClientId} sent an undecodable PingCommand", _connection.ClientId);
            _metrics.Increment(NetCounter.InboundMalformed);
            return CountProtocolError(MessageTypeIds.PingCommand, "undecodable PingCommand");
        }

        if (ping is null)
        {
            _metrics.Increment(NetCounter.InboundMalformed);
            return CountProtocolError(MessageTypeIds.PingCommand, "empty PingCommand payload");
        }

        var pong = new PongEvent
        {
            ClientTimeMs = ping.ClientTimeMs,
            ServerTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ServerTick = ReadServerTick(),
        };

        OutboundFrame reply = FramePool.EncodeControl(MessageTypeIds.PongEvent, pong);
        if (!_connection.TryEnqueue(reply, FrameLane.Control))
        {
            // OWNERSHIP: a failed enqueue leaves the buffer with us. (A full control lane has also just
            // closed the connection, which is why nothing is retried.)
            FramePool.Return(reply.Buffer);
        }

        _metrics.Increment(NetCounter.PingsAnswered);
        _consecutiveProtocolErrors = 0;
        return true;
    }

    /// <summary>
    /// The tick to stamp a pong with. <see cref="IRoom.ServerTick"/> is a volatile read the room's own
    /// thread publishes — deliberately not <c>SnapshotStats()</c>, which allocates a record and samples two
    /// histograms, and would do so once per second per client from a socket thread.
    /// </summary>
    private uint ReadServerTick() => _room.ServerTick;

    /// <summary>Copies the frame into a pooled buffer and hands it to the room. Never the socket's own buffer.</summary>
    private bool Forward(byte typeId, ReadOnlySpan<byte> frame)
    {
        byte[] buffer = FramePool.Rent(frame.Length);
        frame.CopyTo(buffer);

        bool enqueued;
        try
        {
            enqueued = _room.TryEnqueueInbound(new InboundMessage(_connection.ClientId, typeId, buffer, frame.Length));
        }
        catch (Exception ex)
        {
            // OWNERSHIP: the room never took the buffer, so we return it before anything else.
            FramePool.Return(buffer);
            _logger.LogError(
                ex,
                "Room {RoomId} threw while queueing {MessageName} from client {ClientId}",
                _room.Config.RoomId,
                MessageTypeIds.GetName(typeId),
                _connection.ClientId);
            _connection.RequestClose(RejectCode.InternalError, "the room could not accept this message");
            return false;
        }

        if (!enqueued)
        {
            // OWNERSHIP: a refused enqueue transfers nothing; the buffer goes back exactly once, here.
            FramePool.Return(buffer);
            _dropped++;
            _metrics.Increment(NetCounter.InboundDroppedRoomQueueFull);
            return true;
        }

        // OWNERSHIP TRANSFER: the room owns the buffer now and returns it after handling the message.
        _forwarded++;
        _metrics.Increment(NetCounter.InboundEnqueued);
        _consecutiveProtocolErrors = 0;
        return true;
    }

    /// <summary>
    /// Counts one protocol error and keeps the connection alive — an unknown TypeId is how forward
    /// compatibility looks from an older server, and the protocol requires it to be ignored, never fatal.
    /// A <i>stream</i> of them is abuse, so the session dies once
    /// <see cref="NetOptions.MaxConsecutiveProtocolErrors"/> consecutive frames fail.
    /// </summary>
    private bool CountProtocolError(byte typeId, string detail)
    {
        _protocolErrors++;
        _consecutiveProtocolErrors++;

        if (_maxConsecutiveProtocolErrors > 0 && _consecutiveProtocolErrors >= _maxConsecutiveProtocolErrors)
        {
            _metrics.Increment(NetCounter.ProtocolErrorCutoffs);
            _logger.LogWarning(
                "Dropping client {ClientId}: {Errors} consecutive protocol errors, last was TypeId {TypeId} ({Detail})",
                _connection.ClientId,
                _consecutiveProtocolErrors,
                typeId,
                detail);
            _connection.RequestClose(RejectCode.BadRequest, "too many consecutive protocol errors");
            return false;
        }

        _logger.LogDebug(
            "Ignoring TypeId {TypeId} from client {ClientId}: {Detail} ({Errors}/{Max})",
            typeId,
            _connection.ClientId,
            detail,
            _consecutiveProtocolErrors,
            _maxConsecutiveProtocolErrors);
        return true;
    }

    /// <summary>True for TypeIds this server only ever sends; a client using one is out of contract.</summary>
    private static bool IsServerToClientOnly(byte typeId) => typeId
        is MessageTypeIds.WelcomeEvent
        or MessageTypeIds.RejectedEvent
        or MessageTypeIds.PongEvent
        or MessageTypeIds.PeerJoinedEvent
        or MessageTypeIds.PeerLeftEvent
        or MessageTypeIds.RoomInfoEvent
        or MessageTypeIds.ChatMessageEvent
        or MessageTypeIds.RoomVarsChangedEvent
        or MessageTypeIds.HostChangedEvent
        or MessageTypeIds.SpawnEntityResponse
        or MessageTypeIds.SnapshotPacket
        or MessageTypeIds.DeltaPacket
        or MessageTypeIds.EntityPropsChangedEvent
        or MessageTypeIds.SignalEvent
        or MessageTypeIds.SignalBatchPacket;
}
