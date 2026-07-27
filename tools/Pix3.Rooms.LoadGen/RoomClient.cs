using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using MemoryPack;
using Pix3.Rooms.Protocol;

namespace Pix3.Rooms.LoadGen;

/// <summary>
/// One client session speaking protocol v2 over a WebSocket: handshake, spawn, entity updates, and the
/// full inbound decode with <c>Seq</c> tracking and automatic resync.
/// </summary>
/// <remarks>
/// <para>
/// This is the reference client for the C# side, and deliberately the <i>only</i> one: the load
/// generator drives it at scale and the end-to-end tests drive it for correctness, so a load run cannot
/// be measuring a client that quietly disagrees with the protocol. It validates while it runs — a delta
/// for a slot no full record ever introduced, a <c>Seq</c> gap or a malformed frame is recorded in
/// <see cref="Metrics"/> rather than ignored, because a load test that silently accepts broken frames
/// measures nothing.
/// </para>
/// <para>
/// It is a client, not a server: the receive loop allocates a decoded record here and there. What it
/// must not do is become the bottleneck, so the hot path keeps one fixed receive buffer, no LINQ and no
/// per-frame allocation beyond the known-set dictionary.
/// </para>
/// </remarks>
public sealed class RoomClient : IAsyncDisposable
{
    private const int ReceiveBufferBytes = 64 * 1024;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly ClientWebSocket _socket = new();
    private readonly Uri _uri;
    private readonly string _roomId;
    private readonly string _displayName;
    private readonly string _token;

    private readonly TaskCompletionSource<WelcomeEvent> _welcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<TaskCompletionSource<SpawnEntityResponse>> _spawnWaiters = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Lock _gate = new();

    /// <summary>slot → netId, exactly what the protocol says a client's known set is.</summary>
    private readonly Dictionary<ushort, uint> _known = [];

    private readonly CancellationTokenSource _closing = new();
    private Task? _receiveLoop;
    private int _lastSeq = -1;
    private long _pingSentTimestamp;

    /// <summary>Creates an unconnected client. Call <see cref="ConnectAsync"/> once.</summary>
    /// <param name="baseUri">The server's HTTP base address, e.g. <c>http://127.0.0.1:5011</c>.</param>
    /// <param name="roomId">Room to join; also the room the dev token is scoped to.</param>
    /// <param name="displayName">Name announced in the handshake and echoed to peers.</param>
    /// <param name="token">Room token. Defaults to the development <c>dev:&lt;name&gt;:&lt;roomId&gt;</c> form.</param>
    public RoomClient(Uri baseUri, string roomId, string displayName, string? token = null)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        _roomId = roomId;
        _displayName = displayName;
        _token = token ?? $"dev:{displayName}:{roomId}";

        UriBuilder builder = new(baseUri)
        {
            Scheme = baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = "/ws",
            Query = $"room={Uri.EscapeDataString(roomId)}",
        };
        _uri = builder.Uri;
    }

    /// <summary>Everything this session's counters say about what it received.</summary>
    public ClientMetrics Metrics { get; } = new();

    /// <summary>The id the room assigned, preserved across a successful resume.</summary>
    public uint ClientId { get; private set; }

    /// <summary>The room's host at welcome time, or 0.</summary>
    public uint HostClientId { get; private set; }

    /// <summary>Room tick rate, from the welcome.</summary>
    public byte TickHz { get; private set; }

    /// <summary>The negotiated session version — <c>min(client, server)</c>.</summary>
    public ushort NegotiatedVersion { get; private set; }

    /// <summary>Per-client cap on how many entities the server will tell this client about at once.</summary>
    public ushort MaxVisibleEntities { get; private set; }

    /// <summary>True when the welcome answered a successful resume, so local state must be kept.</summary>
    public bool Resumed { get; private set; }

    /// <summary>The 16-byte resume credential, regenerated on every connect.</summary>
    public byte[] ResumeKey { get; private set; } = [];

    /// <summary>This room's world bounds — the only thing that maps quantized state to coordinates.</summary>
    public WorldQuantizer World { get; private set; }

    /// <summary>The reject the server sent before closing, if it sent one.</summary>
    public RejectCode? Rejected { get; private set; }

    /// <summary>Entities this client currently knows about, by slot.</summary>
    public int KnownCount
    {
        get
        {
            lock (_gate)
            {
                return _known.Count;
            }
        }
    }

    /// <summary>The netIds this client currently knows, for assertions.</summary>
    public uint[] KnownNetIds
    {
        get
        {
            lock (_gate)
            {
                uint[] copy = new uint[_known.Count];
                _known.Values.CopyTo(copy, 0);
                return copy;
            }
        }
    }

    /// <summary>True while the socket is usable.</summary>
    public bool IsOpen => _socket.State == WebSocketState.Open;

    /// <summary>
    /// Connects, sends <c>HelloCommand</c> as the first frame and waits for the <c>WelcomeEvent</c>.
    /// </summary>
    /// <param name="resumeKey">A key from a previous session to resume, or null for a fresh join.</param>
    /// <param name="announcedVersion">Version to announce; defaults to this build's current.</param>
    /// <exception cref="TimeoutException">No welcome arrived within the handshake timeout.</exception>
    /// <exception cref="InvalidOperationException">The server rejected the handshake.</exception>
    public async Task ConnectAsync(byte[]? resumeKey = null, ushort? announcedVersion = null, CancellationToken cancellationToken = default)
    {
        await _socket.ConnectAsync(_uri, cancellationToken).ConfigureAwait(false);
        _receiveLoop = Task.Run(ReceiveLoopAsync, CancellationToken.None);

        await SendControlAsync(MessageTypeIds.HelloCommand, new HelloCommand
        {
            ProtocolVersion = announcedVersion ?? ProtocolVersion.Current,
            Token = _token,
            RoomId = _roomId,
            DisplayName = _displayName,
            ResumeKey = resumeKey,
        }, cancellationToken).ConfigureAwait(false);

        Task completed = await Task.WhenAny(_welcome.Task, Task.Delay(HandshakeTimeout, cancellationToken)).ConfigureAwait(false);
        if (completed != _welcome.Task)
        {
            throw Rejected is { } reject
                ? new InvalidOperationException($"handshake rejected: {reject}")
                : new TimeoutException($"no WelcomeEvent for '{_displayName}' within {HandshakeTimeout.TotalSeconds:0} s");
        }

        WelcomeEvent welcome = await _welcome.Task.ConfigureAwait(false);
        ClientId = welcome.ClientId;
        HostClientId = welcome.HostClientId;
        TickHz = welcome.TickHz;
        NegotiatedVersion = welcome.ProtocolVersion;
        MaxVisibleEntities = welcome.MaxVisibleEntities;
        Resumed = welcome.Resumed;
        ResumeKey = welcome.ResumeKey;
        World = new WorldQuantizer(welcome.WorldOriginX, welcome.WorldOriginY, welcome.WorldSize);
    }

    /// <summary>Spawns an entity and waits for its <c>SpawnEntityResponse</c>.</summary>
    /// <returns>The assigned netId.</returns>
    /// <exception cref="InvalidOperationException">The spawn was refused; the message names the code.</exception>
    public async Task<uint> SpawnAsync(
        float x,
        float y,
        ushort kind = 1,
        float rot = 0f,
        OwnershipPolicy policy = OwnershipPolicy.Owned,
        CancellationToken cancellationToken = default)
    {
        World.TryQuantizePosition(x, y, out ushort qx, out ushort qy);
        WorldQuantizer.TryQuantizeRotation(rot, out byte qrot);

        TaskCompletionSource<SpawnEntityResponse> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _spawnWaiters.Enqueue(waiter);
        await SendControlAsync(MessageTypeIds.SpawnEntityRequest, new SpawnEntityRequest
        {
            RequestId = 1,
            Kind = kind,
            QX = qx,
            QY = qy,
            QRot = qrot,
            Flags = EntityFlags.WithPolicy(0, policy),
        }, cancellationToken).ConfigureAwait(false);

        Task completed = await Task.WhenAny(waiter.Task, Task.Delay(HandshakeTimeout, cancellationToken)).ConfigureAwait(false);
        if (completed != waiter.Task)
        {
            throw new TimeoutException($"no SpawnEntityResponse for '{_displayName}' within {HandshakeTimeout.TotalSeconds:0} s");
        }

        SpawnEntityResponse ack = await waiter.Task.ConfigureAwait(false);
        return ack.RejectCode == 0
            ? ack.NetId
            : throw new InvalidOperationException($"spawn refused: {(RejectCode)ack.RejectCode}");
    }

    /// <summary>
    /// Publishes one owned entity's position and rotation as an <c>EntityUpdatePacket</c>. The values are
    /// quantized here, because the quantized integers are what gets replicated — a client that publishes
    /// floats and renders floats is the divergence-pop bug the rule exists to prevent.
    /// </summary>
    public async Task SendUpdateAsync(uint netId, float x, float y, float rot, CancellationToken cancellationToken = default)
    {
        World.TryQuantizePosition(x, y, out ushort qx, out ushort qy);
        WorldQuantizer.TryQuantizeRotation(rot, out byte qrot);
        EntityWireState state = new() { QX = qx, QY = qy, QRot = qrot };

        byte[] frame = new byte[HotWire.EntityUpdatePacketHeaderSize + HotWire.MaxOwnerUpdateRecordSize];
        int cursor = HotWire.WriteEntityUpdatePacketHeader(frame, Metrics.ClientTick);
        cursor += HotWire.WriteOwnerUpdateRecord(
            frame.AsSpan(cursor), netId, DeltaMask.X | DeltaMask.Y | DeltaMask.Rot, state);
        HotWire.TryPatchEntityUpdatePacketCount(frame, 1);

        await SendRawAsync(frame.AsMemory(0, cursor), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a <c>PingCommand</c>; the matching pong feeds the round-trip samples.</summary>
    public async Task PingAsync(CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _pingSentTimestamp, Stopwatch.GetTimestamp());
        await SendControlAsync(MessageTypeIds.PingCommand, new PingCommand
        {
            ClientTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Asks the server to rebuild this client's known set from scratch.</summary>
    public Task RequestResyncAsync(CancellationToken cancellationToken = default)
        => SendControlAsync(MessageTypeIds.ResyncCommand, new ResyncCommand(), cancellationToken);

    /// <summary>Sets the hidden flag and send-rate divisor for this session.</summary>
    public Task SetPrefsAsync(bool hidden, byte sendRateDivisor, CancellationToken cancellationToken = default)
        => SendControlAsync(MessageTypeIds.SetClientPrefsCommand, new SetClientPrefsCommand
        {
            Hidden = hidden,
            SendRateDivisor = sendRateDivisor,
        }, cancellationToken);

    /// <summary>Emits a signal. AOI-scoped signals require a bound focus entity, i.e. an owned entity.</summary>
    public Task EmitSignalAsync(string name, SignalTarget target, byte[] payload, uint targetClientId = 0, CancellationToken cancellationToken = default)
        => SendControlAsync(MessageTypeIds.EmitSignalCommand, new EmitSignalCommand
        {
            Name = name,
            Target = (byte)target,
            TargetClientId = targetClientId,
            Payload = payload,
        }, cancellationToken);

    /// <summary>Sends a chat message.</summary>
    public Task SendChatAsync(string text, CancellationToken cancellationToken = default)
        => SendControlAsync(MessageTypeIds.SendChatCommand, new SendChatCommand { Text = text }, cancellationToken);

    /// <summary>
    /// Sends a raw frame with an arbitrary TypeId — for probing the "unknown TypeId is ignored and
    /// counted, never fatal" rule from the client side.
    /// </summary>
    public Task SendRawFrameAsync(byte typeId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        byte[] frame = new byte[payload.Length + 1];
        frame[0] = typeId;
        payload.Span.CopyTo(frame.AsSpan(1));
        return SendRawAsync(frame, cancellationToken);
    }

    /// <summary>Sends a text frame, which the protocol requires the server to refuse with close 4007.</summary>
    public Task SendTextFrameAsync(string text, CancellationToken cancellationToken = default)
        => _socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken);

    /// <summary>Waits until <paramref name="condition"/> holds or the timeout expires.</summary>
    /// <returns>True when the condition was met.</returns>
    public async Task<bool> WaitForAsync(Func<RoomClient, bool> condition, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        long deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * timeout.TotalSeconds);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (condition(this))
            {
                return true;
            }

            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }

        return condition(this);
    }

    /// <summary>Drops the socket without a close handshake — what a crashed or backgrounded client does.</summary>
    public void Abort() => _socket.Abort();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _closing.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using CancellationTokenSource closeTimeout = new(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeTimeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            _socket.Abort();
        }

        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The receive loop ending with the socket is the normal shutdown path.
            }
        }

        _socket.Dispose();
        _sendGate.Dispose();
        _closing.Dispose();
    }

    // ── Send plumbing ─────────────────────────────────────────────────────────

    private async Task SendControlAsync<T>(byte typeId, T message, CancellationToken cancellationToken)
    {
        byte[] payload = MemoryPackSerializer.Serialize(message);
        byte[] frame = new byte[payload.Length + 1];
        frame[0] = typeId;
        payload.CopyTo(frame, 1);
        await SendRawAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One send at a time: <see cref="ClientWebSocket"/> permits exactly one outstanding send, and a
    /// concurrent second one corrupts the stream rather than throwing somewhere useful.
    /// </summary>
    private async Task SendRawAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(frame, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
            Metrics.RecordSent(frame.Length);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    // ── Receive and decode ────────────────────────────────────────────────────

    private async Task ReceiveLoopAsync()
    {
        byte[] buffer = new byte[ReceiveBufferBytes];
        try
        {
            while (_socket.State == WebSocketState.Open && !_closing.IsCancellationRequested)
            {
                ValueWebSocketReceiveResult result =
                    await _socket.ReceiveAsync(buffer.AsMemory(), _closing.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Metrics.CloseStatus = _socket.CloseStatus is { } status ? (int)status : null;
                    return;
                }

                if (result.Count == 0)
                {
                    continue;
                }

                Metrics.RecordReceived(result.Count);
                Handle(buffer.AsSpan(0, result.Count));
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            Metrics.CloseStatus ??= _socket.CloseStatus is { } status ? (int)status : null;
        }
    }

    private void Handle(ReadOnlySpan<byte> frame)
    {
        switch (frame[0])
        {
            case MessageTypeIds.WelcomeEvent:
                _welcome.TrySetResult(MemoryPackSerializer.Deserialize<WelcomeEvent>(frame[1..])!);
                break;

            case MessageTypeIds.RejectedEvent:
                RejectedEvent rejected = MemoryPackSerializer.Deserialize<RejectedEvent>(frame[1..])!;
                Rejected = (RejectCode)rejected.Code;
                Metrics.RejectMessage = rejected.Message;
                break;

            case MessageTypeIds.SpawnEntityResponse:
                if (_spawnWaiters.TryDequeue(out TaskCompletionSource<SpawnEntityResponse>? waiter))
                {
                    waiter.TrySetResult(MemoryPackSerializer.Deserialize<SpawnEntityResponse>(frame[1..])!);
                }

                break;

            case MessageTypeIds.PongEvent:
                long sent = Volatile.Read(ref _pingSentTimestamp);
                if (sent != 0)
                {
                    Metrics.RecordRoundTrip(Stopwatch.GetElapsedTime(sent).TotalMilliseconds);
                }

                break;

            case MessageTypeIds.SnapshotPacket:
                ReadSnapshot(frame);
                break;

            case MessageTypeIds.DeltaPacket:
                ReadDelta(frame);
                break;

            case MessageTypeIds.SignalBatchPacket:
                if (HotWire.TryReadSignalBatchPacket(frame, out SignalBatchSections signals))
                {
                    TrackSeq(signals.Seq);
                    Metrics.SignalBatches++;
                    Metrics.SignalEntries += signals.Count;
                }
                else
                {
                    Metrics.MalformedFrames++;
                }

                break;

            case MessageTypeIds.PeerJoinedEvent:
                Metrics.PeerJoined++;
                break;

            case MessageTypeIds.PeerLeftEvent:
                Metrics.PeerLeft++;
                break;

            case MessageTypeIds.ChatMessageEvent:
                Metrics.ChatMessages++;
                break;

            case MessageTypeIds.RoomVarsChangedEvent:
                Metrics.RoomVarChanges++;
                break;

            case MessageTypeIds.HostChangedEvent:
                HostChangedEvent hostChanged = MemoryPackSerializer.Deserialize<HostChangedEvent>(frame[1..])!;
                HostClientId = hostChanged.HostClientId;
                Metrics.HostChanges++;
                break;

            case MessageTypeIds.RoomRosterEvent:
                RoomRosterEvent roomRoster = MemoryPackSerializer.Deserialize<RoomRosterEvent>(frame[1..])!;
                Metrics.RosterChunks++;
                if (FrameFlags.IsFinal(roomRoster.FrameFlags))
                {
                    Metrics.RostersCompleted++;
                }

                break;

            case MessageTypeIds.SignalEvent:
                Metrics.SignalEvents++;
                break;

            case MessageTypeIds.RoomInfoEvent:
                Metrics.RoomInfoEvents++;
                break;

            default:
                // Unknown TypeIds are ignored and counted here too — the rule runs in both directions.
                Metrics.UnknownTypeIds++;
                break;
        }
    }

    private void ReadSnapshot(ReadOnlySpan<byte> frame)
    {
        if (!HotWire.TryReadSnapshotPacket(frame, out ushort seq, out _, out byte flags, out int count, out ReadOnlySpan<byte> records))
        {
            Metrics.MalformedFrames++;
            return;
        }

        lock (_gate)
        {
            TrackSeq(seq);
            Metrics.SnapshotFrames++;
            if (FrameFlags.IsFinal(flags))
            {
                Metrics.SnapshotsCompleted++;
            }

            for (int i = 0; i < count; i++)
            {
                if (!HotWire.TryReadFullRecord(records[(i * HotWire.FullRecordSize)..], out uint netId, out _))
                {
                    Metrics.MalformedFrames++;
                    break;
                }

                _known[(ushort)NetId.Slot(netId)] = netId;
                Metrics.FullRecords++;
            }

            Metrics.PeakKnownCount = Math.Max(Metrics.PeakKnownCount, _known.Count);
        }
    }

    private void ReadDelta(ReadOnlySpan<byte> frame)
    {
        if (!HotWire.TryReadDeltaPacket(frame, out DeltaPacketSections sections))
        {
            Metrics.MalformedFrames++;
            return;
        }

        lock (_gate)
        {
            TrackSeq(sections.Seq);
            Metrics.DeltaFrames++;

            // Removals first, before enters and updates: that ordering is what makes u16 slot addressing
            // unambiguous, so a client that applied them in any other order would be the broken party.
            for (int i = 0; i < sections.RemovedCount; i++)
            {
                if (!sections.TryGetRemovedSlot(i, out ushort slot))
                {
                    Metrics.MalformedFrames++;
                    break;
                }

                if (!_known.Remove(slot))
                {
                    Metrics.RemovalsForUnknownSlots++;
                }

                Metrics.Removals++;
            }

            for (int i = 0; i < sections.EnterCount; i++)
            {
                if (!sections.TryGetEnterRecord(i, out uint netId, out _))
                {
                    Metrics.MalformedFrames++;
                    break;
                }

                ushort slot = (ushort)NetId.Slot(netId);
                if (!_known.TryAdd(slot, netId))
                {
                    // A slot entered twice without a removal in between: exactly the ghost the removal
                    // ordering rule prevents, so it is a protocol violation and not a client detail.
                    Metrics.DuplicateEnters++;
                    _known[slot] = netId;
                }

                Metrics.Enters++;
                Metrics.FullRecords++;
            }

            int cursor = 0;
            for (int i = 0; i < sections.UpdateCount; i++)
            {
                if (!sections.TryReadNextUpdate(ref cursor, out ushort slot, out byte mask, out EntityWireState _))
                {
                    Metrics.MalformedFrames++;
                    break;
                }

                Metrics.Updates++;
                Metrics.UpdateBytes += HotWire.UpdateRecordSize(mask);
                if (!_known.ContainsKey(slot))
                {
                    // "No delta without a prior full record" — the invariant that would break a real client.
                    Metrics.UpdatesForUnknownSlots++;
                }
            }

            Metrics.PeakKnownCount = Math.Max(Metrics.PeakKnownCount, _known.Count);
        }
    }

    /// <summary>
    /// A gap means desync: send <c>ResyncCommand</c> and expect a fresh snapshot. Doing this rather than
    /// merely counting is what makes the load generator a conforming client instead of a byte sink.
    /// </summary>
    private void TrackSeq(ushort seq)
    {
        if (_lastSeq >= 0 && seq != (ushort)(_lastSeq + 1))
        {
            Metrics.SeqGaps++;
            _ = Task.Run(async () =>
            {
                try
                {
                    await RequestResyncAsync(_closing.Token).ConfigureAwait(false);
                    Metrics.ResyncsRequested++;
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
                {
                    // The socket is going away; a resync request is moot.
                }
            });
        }

        _lastSeq = seq;
    }
}
