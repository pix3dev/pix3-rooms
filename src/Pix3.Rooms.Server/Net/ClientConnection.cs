using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Pix3.Rooms.Protocol;
using Pix3.Rooms.Server.Rooms;

namespace Pix3.Rooms.Server.Net;

/// <summary>
/// One live WebSocket session: the socket, its bounded outbound queue, its single send loop and its
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
/// <b>Drop the newest, not the oldest.</b> A slow client's queue fills with hot frames. Dropping the
/// oldest would ship a newer delta before an older one and corrupt the client's known-set, so the queue
/// refuses the newest frame instead and the owner returns its buffer to the pool.
/// </para>
/// </remarks>
public sealed class ClientConnection : IClientConnection
{
    /// <summary>Longest close reason forwarded to the client in a <c>RejectEvent</c>.</summary>
    public const int MaxCloseReasonLength = 256;

    /// <summary>A WebSocket close reason may not exceed 123 UTF-8 bytes, so it is truncated to this.</summary>
    private const int MaxWebSocketReasonBytes = 120;

    /// <summary>Grace period for flushing queued frames and completing the close handshake.</summary>
    private const int CloseLingerMilliseconds = 5_000;

    /// <summary>Cap on how long the outbound close frame may take before we give up on it.</summary>
    private const int CloseHandshakeTimeoutMilliseconds = 2_000;

    private static uint _nextClientId;

    private readonly WebSocket _socket;
    private readonly NetOptions _netOptions;
    private readonly QuotaOptions _quotas;
    private readonly NetMetrics _metrics;
    private readonly HandshakeProcessor _handshakeProcessor;
    private readonly ILogger<ClientConnection> _logger;
    private readonly Channel<OutboundFrame> _outbound;
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _receiveBuffer;
    private readonly int _receiveCapacity;
    private readonly long _createdTimestamp;
    private readonly long _handshakeTimeoutTicks;
    private readonly long _idleTimeoutTicks;

    private volatile string _displayName = "";
    private volatile bool _isOpen = true;
    private int _closeState;
    private int _handshakeComplete;
    private int _voluntaryLeave;
    private RejectCode _closeCode = RejectCode.None;
    private string _closeReason = "";
    private long _lastInboundTimestamp;
    private long _outboundDropped;
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
    /// <param name="logger">Logger for this connection.</param>
    public ClientConnection(
        WebSocket socket,
        string remoteIp,
        string requestedRoomId,
        NetOptions netOptions,
        QuotaOptions quotas,
        NetMetrics metrics,
        HandshakeProcessor handshakeProcessor,
        ILogger<ClientConnection> logger)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(remoteIp);
        ArgumentNullException.ThrowIfNull(requestedRoomId);
        ArgumentNullException.ThrowIfNull(netOptions);
        ArgumentNullException.ThrowIfNull(quotas);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(handshakeProcessor);
        ArgumentNullException.ThrowIfNull(logger);

        _socket = socket;
        RemoteIp = remoteIp;
        RequestedRoomId = requestedRoomId;
        _netOptions = netOptions;
        _quotas = quotas;
        _metrics = metrics;
        _handshakeProcessor = handshakeProcessor;
        _logger = logger;

        // Ids start at 1: zero means "no client" everywhere else in the server.
        ClientId = unchecked(Interlocked.Increment(ref _nextClientId));

        _outbound = Channel.CreateBounded<OutboundFrame>(new BoundedChannelOptions(netOptions.OutboundQueueCapacity)
        {
            // Wait, never DropWrite: DropWrite reports success and silently discards the frame, which
            // would leak its pooled buffer. We only ever TryWrite, so a full queue surfaces as
            // TryEnqueue == false and the owner returns the buffer — the same "drop the newest" policy,
            // with correct ownership.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        _receiveCapacity = quotas.MaxPayloadBytes;
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

    /// <inheritdoc />
    public uint ClientId { get; }

    /// <inheritdoc />
    public string RemoteIp { get; }

    /// <inheritdoc />
    public string DisplayName => _displayName;

    /// <inheritdoc />
    public bool IsOpen => _isOpen;

    /// <summary>Room id taken from the upgrade request's query string; empty when it was not supplied.</summary>
    public string RequestedRoomId { get; }

    /// <summary>True once <c>HelloRequest</c> was accepted and the client is a room member.</summary>
    public bool IsJoined => Volatile.Read(ref _handshakeComplete) != 0;

    /// <summary>The room this connection joined, or null before the handshake completes.</summary>
    public IRoom? Room => _room;

    /// <summary>Frames this connection had to drop because its outbound queue was full.</summary>
    public long DroppedOutboundFrames => Volatile.Read(ref _outboundDropped);

    /// <summary>Why the session is ending; <see cref="RejectCode.None"/> while it is healthy.</summary>
    public RejectCode CloseCode => _closeCode;

    /// <inheritdoc />
    public bool TryEnqueue(in OutboundFrame frame)
    {
        if (!_isOpen || frame.IsEmpty)
        {
            return false;
        }

        if (_outbound.Writer.TryWrite(frame))
        {
            return true;
        }

        Interlocked.Increment(ref _outboundDropped);
        _metrics.Increment(NetCounter.OutboundDroppedQueueFull);
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
            TryQueueRejectEvent(code, _closeReason);
        }

        // Completing the writer lets the send loop drain what is already queued and then run the close
        // handshake; the linger cancel is the backstop for a peer that never answers.
        _outbound.Writer.TryComplete();
        try
        {
            _cts.CancelAfter(CloseLingerMilliseconds);
        }
        catch (ObjectDisposedException)
        {
            // Teardown already finished; nothing left to cancel.
        }

        _logger.LogDebug(
            "Closing client {ClientId} from {RemoteIp}: {RejectCode} ({CloseCode}) {Reason}",
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
            _logger.LogError(ex, "Receive loop for client {ClientId} failed", ClientId);
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
                _logger.LogWarning(ex, "Send loop for client {ClientId} ended with an error", ClientId);
            }

            LeaveRoom();
            FramePool.Return(_receiveBuffer);
            _cts.Dispose();
            _metrics.Increment(NetCounter.ConnectionsClosed);
        }
    }

    /// <summary>
    /// Called by the supervisor's sweep. Enforces the handshake deadline before the join and the idle
    /// timeout after it, so neither needs a per-connection timer.
    /// </summary>
    /// <param name="nowTimestamp">A <see cref="Stopwatch.GetTimestamp"/> reading shared by the sweep.</param>
    internal void CheckDeadlines(long nowTimestamp)
    {
        if (Volatile.Read(ref _closeState) != 0)
        {
            return;
        }

        if (Volatile.Read(ref _handshakeComplete) == 0)
        {
            if (_handshakeTimeoutTicks > 0 && nowTimestamp - _createdTimestamp >= _handshakeTimeoutTicks)
            {
                _metrics.Increment(NetCounter.HandshakeTimeouts);
                RequestClose(RejectCode.IdleTimeout, "no HelloRequest arrived in time");
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

    /// <summary>Records that the client asked to leave, so teardown reports <c>LeftVoluntarily</c>.</summary>
    internal void MarkVoluntaryLeave() => Volatile.Write(ref _voluntaryLeave, 1);

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
                _logger.LogDebug(ex, "Client {ClientId} disconnected while receiving", ClientId);
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
                    _metrics.Increment(NetCounter.InboundOversized);
                    _metrics.Increment(NetCounter.QuotaPayloadBreaches);
                    RequestClose(RejectCode.PayloadTooLarge, $"a message exceeded {_receiveCapacity} bytes");
                    return;
                }

                continue;
            }

            if (accumulated == 0)
            {
                // Empty binary message: nothing to dispatch, and not worth killing a client over.
                continue;
            }

            Volatile.Write(ref _lastInboundTimestamp, Stopwatch.GetTimestamp());
            bool keepReading = HandleMessage(_receiveBuffer.AsSpan(0, accumulated));
            accumulated = 0;
            if (!keepReading)
            {
                return;
            }
        }
    }

    /// <summary>Routes one complete frame. False means the session is ending.</summary>
    private bool HandleMessage(ReadOnlySpan<byte> frame)
    {
        if (Volatile.Read(ref _handshakeComplete) == 0)
        {
            return TryCompleteHandshake(frame);
        }

        InboundDispatcher? dispatcher = _dispatcher;
        if (dispatcher is null)
        {
            _logger.LogError("Client {ClientId} is joined but has no dispatcher", ClientId);
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
            _logger.LogError(ex, "Handshake for client {ClientId} from {RemoteIp} threw", ClientId, RemoteIp);
            RequestClose(RejectCode.InternalError, "the handshake failed on the server");
            return false;
        }

        _room = room;
        _dispatcher = new InboundDispatcher(this, room, _netOptions, _quotas, _metrics, _logger);
        Volatile.Write(ref _lastInboundTimestamp, Stopwatch.GetTimestamp());
        Volatile.Write(ref _handshakeComplete, 1);

        _logger.LogInformation(
            "Client {ClientId} ({DisplayName}) from {RemoteIp} joined room {RoomId}",
            ClientId,
            DisplayName,
            RemoteIp,
            room.Config.RoomId);
        return true;
    }

    private async Task SendLoopAsync()
    {
        CancellationToken token = _cts.Token;
        try
        {
            while (await _outbound.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (_outbound.Reader.TryRead(out OutboundFrame frame))
                {
                    bool faulted = false;
                    try
                    {
                        if (_socket.State != WebSocketState.Open)
                        {
                            // Drain the rest so every pooled buffer still goes home.
                            continue;
                        }

                        await _socket
                            .SendAsync(frame.Memory, WebSocketMessageType.Binary, true, token)
                            .ConfigureAwait(false);
                        _metrics.Increment(NetCounter.OutboundFramesSent);
                        _metrics.Add(NetCounter.OutboundBytesSent, frame.Length);
                    }
                    catch (WebSocketException ex)
                    {
                        _metrics.Increment(NetCounter.SendFailures);
                        _logger.LogDebug(ex, "Client {ClientId} went away mid-send", ClientId);
                        faulted = true;
                    }
                    catch (ObjectDisposedException)
                    {
                        faulted = true;
                    }
                    finally
                    {
                        FramePool.Return(frame.Buffer);
                    }

                    if (faulted)
                    {
                        _isOpen = false;
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Linger expired or the host aborted the request.
        }
        catch (ChannelClosedException)
        {
            // Writer completed between WaitToReadAsync and TryRead.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Send loop for client {ClientId} stopped unexpectedly", ClientId);
        }
        finally
        {
            _isOpen = false;

            // Completing first makes every later TryEnqueue fail, so a frame written by the room during
            // teardown is returned by its owner instead of being stranded in the queue.
            _outbound.Writer.TryComplete();
            while (_outbound.Reader.TryRead(out OutboundFrame leftover))
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
            _logger.LogDebug(ex, "Close handshake with client {ClientId} did not complete", ClientId);
        }
    }

    private void TryQueueRejectEvent(RejectCode code, string reason)
    {
        OutboundFrame frame;
        try
        {
            frame = FramePool.EncodeControl(
                MessageTypeIds.RejectEvent,
                new RejectEvent { Code = (ushort)code, Message = reason });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not encode the RejectEvent for client {ClientId}", ClientId);
            return;
        }

        // Written straight to the queue: TryEnqueue already refuses frames on a closing connection.
        if (!_outbound.Writer.TryWrite(frame))
        {
            FramePool.Return(frame.Buffer);
        }
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
