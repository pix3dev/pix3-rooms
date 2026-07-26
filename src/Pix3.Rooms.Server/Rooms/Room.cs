using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Pix3.Rooms.Protocol;
using Pix3.Rooms.Server.Net;
using Pix3.Rooms.Server.Replication;

namespace Pix3.Rooms.Server.Rooms;

/// <summary>
/// One room: its own membership, its own entity replication, its own tick loop and its own budget.
/// Nothing in here is shared with another room — that is the whole point of the type.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading contract.</b> Exactly three members may be called from a socket thread:
/// <see cref="TryJoin"/>, <see cref="Leave"/> and <see cref="TryEnqueueInbound"/> (plus
/// <see cref="SnapshotStats"/> and <see cref="CloseAll"/> from admin threads). They only touch
/// concurrent collections and interlocked counters. Everything else — message handling,
/// <see cref="IRoomReplication"/>, room vars, the entity mirror, fan-out — belongs to the tick thread,
/// so room logic never needs a lock.
/// </para>
/// <para>
/// <b>Tick order.</b> Pending joins (subscribe + room vars + peer announce) → pending leaves
/// (despawn owned entities + peer announce) → drain inbound (capped) → publish AOI focus →
/// <see cref="IRoomReplication.Tick"/> → per-member snapshot or delta frame → ~1 Hz
/// <see cref="RoomInfoEvent"/> → record tick duration.
/// </para>
/// <para>
/// <b>Allocations.</b> A steady-state tick allocates nothing: the member list is a reused array
/// refreshed only when membership changes, every outbound buffer comes from <see cref="FramePool"/>,
/// control messages are re-used scratch instances encoded through the pooled writer, and the hot
/// inbound frame (<c>EntityUpdateFrame</c>) is read straight out of the rented receive buffer.
/// </para>
/// </remarks>
public sealed partial class Room : IRoom, IDisposable
{
    private readonly RoomConfig _config;
    private readonly IRoomReplication _replication;
    private readonly RoomServerOptions _options;
    private readonly ILogger<Room> _logger;

    // Membership is written from socket threads (join/leave) and read from the tick thread.
    private readonly ConcurrentDictionary<uint, RoomMember> _members = new();
    private readonly ConcurrentQueue<RoomMember> _pendingJoins = new();
    private readonly ConcurrentQueue<PendingLeave> _pendingLeaves = new();
    private readonly Channel<InboundMessage> _inbound;

    // Tick-thread snapshot of membership. Rebuilt only when _membershipVersion moves, so the per-tick
    // per-client loop is a plain array walk with no enumerator allocation.
    private RoomMember[] _memberList = new RoomMember[16];
    private int _memberCount;
    private int _memberListVersion = -1;
    private int _membershipVersion;

    // Tick-thread-only room state.
    private readonly Dictionary<string, byte[]> _roomVars = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, EntityInfo> _entities = new();
    private readonly List<uint> _despawnScratch = new(64);

    // Re-used control-message instances. Encoding is synchronous and single-threaded, so one instance
    // per message type is enough and keeps the control path off the allocation profile too.
    private readonly RoomInfoEvent _roomInfoScratch = new();
    private readonly RoomVarsEvent _roomVarsScratch = new();
    private readonly string[] _roomVarKeyScratch = new string[1];
    private readonly byte[][] _roomVarValueScratch = new byte[1][];
    private readonly PeerJoinedEvent _peerJoinedScratch = new();
    private readonly PeerLeftEvent _peerLeftScratch = new();
    private readonly ChatMessageEvent _chatScratch = new();
    private readonly EntitySpawnAckEvent _spawnAckScratch = new();
    private readonly EntityColdPropsEvent _coldPropsScratch = new();
    private readonly RemoteEventBroadcast _remoteEventScratch = new();
    private readonly PongEvent _pongScratch = new();

    private readonly TickHistogram _tickHistogram;
    private readonly long _tickBudgetTimestampTicks;
    private readonly int _tickIntervalMs;

    private int _reservedSlots;
    private long _joinSequence;
    private int _closing;
    private int _runState;
    private int _consecutiveTickFailures;
    private uint _serverTick;
    private uint _lastRoomInfoTick;
    private uint _lastDrainWarningTick;
    private uint _hostClientId;
    private long _bytesOutTotal;
    private long _bytesOutAtLastSample;
    private long _lastRateTimestamp;
    private long _lastActivityUtcTicks;

    // Counters read from other threads via SnapshotStats / the public diagnostics properties.
    private long _droppedFrames;
    private long _budgetOverruns;
    private long _inboundDropped;
    private long _bytesOutPerSecond;
    private int _entityCountSnapshot;
    private uint _serverTickSnapshot;
    private long _drainSaturatedTicks;
    private long _malformedMessages;
    private long _messagesFromNonMembers;
    private long _unroutableMessages;
    private long _ownershipViolations;
    private long _illegalMaskRecords;
    private long _nonFiniteRecords;
    private long _spawnRejections;
    private long _chatThrottled;
    private long _roomVarRejections;
    private long _remoteEventRejections;
    private long _serverTargetedRemoteEvents;
    private long _coldPropsRejections;

    /// <summary>
    /// Creates a room. <paramref name="config"/> must already have gone through
    /// <see cref="RoomConfigValidator"/> — a room never re-validates or clamps its own config.
    /// </summary>
    /// <param name="config">Normalized creation parameters.</param>
    /// <param name="replication">This room's own replication instance; never shared with another room.</param>
    /// <param name="options">Server-wide room knobs; already normalized by the caller.</param>
    /// <param name="logger">Logger; every message carries the room id.</param>
    public Room(RoomConfig config, IRoomReplication replication, RoomServerOptions options, ILogger<Room> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(replication);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _config = config;
        _replication = replication;
        _options = options;
        _logger = logger;

        _inbound = Channel.CreateBounded<InboundMessage>(new BoundedChannelOptions(options.InboundQueueCapacity)
        {
            // Wait (not DropOldest): TryWrite must report the overflow so the caller can return the
            // dropped frame's buffer to the pool. DropOldest would silently leak it.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        _tickIntervalMs = Math.Max(1, 1000 / config.TickHz);
        _tickBudgetTimestampTicks = Stopwatch.Frequency * _tickIntervalMs / 1000L;

        long now = Stopwatch.GetTimestamp();
        _tickHistogram = new TickHistogram(options.TickHistogramWindowSeconds, now);
        _lastRateTimestamp = now;

        DateTimeOffset created = DateTimeOffset.UtcNow;
        CreatedAt = created;
        _lastActivityUtcTicks = created.UtcTicks;
    }

    /// <inheritdoc />
    public RoomConfig Config => _config;

    /// <inheritdoc />
    public int PlayerCount => _members.Count;

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; }

    /// <inheritdoc />
    public DateTimeOffset LastActivityAt => new(Volatile.Read(ref _lastActivityUtcTicks), TimeSpan.Zero);

    /// <summary>True once the room stopped admitting members (shutdown or fatal tick failure).</summary>
    public bool IsClosing => Volatile.Read(ref _closing) != 0;

    /// <summary>
    /// The member allowed to write room vars, or 0 when the room is empty. The first member to join
    /// becomes host; when it leaves the longest-present remaining member is promoted. The wire protocol
    /// has no host notification, so this is only observable through who can change room vars.
    /// </summary>
    public uint HostClientId => Volatile.Read(ref _hostClientId);

    /// <summary>Most recently completed tick.</summary>
    public uint ServerTick => Volatile.Read(ref _serverTickSnapshot);

    /// <summary>Inbound messages rejected because the room queue was full.</summary>
    public long InboundDropped => Interlocked.Read(ref _inboundDropped);

    /// <summary>Ticks that hit <see cref="RoomServerOptions.MaxDrainPerTick"/> and deferred the rest.</summary>
    public long DrainSaturatedTicks => Volatile.Read(ref _drainSaturatedTicks);

    /// <summary>Frames that failed to decode, or decoded to null.</summary>
    public long MalformedMessages => Volatile.Read(ref _malformedMessages);

    /// <summary>Messages whose sender had already left the room.</summary>
    public long MessagesFromNonMembers => Volatile.Read(ref _messagesFromNonMembers);

    /// <summary>Frames whose TypeId this room does not route (including the app-reserved range).</summary>
    public long UnroutableMessages => Volatile.Read(ref _unroutableMessages);

    /// <summary>Entity mutations refused because the sender did not own the entity.</summary>
    public long OwnershipViolations => Volatile.Read(ref _ownershipViolations);

    /// <summary>Client delta records carrying a mask bit clients may not set.</summary>
    public long IllegalMaskRecords => Volatile.Read(ref _illegalMaskRecords);

    /// <summary>Client records carrying NaN/±∞ coordinates, which would poison the spatial hash.</summary>
    public long NonFiniteRecords => Volatile.Read(ref _nonFiniteRecords);

    /// <summary>Spawn requests refused (entity limit, per-owner quota, non-finite spawn position).</summary>
    public long SpawnRejections => Volatile.Read(ref _spawnRejections);

    /// <summary>Chat messages dropped by the per-member rate limit.</summary>
    public long ChatThrottled => Volatile.Read(ref _chatThrottled);

    /// <summary>Room-var writes refused (not host, bad key, oversized value, too many keys).</summary>
    public long RoomVarRejections => Volatile.Read(ref _roomVarRejections);

    /// <summary>Remote events refused (bad name, oversized payload, unknown target).</summary>
    public long RemoteEventRejections => Volatile.Read(ref _remoteEventRejections);

    /// <summary>
    /// Remote events addressed to <see cref="RemoteEventTarget.Server"/>. A Relay room has no
    /// server-side game logic to receive them, so they are counted and dropped.
    /// </summary>
    public long ServerTargetedRemoteEvents => Volatile.Read(ref _serverTargetedRemoteEvents);

    /// <summary>Cold-props writes refused (oversized, or naming an entity the room does not know).</summary>
    public long ColdPropsRejections => Volatile.Read(ref _coldPropsRejections);

    /// <summary>Total bytes handed to connections' send queues.</summary>
    public long BytesOutTotal => Volatile.Read(ref _bytesOutTotal);

    // ── Membership ────────────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Safe from a socket thread. Capacity is reserved with an interlocked counter so two simultaneous
    /// joins can never both squeeze past <see cref="RoomConfig.MaxPlayers"/>. State that needs the
    /// replication instance (subscribe, room vars, snapshot, peer announce) is queued for the next
    /// tick, because <see cref="IRoomReplication"/> is single-threaded by contract.
    /// </remarks>
    public bool TryJoin(IClientConnection connection, out RejectCode reject)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (Volatile.Read(ref _closing) != 0)
        {
            reject = RejectCode.RoomClosing;
            return false;
        }

        if (!connection.IsOpen)
        {
            reject = RejectCode.BadRequest;
            return false;
        }

        int reserved = Interlocked.Increment(ref _reservedSlots);
        if (reserved > _config.MaxPlayers)
        {
            Interlocked.Decrement(ref _reservedSlots);
            reject = RejectCode.RoomFull;
            return false;
        }

        var member = new RoomMember(connection, Interlocked.Increment(ref _joinSequence));
        if (!_members.TryAdd(connection.ClientId, member))
        {
            Interlocked.Decrement(ref _reservedSlots);
            _logger.LogWarning("Room {RoomId} refused client {ClientId}: id already joined", _config.RoomId, connection.ClientId);
            reject = RejectCode.BadRequest;
            return false;
        }

        Interlocked.Increment(ref _membershipVersion);
        _pendingJoins.Enqueue(member);
        StampActivity();

        reject = RejectCode.None;
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Safe from a socket thread and idempotent. Membership drops immediately (freeing the slot); the
    /// replication half — despawning the leaver's entities, dropping its known-set — plus the
    /// <see cref="PeerLeftEvent"/> fan-out run on the next tick.
    /// </remarks>
    public void Leave(uint clientId, LeaveReason reason)
    {
        if (!_members.TryRemove(clientId, out RoomMember? member))
        {
            return;
        }

        Interlocked.Decrement(ref _reservedSlots);
        Interlocked.Increment(ref _membershipVersion);
        _pendingLeaves.Enqueue(new PendingLeave(member, reason));
        StampActivity();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Never blocks and never throws: the queue is bounded with <see cref="BoundedChannelFullMode.Wait"/>
    /// and <c>TryWrite</c> simply reports false when it is full or completed. On false the caller keeps
    /// ownership of <see cref="InboundMessage.Payload"/> and must return it to <see cref="FramePool"/>.
    /// </remarks>
    public bool TryEnqueueInbound(in InboundMessage message)
    {
        if (Volatile.Read(ref _closing) != 0)
        {
            Interlocked.Increment(ref _inboundDropped);
            return false;
        }

        if (_inbound.Writer.TryWrite(message))
        {
            StampActivity();
            return true;
        }

        Interlocked.Increment(ref _inboundDropped);
        return false;
    }

    /// <summary>
    /// Stops admitting members. Called on shutdown, on destruction and after a fatal tick failure.
    /// </summary>
    public void BeginShutdown() => Volatile.Write(ref _closing, 1);

    /// <summary>
    /// Removes every member and closes their sockets with <paramref name="code"/>. Safe from the
    /// manager's thread: it only removes membership (queueing the replication cleanup) and asks each
    /// connection to close.
    /// </summary>
    /// <param name="code">Why the sessions are ending; drives the WS close code and the RejectEvent.</param>
    /// <param name="reason">Leave reason reported to peers if the room still ticks.</param>
    /// <param name="message">Human-readable detail for the client.</param>
    public void CloseAll(RejectCode code, LeaveReason reason, string message)
    {
        BeginShutdown();

        foreach (KeyValuePair<uint, RoomMember> pair in _members)
        {
            RoomMember member = pair.Value;
            Leave(member.ClientId, reason);
            try
            {
                member.Connection.RequestClose(code, message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Room {RoomId} failed to close client {ClientId}", _config.RoomId, member.ClientId);
            }
        }
    }

    // ── Tick loop ─────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _runState, 1, 0) != 0)
        {
            throw new InvalidOperationException($"Room '{_config.RoomId}' is already running.");
        }

        _logger.LogInformation(
            "Room {RoomId} (project {ProjectId}) started: {TickHz} Hz, {IntervalMs} ms budget, {MaxPlayers} players, {MaxEntities} entities, AOI {AoiRadius}",
            _config.RoomId, _config.ProjectId, _config.TickHz, _tickIntervalMs, _config.MaxPlayers, _config.MaxEntities, _config.AoiRadius);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_tickIntervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!ExecuteTick())
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown: the manager cancelled the room's token.
        }
        finally
        {
            BeginShutdown();
            DrainAndDiscardInbound();
            _logger.LogInformation("Room {RoomId} stopped after {Ticks} ticks", _config.RoomId, _serverTick);
        }
    }

    /// <summary>Runs one tick. Returns false when the room has failed too often to keep going.</summary>
    private bool ExecuteTick()
    {
        long started = Stopwatch.GetTimestamp();
        uint tick = ++_serverTick;
        bool keepRunning = true;

        try
        {
            RefreshMemberList();
            ProcessPendingJoins();
            ProcessPendingLeaves();
            RefreshMemberList();
            DrainInbound(tick);
            PublishSubscriberFocus();

            _replication.Tick(tick);
            Volatile.Write(ref _entityCountSnapshot, _replication.EntityCount);

            FanOutState();
            MaybeSendRoomInfo(tick, started);

            _consecutiveTickFailures = 0;
        }
        catch (Exception ex)
        {
            _consecutiveTickFailures++;
            _logger.LogError(
                ex,
                "Room {RoomId} tick {Tick} failed ({Failures}/{MaxFailures} consecutive)",
                _config.RoomId, tick, _consecutiveTickFailures, _options.MaxConsecutiveTickFailures);

            if (_consecutiveTickFailures >= _options.MaxConsecutiveTickFailures)
            {
                _logger.LogCritical(
                    "Room {RoomId} is closing after {Failures} consecutive failed ticks; disconnecting {PlayerCount} members",
                    _config.RoomId, _consecutiveTickFailures, _members.Count);
                CloseAll(RejectCode.InternalError, LeaveReason.Error, "room internal error");
                keepRunning = false;
            }
        }
        finally
        {
            long elapsed = Stopwatch.GetTimestamp() - started;
            _tickHistogram.Record(elapsed * 1000.0 / Stopwatch.Frequency, started + elapsed);
            if (elapsed > _tickBudgetTimestampTicks)
            {
                _budgetOverruns++;
            }

            Volatile.Write(ref _serverTickSnapshot, tick);
        }

        return keepRunning;
    }

    /// <summary>
    /// Rebuilds the tick-thread member array when membership changed. A no-op (one interlocked read and
    /// an int compare) on the overwhelming majority of ticks.
    /// </summary>
    private void RefreshMemberList()
    {
        int version = Volatile.Read(ref _membershipVersion);
        if (version == _memberListVersion)
        {
            return;
        }

        int count = 0;
        foreach (KeyValuePair<uint, RoomMember> pair in _members)
        {
            if (count == _memberList.Length)
            {
                Array.Resize(ref _memberList, count * 2);
            }

            _memberList[count++] = pair.Value;
        }

        // Drop stale tail references so a departed member is not kept alive by the scratch array.
        if (count < _memberList.Length)
        {
            Array.Clear(_memberList, count, _memberList.Length - count);
        }

        _memberCount = count;
        _memberListVersion = version;
    }

    private void ProcessPendingJoins()
    {
        while (_pendingJoins.TryDequeue(out RoomMember? member))
        {
            if (!_members.ContainsKey(member.ClientId))
            {
                // Joined and left before its first tick; nothing was ever registered for it.
                continue;
            }

            _replication.AddSubscriber(member.ClientId);
            member.SubscriberAdded = true;
            member.SnapshotPending = true;
            member.SnapshotCursor = 0;

            if (Volatile.Read(ref _hostClientId) == 0u)
            {
                Volatile.Write(ref _hostClientId, member.ClientId);
                _logger.LogInformation("Room {RoomId} host is now client {ClientId}", _config.RoomId, member.ClientId);
            }

            SendFullRoomVars(member);

            _peerJoinedScratch.ClientId = member.ClientId;
            _peerJoinedScratch.DisplayName = member.Connection.DisplayName;
            RefreshMemberList();
            BroadcastControlExcept(MessageTypeIds.PeerJoinedEvent, _peerJoinedScratch, member.ClientId);
            member.JoinAnnounced = true;

            _logger.LogInformation(
                "Room {RoomId} admitted client {ClientId} ({DisplayName}) from {RemoteIp}; {PlayerCount}/{MaxPlayers} members",
                _config.RoomId, member.ClientId, member.Connection.DisplayName, member.Connection.RemoteIp,
                _members.Count, _config.MaxPlayers);
        }
    }

    private void ProcessPendingLeaves()
    {
        while (_pendingLeaves.TryDequeue(out PendingLeave leave))
        {
            RoomMember member = leave.Member;

            _despawnScratch.Clear();
            _replication.RemoveOwner(member.ClientId, _despawnScratch);
            for (int i = 0; i < _despawnScratch.Count; i++)
            {
                _entities.Remove(_despawnScratch[i]);
            }

            if (member.SubscriberAdded)
            {
                _replication.RemoveSubscriber(member.ClientId);
                member.SubscriberAdded = false;
            }

            member.OwnedEntityCount = 0;
            member.FocusNetId = NetId.None;
            member.FocusDirty = false;
            member.SnapshotPending = false;

            if (member.JoinAnnounced)
            {
                member.JoinAnnounced = false;
                _peerLeftScratch.ClientId = member.ClientId;
                _peerLeftScratch.Reason = (byte)leave.Reason;
                RefreshMemberList();
                BroadcastControl(MessageTypeIds.PeerLeftEvent, _peerLeftScratch);
            }

            if (Volatile.Read(ref _hostClientId) == member.ClientId)
            {
                PromoteHost();
            }

            _logger.LogInformation(
                "Room {RoomId} released client {ClientId} ({Reason}); {DespawnedCount} entities despawned, {PlayerCount} members left",
                _config.RoomId, member.ClientId, leave.Reason, _despawnScratch.Count, _members.Count);
        }
    }

    /// <summary>Hands the host role to the longest-present remaining member, or nobody if empty.</summary>
    private void PromoteHost()
    {
        RefreshMemberList();

        uint host = 0u;
        long best = long.MaxValue;
        for (int i = 0; i < _memberCount; i++)
        {
            RoomMember candidate = _memberList[i];
            if (candidate.JoinSequence < best)
            {
                best = candidate.JoinSequence;
                host = candidate.ClientId;
            }
        }

        Volatile.Write(ref _hostClientId, host);
        _logger.LogInformation("Room {RoomId} host is now client {ClientId}", _config.RoomId, host);
    }

    private void DrainInbound(uint tick)
    {
        ChannelReader<InboundMessage> reader = _inbound.Reader;
        int budget = _options.MaxDrainPerTick;
        int drained = 0;

        while (drained < budget && reader.TryRead(out InboundMessage message))
        {
            drained++;
            try
            {
                if (_members.TryGetValue(message.ClientId, out RoomMember? member))
                {
                    Handle(member, in message, tick);
                }
                else
                {
                    _messagesFromNonMembers++;
                }
            }
            catch (Exception ex)
            {
                _malformedMessages++;
                _logger.LogDebug(
                    ex,
                    "Room {RoomId} failed to handle {MessageName} ({TypeId}) from client {ClientId} on tick {Tick}",
                    _config.RoomId, MessageTypeIds.GetName(message.TypeId), message.TypeId, message.ClientId, tick);
            }
            finally
            {
                ReturnPayload(in message);
            }
        }

        if (drained < budget)
        {
            return;
        }

        _drainSaturatedTicks++;
        int backlog = reader.CanCount ? reader.Count : 0;
        if (backlog > 0 && tick - _lastDrainWarningTick >= (uint)_config.TickHz)
        {
            _lastDrainWarningTick = tick;
            _logger.LogWarning(
                "Room {RoomId} tick {Tick} hit the inbound drain cap ({Budget}); {Backlog} messages deferred to later ticks",
                _config.RoomId, tick, budget, backlog);
        }
    }

    /// <summary>
    /// Publishes each member's area-of-interest centre once per tick, as
    /// <see cref="IRoomReplication.SetSubscriberFocus"/> requires, however many update frames it sent.
    /// </summary>
    private void PublishSubscriberFocus()
    {
        for (int i = 0; i < _memberCount; i++)
        {
            RoomMember member = _memberList[i];
            if (!member.FocusDirty)
            {
                continue;
            }

            member.FocusDirty = false;
            if (member.SubscriberAdded)
            {
                _replication.SetSubscriberFocus(member.ClientId, member.FocusX, member.FocusY);
            }
        }
    }

    /// <summary>Sends every member either its outstanding snapshot frames or this tick's delta.</summary>
    private void FanOutState()
    {
        int frameCapacity = _options.MaxFrameBytes;

        for (int i = 0; i < _memberCount; i++)
        {
            RoomMember member = _memberList[i];

            if (!member.Connection.IsOpen)
            {
                // Self-healing: the transport should have called Leave, but a room must never keep a
                // dead member occupying a slot.
                Leave(member.ClientId, LeaveReason.Disconnected);
                continue;
            }

            if (member.SnapshotPending)
            {
                WriteSnapshotFrames(member, frameCapacity);
                continue;
            }

            byte[] buffer = FramePool.Rent(frameCapacity);
            int written = _replication.WriteDelta(member.ClientId, buffer.AsSpan(0, frameCapacity));
            if (written <= 0)
            {
                // Nothing for this client this tick: a conforming server sends no frame at all.
                FramePool.Return(buffer);
                continue;
            }

            Enqueue(member, buffer, written);
        }
    }

    private void WriteSnapshotFrames(RoomMember member, int frameCapacity)
    {
        int emitted = 0;
        while (emitted < _options.MaxSnapshotFramesPerTick)
        {
            byte[] buffer = FramePool.Rent(frameCapacity);
            int written = _replication.WriteSnapshot(member.ClientId, buffer.AsSpan(0, frameCapacity), ref member.SnapshotCursor);
            if (written <= 0)
            {
                FramePool.Return(buffer);
                member.SnapshotPending = false;
                member.SnapshotCursor = 0;
                return;
            }

            Enqueue(member, buffer, written);
            emitted++;

            if (member.SnapshotCursor == 0)
            {
                member.SnapshotPending = false;
                return;
            }
        }
    }

    private void MaybeSendRoomInfo(uint tick, long timestamp)
    {
        if (tick - _lastRoomInfoTick < (uint)_config.TickHz)
        {
            return;
        }

        _lastRoomInfoTick = tick;

        double seconds = (timestamp - _lastRateTimestamp) / (double)Stopwatch.Frequency;
        if (seconds > 0.0)
        {
            long delta = _bytesOutTotal - _bytesOutAtLastSample;
            Volatile.Write(ref _bytesOutPerSecond, (long)(delta / seconds));
        }

        _bytesOutAtLastSample = _bytesOutTotal;
        _lastRateTimestamp = timestamp;

        int players = _members.Count;
        int entities = _entityCountSnapshot;
        _roomInfoScratch.PlayerCount = players > ushort.MaxValue ? ushort.MaxValue : (ushort)players;
        _roomInfoScratch.EntityCount = entities > ushort.MaxValue ? ushort.MaxValue : (ushort)entities;
        _roomInfoScratch.ServerTick = tick;
        BroadcastControl(MessageTypeIds.RoomInfoEvent, _roomInfoScratch);
    }

    // ── Fan-out ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Encodes <paramref name="message"/> once and sends a private copy to every member of
    /// <b>this</b> room. Tick-thread only.
    /// </summary>
    /// <typeparam name="T">A <c>[MemoryPackable]</c> control message type.</typeparam>
    /// <param name="typeId">The message's TypeId.</param>
    /// <param name="message">The message to fan out.</param>
    public void BroadcastControl<T>(byte typeId, T message) => FanOutControl(typeId, message, false, 0u);

    /// <summary>
    /// Same as <see cref="BroadcastControl{T}"/> but skips one member — the sender, for echo-free
    /// fan-out. Tick-thread only.
    /// </summary>
    /// <typeparam name="T">A <c>[MemoryPackable]</c> control message type.</typeparam>
    /// <param name="typeId">The message's TypeId.</param>
    /// <param name="message">The message to fan out.</param>
    /// <param name="excludeClientId">Member to skip.</param>
    public void BroadcastControlExcept<T>(byte typeId, T message, uint excludeClientId)
        => FanOutControl(typeId, message, true, excludeClientId);

    /// <summary>
    /// Sends one control message to one member. Returns false when the client is not a member, its
    /// socket is closed, or its send queue was full. Tick-thread only.
    /// </summary>
    /// <typeparam name="T">A <c>[MemoryPackable]</c> control message type.</typeparam>
    /// <param name="clientId">Recipient.</param>
    /// <param name="typeId">The message's TypeId.</param>
    /// <param name="message">The message to send.</param>
    public bool SendTo<T>(uint clientId, byte typeId, T message)
    {
        if (!_members.TryGetValue(clientId, out RoomMember? member) || !member.Connection.IsOpen)
        {
            return false;
        }

        OutboundFrame frame = FramePool.EncodeControl(typeId, message);
        if (member.Connection.TryEnqueue(frame))
        {
            _bytesOutTotal += frame.Length;
            return true;
        }

        FramePool.Return(frame.Buffer);
        _droppedFrames++;
        return false;
    }

    /// <remarks>
    /// Encode once, memcpy many: one MemoryPack pass, then a pooled copy per recipient. Frames are not
    /// refcounted, so two connections must never be handed the same buffer.
    /// </remarks>
    private void FanOutControl<T>(byte typeId, T message, bool hasExclusion, uint excludeClientId)
    {
        if (_memberCount == 0)
        {
            return;
        }

        OutboundFrame source = FramePool.EncodeControl(typeId, message);
        try
        {
            ReadOnlySpan<byte> bytes = source.Span;
            for (int i = 0; i < _memberCount; i++)
            {
                RoomMember member = _memberList[i];
                if (hasExclusion && member.ClientId == excludeClientId)
                {
                    continue;
                }

                if (!member.Connection.IsOpen)
                {
                    continue;
                }

                byte[] copy = FramePool.Rent(source.Length);
                bytes.CopyTo(copy);
                Enqueue(member, copy, source.Length);
            }
        }
        finally
        {
            FramePool.Return(source.Buffer);
        }
    }

    /// <summary>
    /// Hands a rented frame to a member's send queue, taking ownership on success and returning the
    /// buffer to the pool (and counting a drop) on failure.
    /// </summary>
    private void Enqueue(RoomMember member, byte[] buffer, int length)
    {
        var frame = new OutboundFrame(buffer, length);
        if (member.Connection.TryEnqueue(frame))
        {
            _bytesOutTotal += length;
            return;
        }

        FramePool.Return(buffer);
        _droppedFrames++;
    }

    // ── Stats and teardown ────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Safe from any thread. Entity count and server tick are values the tick thread published, not
    /// live reads of <see cref="IRoomReplication"/> — that instance is single-threaded by contract.
    /// </remarks>
    public RoomStats SnapshotStats() => new(
        _members.Count,
        Volatile.Read(ref _entityCountSnapshot),
        Volatile.Read(ref _serverTickSnapshot),
        _tickHistogram.Percentile(0.50),
        _tickHistogram.Percentile(0.99),
        Volatile.Read(ref _bytesOutPerSecond),
        Volatile.Read(ref _droppedFrames) + Interlocked.Read(ref _inboundDropped),
        Volatile.Read(ref _budgetOverruns));

    /// <summary>
    /// Closes the inbound queue and returns every buffer still sitting in it, so a destroyed room
    /// cannot leak pooled arrays.
    /// </summary>
    public void Dispose()
    {
        BeginShutdown();
        DrainAndDiscardInbound();
    }

    private void DrainAndDiscardInbound()
    {
        _inbound.Writer.TryComplete();
        while (_inbound.Reader.TryRead(out InboundMessage message))
        {
            ReturnPayload(in message);
        }
    }

    private static void ReturnPayload(in InboundMessage message)
    {
        byte[]? payload = message.Payload;
        if (payload is not null)
        {
            FramePool.Return(payload);
        }
    }

    private void StampActivity() => Volatile.Write(ref _lastActivityUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
}
