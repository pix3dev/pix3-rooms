using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Pix3.Rooms.Protocol;
using Pix3.Rooms.Server.Rooms;

namespace Pix3.Rooms.Server.Net;

/// <summary>
/// One live WebSocket session: the socket, its two bounded outbound lanes, its single send loop and its
/// receive loop. This is the only class in the process that touches a <see cref="WebSocket"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading.</b> Exactly one task receives and one task sends. <see cref="TryEnqueue"/> and
/// <see cref="RequestClose"/> are called from the room thread and are non-blocking; the socket is never
/// touched from there. The WebSocket contract allows one concurrent send and one concurrent receive, so
/// the close handshake is performed by the send loop (it is a send) and never by the room.
/// </para>
/// <para>
/// <b>Two lanes, two failure policies.</b> Both channels are <see cref="BoundedChannelFullMode.Wait"/> and
/// only ever written with <c>TryWrite</c>, so a full queue surfaces as <c>TryEnqueue == false</c> with the
/// buffer still owned by the caller — never as a silent discard that leaks a rented array. A full
/// <see cref="FrameLane.Control"/> queue additionally <i>closes the connection</i>, because a dropped
/// control frame has no repair mechanism; a full <see cref="FrameLane.Hot"/> queue merely reports failure,
/// and the caller rolls back that frame's known-set changes and marks the client for resync.
/// </para>
/// <para>
/// <b>Pre-auth gate.</b> Before the handshake authenticates, a socket may consume: no client id, one
/// receive buffer of <see cref="NetOptions.MaxPreAuthFrameBytes"/>, exactly <b>one</b> frame of at most
/// that many bytes, one pre-auth slot for its address, and
/// <see cref="NetOptions.HandshakeTimeoutSeconds"/> of wall clock. A second frame or an oversized one ends
/// the session.
/// </para>
/// </remarks>
public sealed class ClientConnection : IClientConnection
{
    /// <summary>Longest close reason forwarded to the client in a <c>RejectedEvent</c>.</summary>
    public const int MaxCloseReasonLength = 256;

    /// <summary>A WebSocket close reason may not exceed 123 UTF-8 bytes, so it is truncated to this.</summary>
    private const int MaxWebSocketReasonBytes = 120;

    /// <summary>Grace period for flushing queued frames and completing the close handshake.</summary>
    private const int CloseLingerMilliseconds = 5_000;

    /// <summary>Cap on how long the outbound close frame may take before we give up on it.</summary>
    private const int CloseHandshakeTimeoutMilliseconds = 2_000;

    /// <summary>
    /// Transport-local session ids. Distinct from client ids on purpose: a session id exists from the
    /// moment a socket is accepted, so the connection registry can key on it, while a client id is only
    /// minted once the socket authenticates.
    /// </summary>
    private static long _nextSessionId;

    private readonly WebSocket _socket;
    private readonly NetOptions _netOptions;
    private readonly QuotaOptions _quotas;
    private readonly NetMetrics _metrics;
    private readonly HandshakeProcessor _handshakeProcessor;
    private readonly PreAuthLease _preAuthLease;
    private readonly ILogger<ClientConnection> _logger;

    /// <summary>Handshake, chat, room vars, spawn responses, per-recipient signals, rejections.</summary>
    private readonly Channel<OutboundFrame> _control;

    /// <summary>Snapshots, deltas, signal batches.</summary>
    private readonly Channel<OutboundFrame> _hot;

    /// <summary>
    /// The single wake signal for the send loop, released once per successful enqueue on either lane.
    /// </summary>
    /// <remarks>
    /// One signal, not a <c>Task.WhenAny</c> over both readers: at 600 clients x 20 Hz that pattern
    /// allocates two <c>ValueTask</c> wrappers, an array and a combinator task <i>per iteration</i> — about
    /// 12 000 allocations a second of pure GC pressure. Here the only allocation is one wait node per wake
    /// that actually has to block, and a wake carries every frame queued since the last one.
    /// </remarks>
    private readonly SemaphoreSlim _sendSignal = new(0);

    private readonly CancellationTokenSource _cts = new();
    private readonly long _createdTimestamp;
    private readonly long _handshakeTimeoutTicks;
    private readonly long _idleTimeoutTicks;
    private readonly int _preAuthReceiveCapacity;
    private readonly int _joinedReceiveCapacity;

    /// <summary>
    /// The receive buffer. Swapped from the small pre-auth buffer to the full-size one exactly once, by the
    /// receive loop, at a point where no span over the old array is alive.
    /// </summary>
    private byte[] _receiveBuffer;
    private int _receiveCapacity;

    private volatile string _displayName = "";
    private volatile bool _isOpen = true;
    private uint _clientId;
    private int _closeState;
    private int _authenticated;
    private int _preAuthFrames;
    private int _voluntaryLeave;
    private RejectCode _closeCode = RejectCode.None;
    private string _closeReason = "";
    private long _lastInboundTimestamp;
    private long _controlDropped;
    private long _hotDropped;
    private IRoom? _room;
    private InboundDispatcher? _dispatcher;

    /// <summary>Wraps an accepted socket. The caller owns disposing <paramref name="socket"/>.</summary>
    /// <param name="socket">The accepted WebSocket.</param>
    /// <param name="remoteIp">Canonical client address from <see cref="RemoteIpResolver"/>.</param>
    /// <param name="requestedRoomId">Room id from the query string; may be empty.</param>
    /// <param name="netOptions">Transport options.</param>
    /// <param name="quotas">Abuse limits.</param>
    /// <param name="metrics">Counter surface.</param>
    /// <param name="handshakeProcessor">Handles the first frame.</param>
    /// <param name="preAuthLease">
    /// The address's pre-auth slot, released the moment this connection authenticates. Idempotent, so the
    /// endpoint may also release it when the session ends.
    /// </param>
    /// <param name="logger">Logger for this connection.</param>
    public ClientConnection(
        WebSocket socket,
        string remoteIp,
        string requestedRoomId,
        NetOptions netOptions,
        QuotaOptions quotas,
        NetMetrics metrics,
        HandshakeProcessor handshakeProcessor,
        PreAuthLease preAuthLease,
        ILogger<ClientConnection> logger)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(remoteIp);
        ArgumentNullException.ThrowIfNull(requestedRoomId);
        ArgumentNullException.ThrowIfNull(netOptions);
        ArgumentNullException.ThrowIfNull(quotas);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(handshakeProcessor);
        ArgumentNullException.ThrowIfNull(preAuthLease);
        ArgumentNullException.ThrowIfNull(logger);

        _socket = socket;
        RemoteIp = remoteIp;
        RequestedRoomId = requestedRoomId;
        _netOptions = netOptions;
        _quotas = quotas;
        _metrics = metrics;
        _handshakeProcessor = handshakeProcessor;
        _preAuthLease = preAuthLease;
        _logger = logger;

        SessionId = Interlocked.Increment(ref _nextSessionId);

        // No client id yet, and that is the contract: an unauthenticated socket consumes no id from the
        // monotonic allocator, so an unauthenticated flood cannot advance it. See ClientIdAllocator.
        _clientId = 0u;

        _control = CreateLane(netOptions.OutboundControlQueueCapacity);
        _hot = CreateLane(netOptions.OutboundHotQueueCapacity);

        // A socket that has proved nothing gets the small buffer; it is promoted only after the handshake.
        _preAuthReceiveCapacity = Math.Min(netOptions.MaxPreAuthFrameBytes, quotas.MaxPayloadBytes);
        _joinedReceiveCapacity = quotas.MaxPayloadBytes;
        _receiveCapacity = _preAuthReceiveCapacity;
        _receiveBuffer = FramePool.Rent(_receiveCapacity);

        _createdTimestamp = Stopwatch.GetTimestamp();
        _lastInboundTimestamp = _createdTimestamp;
        _handshakeTimeoutTicks = netOptions.HandshakeTimeoutSeconds > 0
            ? netOptions.HandshakeTimeoutSeconds * Stopwatch.Frequency
            : 0;
        _idleTimeoutTicks = quotas.IdleTimeoutSeconds > 0
            ? quotas.IdleTimeoutSeconds * Stopwatch.Frequency
            : 0;
    }

    /// <summary>
    /// Transport-local, monotonic per process, allocated at accept time. The connection registry keys on
    /// this rather than on <see cref="ClientId"/>, which is zero until the handshake authenticates.
    /// </summary>
    public long SessionId { get; }

    /// <inheritdoc />
    public uint ClientId => Volatile.Read(ref _clientId);

    /// <inheritdoc />
    public string RemoteIp { get; }

    /// <inheritdoc />
    public string DisplayName => _displayName;

    /// <inheritdoc />
    public bool IsOpen => _isOpen;

    /// <summary>Room id taken from the upgrade request's query string; empty when it was not supplied.</summary>
    /// <remarks>
    /// Only the <i>room id</i> may come from the query string. The token never does: query strings land in
    /// access logs, proxy logs and <c>Referer</c> headers, so a token there is a credential written to disk
    /// on every hop. It travels in the first frame instead.
    /// </remarks>
    public string RequestedRoomId { get; }

    /// <summary>True once <c>HelloCommand</c> was accepted and the client is a room member.</summary>
    public bool IsAuthenticated => Volatile.Read(ref _authenticated) != 0;

    /// <summary>The room this connection joined, or null before the handshake completes.</summary>
    public IRoom? Room => _room;

    /// <summary>Control frames refused because the control lane was full. Every one of them closed the session.</summary>
    public long DroppedControlFrames => Volatile.Read(ref _controlDropped);

    /// <summary>Hot frames refused because the hot lane was full. Each one costs a rollback and a resync.</summary>
    public long DroppedHotFrames => Volatile.Read(ref _hotDropped);

    /// <summary>Why the session is ending; <see cref="RejectCode.None"/> while it is healthy.</summary>
    public RejectCode CloseCode => _closeCode;

    /// <inheritdoc />
    public bool TryEnqueue(in OutboundFrame frame, FrameLane lane)
    {
        if (!_isOpen || frame.IsEmpty)
        {
            // Ownership stays with the caller, which returns the buffer. Nothing was queued.
            return false;
        }

        Channel<OutboundFrame> channel = lane == FrameLane.Hot ? _hot : _control;
        if (channel.Writer.TryWrite(frame))
        {
            // OWNERSHIP TRANSFER: the frame is now the send loop's, which returns its buffer to the pool
            // after writing it. The caller must not touch it again. The release comes after the write so a
            // wake can never observe an empty queue for a frame that is about to appear.
            Release();
            return true;
        }

        _metrics.Increment(NetCounter.OutboundDroppedQueueFull);

        if (lane == FrameLane.Hot)
        {
            Interlocked.Increment(ref _hotDropped);
            _metrics.Increment(NetCounter.OutboundHotQueueOverflows);

            // Recoverable by design: the caller still owns the buffer, rolls back the known-set changes this
            // frame carried and marks the client for resync. Seq is left untouched, so the client never sees
            // a gap for a frame that never existed.
            return false;
        }

        Interlocked.Increment(ref _controlDropped);
        _metrics.Increment(NetCounter.OutboundControlQueueOverflows);

        // A control frame has no later frame that repairs it: dropping a RejectedEvent, a spawn response or
        // a room-var change silently desynchronises the client's view of the session for good. A client that
        // cannot drain 64 control frames is unrecoverably behind, so the session ends. RateLimited (close
        // 4004) is the closest defined code — the protocol does not name one for send-queue overflow — and it
        // tells the client the honest thing: back off and reconnect.
        RequestClose(RejectCode.RateLimited, "this connection is too far behind to receive control messages");

        // Still false: the caller owns the buffer and returns it exactly once, here as everywhere.
        return false;
    }

    /// <inheritdoc />
    public void RequestClose(RejectCode code, string reason)
    {
        if (Interlocked.CompareExchange(ref _closeState, 1, 0) != 0)
        {
            return;
        }

        _closeCode = code;
        _closeReason = Truncate(reason ?? "", MaxCloseReasonLength);

        // Refuse further room traffic before queueing the goodbye, so the reject is the last frame out.
        _isOpen = false;

        if (code != RejectCode.None)
        {
            TryQueueRejectedEvent(code, _closeReason);
        }

        // Completing the writers lets the send loop drain what is already queued and then run the close
        // handshake; the linger cancel is the backstop for a peer that never answers. The order matters: the
        // reject is queued first, then the writers are completed, then the loop is woken — so the loop's exit
        // test (both readers' Completion) can never be true while the reject is still unsent.
        _control.Writer.TryComplete();
        _hot.Writer.TryComplete();
        Release();

        try
        {
            _cts.CancelAfter(CloseLingerMilliseconds);
        }
        catch (ObjectDisposedException)
        {
            // Teardown already finished; nothing left to cancel.
        }

        _logger.LogDebug(
            "Closing session {SessionId} (client {ClientId}) from {RemoteIp}: {RejectCode} ({CloseCode}) {Reason}",
            SessionId,
            ClientId,
            RemoteIp,
            code,
            code.ToCloseCode(),
            _closeReason);
    }

    /// <summary>
    /// Runs the session until the socket ends or a close is requested. Never throws for network reasons.
    /// </summary>
    /// <param name="cancellationToken">Request abort token; cancelling it closes the session.</param>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenRegistration abortRegistration = cancellationToken.UnsafeRegister(
            static state => ((ClientConnection)state!).RequestClose(RejectCode.None, "the host aborted the request"),
            this);

        Task sendLoop = SendLoopAsync();
        try
        {
            await ReceiveLoopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Receive loop for session {SessionId} failed", SessionId);
            RequestClose(RejectCode.InternalError, "the server failed while reading from this connection");
        }
        finally
        {
            // No-op when a close is already in flight; guarantees the send loop is told to finish.
            RequestClose(RejectCode.None, "connection closed");

            try
            {
                await sendLoop.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Send loop for session {SessionId} ended with an error", SessionId);
            }

            LeaveRoom();

            // The pre-auth slot is normally released at authentication; releasing again here is a no-op and
            // covers the socket that never got that far.
            _preAuthLease.Release();

            FramePool.Return(_receiveBuffer);
            _sendSignal.Dispose();
            _cts.Dispose();
            _metrics.Increment(NetCounter.ConnectionsClosed);
        }
    }

    /// <summary>
    /// Called by the supervisor's sweep. Enforces the handshake deadline before authentication and the idle
    /// timeout after it, so neither needs a per-connection timer.
    /// </summary>
    /// <param name="nowTimestamp">A <see cref="Stopwatch.GetTimestamp"/> reading shared by the sweep.</param>
    internal void CheckDeadlines(long nowTimestamp)
    {
        if (Volatile.Read(ref _closeState) != 0)
        {
            return;
        }

        if (Volatile.Read(ref _authenticated) == 0)
        {
            if (_handshakeTimeoutTicks > 0 && nowTimestamp - _createdTimestamp >= _handshakeTimeoutTicks)
            {
                _metrics.Increment(NetCounter.HandshakeTimeouts);
                RequestClose(RejectCode.IdleTimeout, "no HelloCommand arrived in time");
            }

            return;
        }

        if (_idleTimeoutTicks > 0 && nowTimestamp - Volatile.Read(ref _lastInboundTimestamp) >= _idleTimeoutTicks)
        {
            _metrics.Increment(NetCounter.IdleTimeouts);
            RequestClose(RejectCode.IdleTimeout, $"no traffic for {_quotas.IdleTimeoutSeconds}s");
        }
    }

    /// <summary>
    /// Publishes the display name the handshake settled on. Called before <c>IRoom.TryJoin</c> so the
    /// room's <c>PeerJoinedEvent</c> fan-out sees the final name.
    /// </summary>
    internal void ApplyIdentity(string displayName)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        _displayName = displayName;
    }

    /// <summary>
    /// Takes this session's client id from the process allocator, once the token has been validated and a
    /// room resolved. Called by the handshake immediately before <c>IRoom.TryJoin</c>, because the room keys
    /// its membership on <see cref="ClientId"/>.
    /// </summary>
    /// <returns>The freshly allocated id.</returns>
    internal uint AllocateClientId()
    {
        uint id = ClientIdAllocator.Next();
        Volatile.Write(ref _clientId, id);
        return id;
    }

    /// <summary>
    /// Adopts the id the room's <c>JoinGrant</c> reports, before the member is published.
    /// </summary>
    /// <remarks>
    /// For a fresh join this is the id <see cref="AllocateClientId"/> just minted, and adopting it is a
    /// no-op — but the grant is still the authority, so the transport always takes its value. For a
    /// <b>resume</b> it is the dropped session's <i>original</i> id, which is the whole point: the client
    /// keeps its identity and its entities across a blip, and its peers were never told it left. The client
    /// never gets to claim an id; it presents a 16-byte key and the room decides.
    /// </remarks>
    internal void AdoptClientId(uint clientId) => Volatile.Write(ref _clientId, clientId);

    /// <summary>Records that the client asked to leave, so teardown reports <c>LeftVoluntarily</c>.</summary>
    internal void MarkVoluntaryLeave() => Volatile.Write(ref _voluntaryLeave, 1);

    /// <summary>Creates one send lane. Both lanes are configured identically; only their depth and policy differ.</summary>
    private static Channel<OutboundFrame> CreateLane(int capacity)
        => Channel.CreateBounded<OutboundFrame>(new BoundedChannelOptions(capacity)
        {
            // Wait, never DropWrite/DropOldest. DropWrite reports success and silently discards the frame,
            // leaking its pooled buffer; DropOldest would ship a newer delta before an older one and corrupt
            // the client's known set. We only ever TryWrite, so a full queue surfaces as TryEnqueue == false
            // with ownership still in the caller's hands — the one policy that is both lossless-by-report
            // and leak-free.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

    /// <summary>Wakes the send loop. Safe after disposal, which happens only during teardown.</summary>
    private void Release()
    {
        try
        {
            _sendSignal.Release();
        }
        catch (ObjectDisposedException)
        {
            // Teardown already returned every queued buffer; there is nothing left to wake for.
        }
        catch (SemaphoreFullException)
        {
            // Cannot happen with the default maximum, but a lost wake must never take down a socket thread:
            // the loop's own drain-everything-per-wake behaviour makes a missed permit harmless.
        }
    }

    private async Task ReceiveLoopAsync()
    {
        CancellationToken token = _cts.Token;
        int accumulated = 0;

        while (!token.IsCancellationRequested && _socket.State == WebSocketState.Open)
        {
            ValueWebSocketReceiveResult result;
            try
            {
                result = await _socket
                    .ReceiveAsync(_receiveBuffer.AsMemory(accumulated, _receiveCapacity - accumulated), token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (WebSocketException ex)
            {
                // Abrupt disconnects (browser closed, network drop) land here; a lifecycle event.
                _logger.LogDebug(ex, "Session {SessionId} disconnected while receiving", SessionId);
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                RequestClose(RejectCode.None, "the client closed the connection");
                return;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                _metrics.Increment(NetCounter.InboundTextFrames);
                RequestClose(RejectCode.BadRequest, "this protocol is binary only; text frames are rejected");
                return;
            }

            accumulated += result.Count;

            if (!result.EndOfMessage)
            {
                if (accumulated >= _receiveCapacity)
                {
                    if (Volatile.Read(ref _authenticated) == 0)
                    {
                        // The pre-auth buffer IS the pre-auth frame cap, so filling it without an end-of-message
                        // means the client is sending more than 2 KiB before it has proved anything.
                        _metrics.Increment(NetCounter.PreAuthFrameOversized);
                        _metrics.Increment(NetCounter.InboundOversized);
                        RequestClose(
                            RejectCode.PayloadTooLarge,
                            $"the first frame must not exceed {_preAuthReceiveCapacity} bytes");
                        return;
                    }

                    _metrics.Increment(NetCounter.InboundOversized);
                    _metrics.Increment(NetCounter.QuotaPayloadBreaches);
                    RequestClose(RejectCode.PayloadTooLarge, $"a message exceeded {_receiveCapacity} bytes");
                    return;
                }

                continue;
            }

            if (accumulated == 0 && Volatile.Read(ref _authenticated) != 0)
            {
                // Empty binary message from a joined client: nothing to dispatch, and not worth killing a
                // client over. Before authentication it is NOT ignored — see HandleMessage — because an
                // unauthenticated socket gets exactly one frame and an empty one is not a HelloCommand.
                continue;
            }

            Volatile.Write(ref _lastInboundTimestamp, Stopwatch.GetTimestamp());
            bool keepReading = HandleMessage(_receiveBuffer.AsSpan(0, accumulated));
            accumulated = 0;
            if (!keepReading)
            {
                return;
            }

            // Promoted here, not inside the handshake: at this point no span over the old array is alive,
            // which is what makes returning it to the pool safe.
            if (_receiveCapacity < _joinedReceiveCapacity && Volatile.Read(ref _authenticated) != 0)
            {
                PromoteReceiveBuffer();
            }
        }
    }

    /// <summary>
    /// Swaps the small pre-auth receive buffer for the full-size one, now that the client has authenticated
    /// and is allowed to send <see cref="QuotaOptions.MaxPayloadBytes"/> frames.
    /// </summary>
    private void PromoteReceiveBuffer()
    {
        byte[] promoted = FramePool.Rent(_joinedReceiveCapacity);
        byte[] previous = _receiveBuffer;
        _receiveBuffer = promoted;
        _receiveCapacity = _joinedReceiveCapacity;

        // Returned exactly once, at the single point that owns it. The receive loop is the only reader of
        // this field, so there is no window in which another thread could hold the old array.
        FramePool.Return(previous);
    }

    /// <summary>Routes one complete frame. False means the session is ending.</summary>
    private bool HandleMessage(ReadOnlySpan<byte> frame)
    {
        if (Volatile.Read(ref _authenticated) == 0)
        {
            // Exactly one pre-auth frame. A second one means the client is talking before it has been
            // admitted — either a broken client or something probing the handshake — and there is no
            // legitimate reason for it, because the handshake either authenticates or closes.
            if (Interlocked.Increment(ref _preAuthFrames) > 1)
            {
                _metrics.Increment(NetCounter.PreAuthExtraFrames);
                RequestClose(RejectCode.BadRequest, "only one frame is accepted before authentication");
                return false;
            }

            return TryCompleteHandshake(frame);
        }

        InboundDispatcher? dispatcher = _dispatcher;
        if (dispatcher is null)
        {
            _logger.LogError("Session {SessionId} is joined but has no dispatcher", SessionId);
            RequestClose(RejectCode.InternalError, "the session is in an inconsistent state");
            return false;
        }

        return dispatcher.Dispatch(frame);
    }

    private bool TryCompleteHandshake(ReadOnlySpan<byte> frame)
    {
        IRoom? room;
        RejectCode reject;
        string reason;
        try
        {
            if (!_handshakeProcessor.TryProcess(this, frame, out room, out reject, out reason))
            {
                RequestClose(reject, reason);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Handshake for session {SessionId} from {RemoteIp} threw",
                SessionId,
                RemoteIp);
            RequestClose(RejectCode.InternalError, "the handshake failed on the server");
            return false;
        }

        _room = room;
        _dispatcher = new InboundDispatcher(this, room, _netOptions, _quotas, _metrics, _logger);
        Volatile.Write(ref _lastInboundTimestamp, Stopwatch.GetTimestamp());
        Volatile.Write(ref _authenticated, 1);

        // The address's pre-auth budget is freed the instant this socket stops being pre-auth, so a
        // legitimate client reconnecting in a loop is throttled by the connect bucket, not by its own
        // successful handshakes.
        _preAuthLease.Release();

        _logger.LogInformation(
            "Client {ClientId} ({DisplayName}) from {RemoteIp} joined room {RoomId} on session {SessionId}",
            ClientId,
            DisplayName,
            RemoteIp,
            room.Config.RoomId,
            SessionId);
        return true;
    }

    /// <summary>
    /// The single send loop. One wake drains <b>both</b> lanes, control first, so a hot backlog can never
    /// delay a rejection; and the drain re-checks the control lane after every single hot frame, so a client
    /// with 8 queued deltas still gets its <c>RejectedEvent</c> before the socket closes.
    /// </summary>
    private async Task SendLoopAsync()
    {
        CancellationToken token = _cts.Token;
        try
        {
            while (true)
            {
                await _sendSignal.WaitAsync(token).ConfigureAwait(false);

                // Collapse any surplus permits before reading the lanes, never after: a frame queued after
                // this point releases a permit we have not consumed, so the next wait returns immediately and
                // no frame is ever stranded.
                while (_sendSignal.Wait(0))
                {
                }

                bool faulted = false;
                while (true)
                {
                    // The whole lane priority is this one condition: the control lane is tried first on every
                    // single iteration, so it is drained completely before the first hot frame AND re-checked
                    // after each one. A client with a full hot lane still gets its RejectedEvent promptly.
                    if (!_control.Reader.TryRead(out OutboundFrame frame)
                        && !_hot.Reader.TryRead(out frame))
                    {
                        break;
                    }

                    bool sent = false;
                    try
                    {
                        // A closed socket leaves `sent` false, which ends the loop; the finally block below
                        // drains whatever is still queued so every pooled buffer goes home.
                        if (_socket.State == WebSocketState.Open)
                        {
                            await _socket
                                .SendAsync(frame.Memory, WebSocketMessageType.Binary, true, token)
                                .ConfigureAwait(false);
                            _metrics.Increment(NetCounter.OutboundFramesSent);
                            _metrics.Add(NetCounter.OutboundBytesSent, frame.Length);
                            sent = true;
                        }
                    }
                    catch (WebSocketException ex)
                    {
                        _metrics.Increment(NetCounter.SendFailures);
                        _logger.LogDebug(ex, "Session {SessionId} went away mid-send", SessionId);
                    }
                    catch (ObjectDisposedException)
                    {
                        // The socket was disposed under us during teardown.
                    }
                    finally
                    {
                        // OWNERSHIP: the send loop took this frame off a queue, so it returns the buffer —
                        // exactly once, on every path including a failed write.
                        FramePool.Return(frame.Buffer);
                    }

                    if (!sent)
                    {
                        faulted = true;
                        break;
                    }
                }

                if (faulted)
                {
                    _isOpen = false;
                    return;
                }

                // Both lanes are drained. Exiting on Completion rather than on the close flag is what
                // guarantees a queued RejectedEvent is written before the socket goes: Completion only
                // finishes once the writer is completed AND everything written has been consumed.
                if (_control.Reader.Completion.IsCompleted && _hot.Reader.Completion.IsCompleted)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Linger expired or the host aborted the request.
        }
        catch (ChannelClosedException)
        {
            // A writer completed between the wake and a read.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Send loop for session {SessionId} stopped unexpectedly", SessionId);
        }
        finally
        {
            _isOpen = false;

            // Completing first makes every later TryEnqueue fail, so a frame written by the room during
            // teardown is returned by its owner instead of being stranded in a queue.
            _control.Writer.TryComplete();
            _hot.Writer.TryComplete();

            while (_control.Reader.TryRead(out OutboundFrame leftover))
            {
                FramePool.Return(leftover.Buffer);
            }

            while (_hot.Reader.TryRead(out OutboundFrame leftover))
            {
                FramePool.Return(leftover.Buffer);
            }

            await CloseSocketAsync().ConfigureAwait(false);
        }
    }

    private async Task CloseSocketAsync()
    {
        if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        WebSocketCloseStatus status = _closeCode.ToWebSocketCloseStatus();
        using var timeout = new CancellationTokenSource(CloseHandshakeTimeoutMilliseconds);
        try
        {
            await _socket
                .CloseOutputAsync(status, TruncateToWebSocketReason(_closeReason), timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Close handshake with session {SessionId} did not complete", SessionId);
        }
    }

    /// <summary>
    /// Queues the goodbye frame, so a client can show a real message instead of "connection lost".
    /// </summary>
    /// <remarks>
    /// Written straight to the control channel: <see cref="TryEnqueue"/> already refuses frames on a closing
    /// connection, and routing through it would recurse into <see cref="RequestClose"/> on a full lane. When
    /// the lane is full the reject cannot be delivered — which is exactly the "unrecoverably behind" case
    /// that produced the close in the first place — so the buffer is returned and the loss is logged.
    /// </remarks>
    private void TryQueueRejectedEvent(RejectCode code, string reason)
    {
        OutboundFrame frame;
        try
        {
            frame = FramePool.EncodeControl(
                MessageTypeIds.RejectedEvent,
                new RejectedEvent { Code = (ushort)code, Message = reason });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not encode the RejectedEvent for session {SessionId}", SessionId);
            return;
        }

        if (_control.Writer.TryWrite(frame))
        {
            return;
        }

        FramePool.Return(frame.Buffer);
        _logger.LogDebug(
            "Session {SessionId} could not be told why it is closing ({RejectCode}): the control lane is full",
            SessionId,
            code);
    }

    private void LeaveRoom()
    {
        IRoom? room = _room;
        if (room is null)
        {
            return;
        }

        _room = null;
        LeaveReason reason = ResolveLeaveReason();
        try
        {
            room.Leave(ClientId, reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Room {RoomId} failed to remove client {ClientId} ({LeaveReason})",
                room.Config.RoomId,
                ClientId,
                reason);
        }
    }

    private LeaveReason ResolveLeaveReason()
    {
        if (Volatile.Read(ref _voluntaryLeave) != 0)
        {
            return LeaveReason.LeftVoluntarily;
        }

        return _closeCode switch
        {
            RejectCode.None => LeaveReason.Disconnected,
            RejectCode.IdleTimeout => LeaveReason.Timeout,
            RejectCode.SessionReplaced => LeaveReason.Kicked,
            RejectCode.RoomClosing => LeaveReason.RoomClosed,
            RejectCode.ServerShuttingDown => LeaveReason.RoomClosed,
            RejectCode.InternalError => LeaveReason.Error,
            RejectCode.BadRequest => LeaveReason.Error,
            RejectCode.RateLimited => LeaveReason.Error,
            RejectCode.PayloadTooLarge => LeaveReason.Error,
            RejectCode.QuotaExceeded => LeaveReason.Error,
            _ => LeaveReason.Disconnected,
        };
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value.Substring(0, maxLength);

    /// <summary>
    /// A WebSocket close reason is capped at 123 UTF-8 bytes; exceeding it makes
    /// <c>CloseOutputAsync</c> throw, which would turn a clean close into an abort.
    /// </summary>
    private static string TruncateToWebSocketReason(string reason)
    {
        if (reason.Length == 0)
        {
            return reason;
        }

        if (Encoding.UTF8.GetByteCount(reason) <= MaxWebSocketReasonBytes)
        {
            return reason;
        }

        int length = reason.Length;
        while (length > 0 && Encoding.UTF8.GetByteCount(reason.AsSpan(0, length)) > MaxWebSocketReasonBytes)
        {
            length--;
        }

        // Never split a surrogate pair, or the reason becomes invalid UTF-8.
        if (length > 0 && char.IsHighSurrogate(reason[length - 1]))
        {
            length--;
        }

        return reason.Substring(0, length);
    }
}
