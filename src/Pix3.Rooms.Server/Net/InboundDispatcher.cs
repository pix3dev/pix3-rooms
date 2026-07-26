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
/// clients. It allocates nothing on the <c>EntityUpdateFrame</c> path: span reads for validation, one
/// pooled buffer for the hand-off, and a struct <see cref="InboundMessage"/>. MemoryPack only ever
/// touches the low-rate control frames.
/// </para>
/// <para>
/// <b>Only the TypeId is decoded here.</b> The payload stays opaque bytes; the room owns interpreting it.
/// The exception is <c>PingRequest</c>, which is answered on the socket thread precisely so a latency
/// probe does not have to wait for a room tick.
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

        _metrics.Increment(NetCounter.InboundMessages);
        _metrics.Add(NetCounter.InboundBytes, length);

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

        byte typeId = frame[0];
        switch (typeId)
        {
            case MessageTypeIds.EntityUpdateFrame:
                return DispatchEntityUpdateFrame(frame);

            case MessageTypeIds.PingRequest:
                return DispatchPing(frame);

            case MessageTypeIds.LeaveRequest:
                _metrics.Increment(NetCounter.LeaveRequests);
                _connection.MarkVoluntaryLeave();
                _connection.RequestClose(RejectCode.None, "the client left the room");
                return false;

            case MessageTypeIds.EntitySpawnRequest:
                if (!_spawnBucket.TryConsume())
                {
                    // A quota breach on a single request is not worth the session: drop and count.
                    _metrics.Increment(NetCounter.QuotaSpawnBreaches);
                    return true;
                }

                return Forward(typeId, frame);

            case MessageTypeIds.ChatMessageRequest:
                if (!_chatBucket.TryConsume())
                {
                    _metrics.Increment(NetCounter.QuotaChatBreaches);
                    return true;
                }

                return Forward(typeId, frame);

            case MessageTypeIds.EntityDespawnRequest:
            case MessageTypeIds.SetEntityColdPropsRequest:
            case MessageTypeIds.SetRoomVarRequest:
            case MessageTypeIds.RemoteEventRequest:
                return Forward(typeId, frame);

            case MessageTypeIds.HelloRequest:
                return CountProtocolError(typeId, "a second HelloRequest arrived after the handshake");

            default:
                return DispatchUnhandled(typeId);
        }
    }

    private bool DispatchUnhandled(byte typeId)
    {
        if (typeId >= MessageTypeIds.AppRangeFirst)
        {
            // 192–255 belongs to the game, and this server promises never to interpret it. Ignoring it
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

    private bool DispatchEntityUpdateFrame(ReadOnlySpan<byte> frame)
    {
        if (!HotWire.TryReadEntityUpdateFrame(frame, out _, out int count, out ReadOnlySpan<byte> records))
        {
            _metrics.Increment(NetCounter.InboundMalformed);
            return CountProtocolError(MessageTypeIds.EntityUpdateFrame, "malformed EntityUpdateFrame");
        }

        if (count > _maxEntityUpdatesPerFrame)
        {
            // Client batching bug rather than an attack: drop the frame, keep the session.
            _metrics.Increment(NetCounter.QuotaEntityUpdateBreaches);
            return true;
        }

        // Validate at the edge so the room and the replication table never see a truncated record or a
        // mask a client is not allowed to set. All span reads, no allocation.
        int cursor = 0;
        for (int i = 0; i < count; i++)
        {
            if (!HotWire.TryReadDeltaRecord(records.Slice(cursor), out _, out byte mask, out _, out int bytesRead))
            {
                _metrics.Increment(NetCounter.InboundMalformed);
                return CountProtocolError(MessageTypeIds.EntityUpdateFrame, "truncated DeltaRecord");
            }

            if (!HotWire.IsClientMaskLegal(mask))
            {
                _metrics.Increment(NetCounter.InboundMalformed);
                return CountProtocolError(MessageTypeIds.EntityUpdateFrame, "illegal client delta mask");
            }

            cursor += bytesRead;
        }

        return Forward(MessageTypeIds.EntityUpdateFrame, frame);
    }

    private bool DispatchPing(ReadOnlySpan<byte> frame)
    {
        PingRequest? ping;
        try
        {
            ping = MemoryPackSerializer.Deserialize<PingRequest>(frame.Slice(1));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Client {ClientId} sent an undecodable PingRequest", _connection.ClientId);
            _metrics.Increment(NetCounter.InboundMalformed);
            return CountProtocolError(MessageTypeIds.PingRequest, "undecodable PingRequest");
        }

        if (ping is null)
        {
            _metrics.Increment(NetCounter.InboundMalformed);
            return CountProtocolError(MessageTypeIds.PingRequest, "empty PingRequest payload");
        }

        var pong = new PongEvent
        {
            ClientTimeMs = ping.ClientTimeMs,
            ServerTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ServerTick = ReadServerTick(),
        };

        OutboundFrame reply = FramePool.EncodeControl(MessageTypeIds.PongEvent, pong);
        if (!_connection.TryEnqueue(reply))
        {
            FramePool.Return(reply.Buffer);
        }

        _metrics.Increment(NetCounter.PingsAnswered);
        _consecutiveProtocolErrors = 0;
        return true;
    }

    private uint ReadServerTick()
    {
        try
        {
            return _room.SnapshotStats().ServerTick;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Room {RoomId} could not report its tick for a pong", _room.Config.RoomId);
            return 0;
        }
    }

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
            FramePool.Return(buffer);
            _dropped++;
            _metrics.Increment(NetCounter.InboundDroppedRoomQueueFull);
            return true;
        }

        _forwarded++;
        _metrics.Increment(NetCounter.InboundEnqueued);
        _consecutiveProtocolErrors = 0;
        return true;
    }

    /// <summary>
    /// Counts one protocol error and keeps the connection alive — an unknown TypeId is how forward
    /// compatibility looks from an older server. A <i>stream</i> of them is abuse, so the session dies
    /// once <see cref="NetOptions.MaxConsecutiveProtocolErrors"/> consecutive frames fail.
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
        or MessageTypeIds.RejectEvent
        or MessageTypeIds.PongEvent
        or MessageTypeIds.PeerJoinedEvent
        or MessageTypeIds.PeerLeftEvent
        or MessageTypeIds.RoomInfoEvent
        or MessageTypeIds.ChatMessageEvent
        or MessageTypeIds.RoomVarsEvent
        or MessageTypeIds.EntitySpawnAckEvent
        or MessageTypeIds.SnapshotFrame
        or MessageTypeIds.DeltaFrame
        or MessageTypeIds.EntityColdPropsEvent
        or MessageTypeIds.RemoteEventBroadcast;
}
