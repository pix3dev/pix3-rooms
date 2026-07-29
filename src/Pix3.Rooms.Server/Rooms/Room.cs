using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Pix3.Rooms.Protocol;
using Pix3.Rooms.Server.Net;
using Pix3.Rooms.Server.Replication;

namespace Pix3.Rooms.Server.Rooms;

/// <summary>
/// One room: its own membership, its own entity replication, its own tick thread and its own budget.
/// Nothing in here is shared with another room — that is the whole point of the type.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading contract.</b> Exactly four members may be called from a socket thread:
/// <see cref="TryJoin"/>, <see cref="TryResume"/>, <see cref="Leave"/> and
/// <see cref="TryEnqueueInbound"/> (plus <see cref="HostClientId"/>, <see cref="ServerTick"/>,
/// <see cref="SnapshotStats"/>, <see cref="SnapshotViolations"/> and <see cref="CloseAll"/> from admin
/// threads). They touch only concurrent collections, interlocked counters and volatile publishes.
/// Everything else — message handling, <see cref="RoomReplication"/>, room vars, the entity mirror,
/// fan-out — belongs to the tick thread, so room logic never needs a lock.
/// </para>
/// <para>
/// <b>Tick order.</b> Expire resume graces → pending leaves (entity policy + host migration + peer
/// announce) → pending admissions (subscribe + room vars + roster + peer announce) → drain inbound
/// (capped) → <see cref="RoomReplication.Tick"/> → per member: pending snapshot frames <i>or</i> delta,
/// then its signal batch → ~1 Hz <c>RoomInfoEvent</c> and counter publish → record tick body time.
/// </para>
/// <para>
/// <b>Admission fan-out order.</b> <c>RoomVarsChangedEvent</c>, then <c>RoomRosterEvent</c> (chunked,
/// only the last chunk <c>Final</c>), then the snapshot the state fan-out emits later in the same tick.
/// </para>
/// <para>
/// <b>Allocations.</b> A steady-state tick allocates nothing: the member list is a reused array refreshed
/// only when membership changes, every outbound buffer comes from <see cref="FramePool"/>, control
/// messages are re-used scratch instances encoded through the pooled writer, and the hot inbound frame
/// (<c>EntityUpdatePacket</c>) is read straight out of the rented receive buffer.
/// </para>
/// </remarks>
public sealed partial class Room : IRoom, IDisposable
{
    /// <summary>
    /// Spin margin on a platform whose sleep is accurate to about a millisecond (Linux, i.e. production).
    /// The tail only ever covers the last sliver.
    /// </summary>
    private const double FineSpinMarginMilliseconds = 2.0;

    /// <summary>
    /// Spin margin on a coarse-granularity platform. It must **exceed** the platform's timer slice, not
    /// merely be smaller than the tick: Windows' default slice is 15.625 ms, so a sleep aimed 2 ms short of
    /// the deadline routinely wakes up *past* it and the tail never gets to absorb anything — which is
    /// precisely the failure this loop was written to eliminate (measured: p99 start jitter 39.5 ms on a
    /// 50 ms tick). Sleeping until 17 ms out and spinning the rest costs real CPU, which is why the tail
    /// is also gated on the room actually having players.
    /// </summary>
    private const double CoarseSpinMarginMilliseconds = 17.0;

    /// <summary>Iterations per <see cref="Thread.SpinWait(int)"/> call while the spin tail runs.</summary>
    private const int SpinWaitIterations = 32;

    private readonly RoomConfig _config;
    private readonly IRoomReplication _replication;
    private readonly RoomServerOptions _options;
    private readonly ILogger<Room> _logger;

    // Membership is written from socket threads (join/resume/leave) and read from the tick thread.
    private readonly ConcurrentDictionary<uint, RoomMember> _members = new();
    private readonly ConcurrentDictionary<ResumeKey, RoomMember> _pendingResumes = new();
    private readonly ConcurrentQueue<PendingAdmission> _pendingAdmissions = new();
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
    private readonly List<uint> _reassignScratch = new(64);
    private readonly List<GraceEntry> _graceList = new(16);
    private readonly HashSet<ushort>? _allowedKinds;

    // Re-used control-message instances. Encoding is synchronous and single-threaded, so one instance
    // per message type is enough and keeps the control path off the allocation profile too.
    private readonly RoomInfoEvent _roomInfoScratch = new();
    private readonly RoomVarsChangedEvent _roomVarsScratch = new();
    private readonly string[] _roomVarKeyScratch = new string[1];
    private readonly byte[][] _roomVarValueScratch = new byte[1][];
    private readonly PeerJoinedEvent _peerJoinedScratch = new();
    private readonly PeerLeftEvent _peerLeftScratch = new();
    private readonly HostChangedEvent _hostChangedScratch = new();
    private readonly ChatMessageEvent _chatScratch = new();
    private readonly SpawnEntityResponse _spawnResponseScratch = new();
    private readonly EntityPropsChangedEvent _propsScratch = new();
    private readonly SignalEvent _signalScratch = new();

    /// <summary>UTF-8 staging for a signal name (≤64 chars can never exceed 4 bytes each).</summary>
    private readonly byte[] _signalNameScratch = new byte[4 * HotWire.MaxSignalNameLength];

    private readonly TickHistogram _tickHistogram;
    private readonly TickHistogram _jitterHistogram;
    private readonly long _tickBudgetTimestampTicks;
    private readonly long _spinMarginTimestampTicks;
    private readonly long _resumeGraceTimestampTicks;
    private readonly bool _coarseTimerPlatform;
    private readonly int _tickIntervalMs;

    private int _reservedSlots;
    private int _pendingResumeCount;
    private long _joinSequence;
    private int _closing;
    private int _runState;
    private int _consecutiveTickFailures;
    private uint _serverTick;
    private uint _lastRoomInfoTick;
    private uint _lastDrainWarningTick;
    private uint _lastSkipWarningTick;
    private uint _hostClientId;
    private long _bytesOutTotal;
    private long _bytesOutAtLastSample;
    private long _lastRateTimestamp;
    private long _lastActivityUtcTicks;

    // Counters read from other threads via SnapshotStats / the public diagnostics properties.
    private long _droppedFrames;
    private long _budgetOverruns;
    private long _skippedTicks;
    private long _inboundDropped;
    private long _bytesOutPerSecond;
    private long _resyncs;
    private long _violationTotal;
    private int _entityCountSnapshot;
    private uint _serverTickSnapshot;
    private long _drainSaturatedTicks;
    private long _malformedMessages;
    private long _messagesFromNonMembers;
    private long _unroutableMessages;
    private long _refusedEntityUpdates;
    private long _spawnRejections;
    private long _chatThrottled;
    private long _roomVarRejections;
    private long _signalRejections;
    private long _serverTargetedSignals;
    private long _coldPropsRejections;
    private long _resumesGranted;
    private long _resumeGracesStarted;
    private long _resumeGracesExpired;
    private long _hostMigrations;

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

        // An empty allowlist means "any kind", which the config validator permits and the composition root
        // is expected to forbid in production.
        _allowedKinds = config.AllowedKinds.Count == 0 ? null : new HashSet<ushort>(config.AllowedKinds);

        _tickIntervalMs = Math.Max(1, 1000 / config.TickHz);
        _tickBudgetTimestampTicks = Stopwatch.Frequency * _tickIntervalMs / 1000L;
        _resumeGraceTimestampTicks = Stopwatch.Frequency * options.ResumeGraceSeconds;

        // Windows' default timer granularity is 15.625 ms — ±31% jitter on a 50 ms tick — so a room there
        // spins the tail regardless of how busy it is. On Linux (production) a plain sleep is ~1 ms
        // accurate, and only a room with real players is worth a busy-wait.
        _coarseTimerPlatform = OperatingSystem.IsWindows();

        double spinMarginMs = _coarseTimerPlatform ? CoarseSpinMarginMilliseconds : FineSpinMarginMilliseconds;
        _spinMarginTimestampTicks = (long)(Stopwatch.Frequency * spinMarginMs / 1000.0);

        long now = Stopwatch.GetTimestamp();
        _tickHistogram = new TickHistogram(options.TickHistogramWindowSeconds, now);
        _jitterHistogram = new TickHistogram(options.TickHistogramWindowSeconds, now);
        _lastRateTimestamp = now;

        DateTimeOffset created = DateTimeOffset.UtcNow;
        CreatedAt = created;
        _lastActivityUtcTicks = created.UtcTicks;
    }

    /// <inheritdoc />
    public RoomConfig Config => _config;

    /// <inheritdoc />
    public int PlayerCount => _members.Count + Volatile.Read(ref _pendingResumeCount);

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; }

    /// <inheritdoc />
    public DateTimeOffset LastActivityAt => new(Volatile.Read(ref _lastActivityUtcTicks), TimeSpan.Zero);

    /// <summary>True once the room stopped admitting members (shutdown or fatal tick failure).</summary>
    public bool IsClosing => Volatile.Read(ref _closing) != 0;

    /// <inheritdoc />
    public uint HostClientId => Volatile.Read(ref _hostClientId);

    /// <inheritdoc />
    public uint ServerTick => Volatile.Read(ref _serverTickSnapshot);

    /// <summary>Sessions currently inside their resume grace.</summary>
    public int ResumableSessionCount => Volatile.Read(ref _pendingResumeCount);

    /// <summary>Inbound messages rejected because the room queue was full.</summary>
    public long InboundDropped => Interlocked.Read(ref _inboundDropped);

    /// <summary>Ticks that hit <see cref="RoomServerOptions.MaxDrainPerTick"/> and deferred the rest.</summary>
    public long DrainSaturatedTicks => Volatile.Read(ref _drainSaturatedTicks);

    /// <summary>Scheduled ticks the loop skipped because it was already past their deadline.</summary>
    public long SkippedTicks => Volatile.Read(ref _skippedTicks);

    /// <summary>Frames that failed to decode, or decoded to null.</summary>
    public long MalformedMessages => Volatile.Read(ref _malformedMessages);

    /// <summary>Messages whose sender had already left the room.</summary>
    public long MessagesFromNonMembers => Volatile.Read(ref _messagesFromNonMembers);

    /// <summary>Frames whose TypeId this room does not route (including the app-reserved range).</summary>
    public long UnroutableMessages => Volatile.Read(ref _unroutableMessages);

    /// <summary>Client update records replication refused (ownership, stale generation, illegal mask).</summary>
    public long RefusedEntityUpdates => Volatile.Read(ref _refusedEntityUpdates);

    /// <summary>Spawn requests refused (entity limit, per-owner quota, kind not allowed).</summary>
    public long SpawnRejections => Volatile.Read(ref _spawnRejections);

    /// <summary>Chat messages dropped by the per-member rate limit.</summary>
    public long ChatThrottled => Volatile.Read(ref _chatThrottled);

    /// <summary>Room-var writes refused (not host, bad key, oversized value, too many keys).</summary>
    public long RoomVarRejections => Volatile.Read(ref _roomVarRejections);

    /// <summary>Signals refused (bad name, oversized payload, unknown target, no AOI focus).</summary>
    public long SignalRejections => Volatile.Read(ref _signalRejections);

    /// <summary>
    /// Signals addressed to <see cref="SignalTarget.Server"/>. A Relay room has no server-side game logic
    /// to receive them, so they are counted and dropped — nothing is fanned out at Level 1.
    /// </summary>
    public long ServerTargetedSignals => Volatile.Read(ref _serverTargetedSignals);

    /// <summary>Cold-props writes refused (oversized, over rate, or naming an entity the room does not know).</summary>
    public long ColdPropsRejections => Volatile.Read(ref _coldPropsRejections);

    /// <summary>Sessions re-attached inside their resume grace.</summary>
    public long ResumesGranted => Volatile.Read(ref _resumesGranted);

    /// <summary>Socket teardowns that started a resume grace instead of leaving.</summary>
    public long ResumeGracesStarted => Volatile.Read(ref _resumeGracesStarted);

    /// <summary>Resume graces that ran out and became real leaves.</summary>
    public long ResumeGracesExpired => Volatile.Read(ref _resumeGracesExpired);

    /// <summary>Host promotions announced with <c>HostChangedEvent</c>.</summary>
    public long HostMigrations => Volatile.Read(ref _hostMigrations);

    /// <summary>Total bytes handed to connections' send queues.</summary>
    public long BytesOutTotal => Volatile.Read(ref _bytesOutTotal);

    // ── Membership ────────────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Safe from a socket thread. Capacity is reserved with an interlocked counter so two simultaneous
    /// joins can never both squeeze past <see cref="RoomConfig.MaxPlayers"/>. State that needs the
    /// replication instance (subscribe, room vars, snapshot, peer announce) is queued for the next tick,
    /// because <see cref="RoomReplication"/> is single-threaded by contract — so the grant is built from
    /// immutable config plus volatile publishes, never from live room state.
    /// </remarks>
    public bool TryJoin(IClientConnection connection, out JoinGrant grant, out RejectCode reject)
    {
        ArgumentNullException.ThrowIfNull(connection);

        grant = default;

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

        var member = new RoomMember(connection, Interlocked.Increment(ref _joinSequence), _options.MaxEntitiesPerOwner)
        {
            ResumeKey = ResumeKey.Create(),
        };

        if (!_members.TryAdd(connection.ClientId, member))
        {
            Interlocked.Decrement(ref _reservedSlots);
            _logger.LogWarning("Room {RoomId} refused client {ClientId}: id already joined", _config.RoomId, connection.ClientId);
            reject = RejectCode.BadRequest;
            return false;
        }

        // The first member to arrive becomes host, published atomically so this join's own WelcomeEvent
        // reports it. Promotion (on the tick thread) is the only other writer, and it only ever runs when
        // the room is losing its host, so either interleaving ends with a valid host.
        Interlocked.CompareExchange(ref _hostClientId, member.ClientId, 0u);

        Interlocked.Increment(ref _membershipVersion);
        _pendingAdmissions.Enqueue(new PendingAdmission(member, isResume: false));
        StampActivity();

        grant = new JoinGrant(member.ClientId, HostClientId, ServerTick, member.ResumeKey.ToArray(), Resumed: false);
        reject = RejectCode.None;
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Safe from a socket thread. The presented key is the whole credential: it names one pending session
    /// and nothing else, so a client can neither claim an id nor resume somebody else's session.
    /// </para>
    /// <para>
    /// A resumed session never needs a slot reservation — its slot was never released — so
    /// <see cref="RejectCode.RoomFull"/> cannot happen here. The only real refusal is a closing room.
    /// </para>
    /// </remarks>
    public bool TryResume(IClientConnection connection, ReadOnlySpan<byte> resumeKey,
                          out JoinGrant grant, out RejectCode reject)
    {
        ArgumentNullException.ThrowIfNull(connection);

        grant = default;
        reject = RejectCode.None;

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

        // A key of the wrong length is not a resume attempt at all, and "not a resume" is never an error.
        if (resumeKey.Length != ResumeKey.Size)
        {
            return false;
        }

        var presented = new ResumeKey(resumeKey);
        if (presented.IsEmpty || !_pendingResumes.TryRemove(presented, out RoomMember? member))
        {
            return false;
        }

        Interlocked.Decrement(ref _pendingResumeCount);

        // Reattach BEFORE publishing the member: the tick thread must never see a member whose connection
        // is the dead socket it was filed with.
        member.Reattach(connection);

        if (!_members.TryAdd(member.ClientId, member))
        {
            // Should be impossible — a pending session's id is never a member. Re-arming the pending entry
            // would leak its reserved slot (only a queued grace gets a sweep entry), so the session is ended
            // for real instead and the transport surfaces a refusal.
            _logger.LogError(
                "Room {RoomId} could not re-attach resumed client {ClientId}: the id is already a member",
                _config.RoomId, member.ClientId);
            Interlocked.Decrement(ref _reservedSlots);
            _pendingLeaves.Enqueue(new PendingLeave(member, LeaveReason.Error, withGrace: false));
            reject = RejectCode.InternalError;
            return false;
        }

        Interlocked.Increment(ref _membershipVersion);
        Interlocked.Increment(ref _resumesGranted);
        _pendingAdmissions.Enqueue(new PendingAdmission(member, isResume: true));
        StampActivity();

        grant = new JoinGrant(member.ClientId, HostClientId, ServerTick, member.ResumeKey.ToArray(), Resumed: true);
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Safe from a socket thread and idempotent. A <see cref="LeaveReason.Disconnected"/> teardown files
    /// the session into the resume grace: its slot stays reserved, its entities stay alive and frozen (no
    /// socket is left to move them) and its <c>PeerLeftEvent</c> is deferred, because peers must not be
    /// told about a blip. Every other reason — a voluntary <c>LeaveCommand</c>, a kick, an idle timeout, a
    /// closing room — leaves for real, and the replication half of it runs on the next tick.
    /// </remarks>
    public void Leave(uint clientId, LeaveReason reason)
    {
        if (!_members.TryRemove(clientId, out RoomMember? member))
        {
            return;
        }

        Interlocked.Increment(ref _membershipVersion);

        bool graceable = reason == LeaveReason.Disconnected
            && _options.ResumeGraceSeconds > 0
            && Volatile.Read(ref _closing) == 0;

        if (graceable)
        {
            long epoch = member.BeginGrace(Stopwatch.GetTimestamp());
            if (_pendingResumes.TryAdd(member.GraceKey, member))
            {
                Interlocked.Increment(ref _pendingResumeCount);
                Interlocked.Increment(ref _resumeGracesStarted);
                _pendingLeaves.Enqueue(new PendingLeave(
                    member, reason, withGrace: true, member.GraceKey, member.GraceStartTimestamp, epoch));
                StampActivity();
                return;
            }

            // A key collision is a 2^-128 event; falling through to a real leave is the safe branch.
            _logger.LogError(
                "Room {RoomId} could not file client {ClientId} for resume (epoch {Epoch}); leaving for real",
                _config.RoomId, clientId, epoch);
        }

        Interlocked.Decrement(ref _reservedSlots);
        _pendingLeaves.Enqueue(new PendingLeave(member, reason, withGrace: false));
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
    /// connection to close. Sessions inside their resume grace are dropped too — a destroyed room has
    /// nothing left to resume into.
    /// </summary>
    /// <param name="code">Why the sessions are ending; drives the WS close code and the RejectedEvent.</param>
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
                // RequestClose sends the RejectedEvent first, so the client can show a real message.
                member.Connection.RequestClose(code, message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Room {RoomId} failed to close client {ClientId}", _config.RoomId, member.ClientId);
            }
        }

        DiscardPendingResumes(reason);
    }

    /// <summary>
    /// Drops every resumable session: their slots are released and the tick thread (if it still runs)
    /// resolves their entities by ownership policy.
    /// </summary>
    private void DiscardPendingResumes(LeaveReason reason)
    {
        foreach (KeyValuePair<ResumeKey, RoomMember> pair in _pendingResumes)
        {
            if (!_pendingResumes.TryRemove(pair.Key, out RoomMember? member))
            {
                continue;
            }

            Interlocked.Decrement(ref _pendingResumeCount);
            Interlocked.Decrement(ref _reservedSlots);
            _pendingLeaves.Enqueue(new PendingLeave(member, reason, withGrace: false));
        }
    }

    // ── Tick loop ─────────────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The loop runs on a <b>dedicated thread</b>, not on the thread pool and not on a
    /// <c>PeriodicTimer</c>: a timer coalesces missed ticks silently, and a pooled continuation inherits
    /// whatever scheduling latency the pool happens to have. The returned task completes when that thread
    /// exits, so <c>RoomManager</c> can await a room's shutdown exactly as before.
    /// </para>
    /// <para>
    /// <b>Absolute deadlines.</b> Tick <c>n</c> is due at <c>t0 + n × Stopwatch.Frequency / TickHz</c>,
    /// computed from the loop's own start rather than from the previous tick, so a slow tick cannot push
    /// every later one. A deadline already in the past is <b>skipped, never caught up</b>: replaying four
    /// ticks back to back would only make the next one late as well.
    /// </para>
    /// </remarks>
    public Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _runState, 1, 0) != 0)
        {
            throw new InvalidOperationException($"Room '{_config.RoomId}' is already running.");
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => TickThreadBody(cancellationToken, completion))
        {
            IsBackground = true,
            Name = $"pix3-room-{_config.RoomId}",
        };

        thread.Start();
        return completion.Task;
    }

    /// <summary>The thread body: runs the loop, then always completes the task it promised.</summary>
    private void TickThreadBody(CancellationToken cancellationToken, TaskCompletionSource completion)
    {
        Exception? failure = null;

        _logger.LogInformation(
            "Room {RoomId} (project {ProjectId}) started: {TickHz} Hz, {IntervalMs} ms budget, {MaxPlayers} players, "
            + "{MaxEntities} entities, AOI {AoiRadius}, world ({WorldOriginX}, {WorldOriginY}) size {WorldSize}, spin tail {SpinTail}",
            _config.RoomId, _config.ProjectId, _config.TickHz, _tickIntervalMs, _config.MaxPlayers, _config.MaxEntities,
            _config.AoiRadius, _config.WorldOriginX, _config.WorldOriginY, _config.WorldSize,
            _coarseTimerPlatform
                ? $"whenever occupied, {CoarseSpinMarginMilliseconds} ms margin (coarse platform timer)"
                : $"at {_options.SpinTailPlayerThreshold}+ players, {FineSpinMarginMilliseconds} ms margin");

        try
        {
            RunTickLoop(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown: the manager cancelled the room's token.
        }
        catch (Exception ex)
        {
            failure = ex;
            _logger.LogCritical(ex, "Room {RoomId} tick thread faulted on tick {Tick}", _config.RoomId, _serverTick);
        }
        finally
        {
            BeginShutdown();
            DrainAndDiscardInbound();
            _logger.LogInformation(
                "Room {RoomId} stopped after {Ticks} ticks ({SkippedTicks} skipped)",
                _config.RoomId, _serverTick, Volatile.Read(ref _skippedTicks));
        }

        if (failure is null)
        {
            completion.TrySetResult();
        }
        else
        {
            completion.TrySetException(failure);
        }
    }

    /// <summary>The absolute-deadline scheduler. Returns when cancelled or when the room gives up.</summary>
    private void RunTickLoop(CancellationToken cancellationToken)
    {
        long frequency = Stopwatch.Frequency;
        long origin = Stopwatch.GetTimestamp();
        long tickIndex = 1;

        while (!cancellationToken.IsCancellationRequested)
        {
            long deadline = origin + (tickIndex * frequency / _config.TickHz);
            WaitForDeadline(deadline, frequency);

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            long startedAt = Stopwatch.GetTimestamp();

            // Tick START jitter: how late this tick began against the deadline it was scheduled for. The
            // tick body histogram cannot see this, and it is the number that proves the scheduler works.
            _jitterHistogram.Record((startedAt - deadline) * 1000.0 / frequency, startedAt);

            if (!ExecuteTick((uint)tickIndex, startedAt))
            {
                break;
            }

            tickIndex = AdvanceTickIndex(origin, frequency, tickIndex);
        }
    }

    /// <summary>
    /// Sleeps (and, when the spin tail is enabled, spins the last sliver) until <paramref name="deadline"/>.
    /// </summary>
    /// <remarks>
    /// The spin tail is <b>conditional</b>: 64 idle rooms each burning a few percent of a core to
    /// busy-wait is a bad trade, and production is Linux, where a plain sleep is ~1 ms accurate. It
    /// engages on a coarse-granularity platform (Windows: 15.625 ms default timer slices) or once the room
    /// carries at least <see cref="RoomServerOptions.SpinTailPlayerThreshold"/> members — and in either
    /// case only while the room actually has members, because nobody can feel an empty room's jitter and
    /// the coarse-platform margin is expensive. Without the tail, a sub-millisecond remainder is simply not
    /// slept — a tick may start up to 1 ms early, which absolute deadlines absorb because the next deadline
    /// does not move.
    /// </remarks>
    private void WaitForDeadline(long deadline, long frequency)
    {
        bool spin = _memberCount > 0
            && (_coarseTimerPlatform || _memberCount >= _options.SpinTailPlayerThreshold);

        while (true)
        {
            long remaining = deadline - Stopwatch.GetTimestamp();
            if (remaining <= 0)
            {
                return;
            }

            long sleepTicks = spin ? remaining - _spinMarginTimestampTicks : remaining;
            int sleepMs = sleepTicks > 0 ? (int)(sleepTicks * 1000L / frequency) : 0;
            if (sleepMs > 0)
            {
                Thread.Sleep(sleepMs);
                continue;
            }

            if (!spin)
            {
                return;
            }

            Thread.SpinWait(SpinWaitIterations);
        }
    }

    /// <summary>
    /// Picks the next tick index, skipping every deadline already in the past rather than catching up.
    /// </summary>
    private long AdvanceTickIndex(long origin, long frequency, long current)
    {
        long next = current + 1;
        long elapsedTicks = (Stopwatch.GetTimestamp() - origin) * _config.TickHz / frequency;
        if (elapsedTicks < next)
        {
            return next;
        }

        long skipped = elapsedTicks - next + 1;
        _skippedTicks += skipped;

        uint tick = _serverTick;
        if (tick - _lastSkipWarningTick >= (uint)_config.TickHz)
        {
            _lastSkipWarningTick = tick;
            _logger.LogWarning(
                "Room {RoomId} skipped {Skipped} tick(s) after tick {Tick}: the loop is behind its {IntervalMs} ms budget",
                _config.RoomId, skipped, tick, _tickIntervalMs);
        }

        return elapsedTicks + 1;
    }

    /// <summary>Runs one tick. Returns false when the room has failed too often to keep going.</summary>
    /// <param name="tick">
    /// The scheduled tick index, so the wire's <c>ServerTick</c> tracks wall time: a skipped tick shows up
    /// as a gap rather than silently compressing the timeline.
    /// </param>
    /// <param name="started">Timestamp the tick began at, shared with the jitter histogram.</param>
    private bool ExecuteTick(uint tick, long started)
    {
        _serverTick = tick;
        bool keepRunning = true;

        try
        {
            ExpireResumeGraces(started);
            RefreshMemberList();

            // Leaves before admissions, and that order is load-bearing: a session that dropped and resumed
            // between two ticks has both a (grace) leave and an admission queued, and running the admission
            // first would subscribe it and then immediately unsubscribe it — a resumed client that never
            // receives another frame.
            ProcessPendingLeaves();
            ProcessPendingAdmissions();
            RefreshMemberList();
            DrainInbound(tick);

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

    /// <summary>
    /// Turns resume graces that ran out into real leaves. The sweep list is tick-thread-only and empty in
    /// steady state, so a room with nobody waiting to reconnect pays one <c>Count</c> compare — no
    /// concurrent-dictionary enumeration on the tick path.
    /// </summary>
    private void ExpireResumeGraces(long now)
    {
        if (_graceList.Count == 0)
        {
            return;
        }

        for (int i = _graceList.Count - 1; i >= 0; i--)
        {
            GraceEntry entry = _graceList[i];
            RoomMember member = entry.Member;

            // A newer grace epoch, or a member that already resumed, retires this entry without expiring
            // anything: only the epoch this entry was filed for may ever be expired by it.
            if (member.GraceEpoch != entry.Epoch || !member.AwaitingResume)
            {
                RemoveGraceEntry(i);
                continue;
            }

            if (now - entry.StartTimestamp < _resumeGraceTimestampTicks)
            {
                continue;
            }

            RemoveGraceEntry(i);

            if (!_pendingResumes.TryRemove(entry.Key, out RoomMember? expired))
            {
                continue;   // a resume won the race; nothing to expire
            }

            Interlocked.Decrement(ref _pendingResumeCount);
            Interlocked.Decrement(ref _reservedSlots);
            Interlocked.Increment(ref _resumeGracesExpired);

            _logger.LogInformation(
                "Room {RoomId} resume grace expired for client {ClientId} after {GraceSeconds}s",
                _config.RoomId, expired.ClientId, _options.ResumeGraceSeconds);

            // Only now do peers learn anything, and what they learn is Timeout — never Disconnected.
            CompleteLeave(expired, LeaveReason.Timeout);
        }
    }

    private void RemoveGraceEntry(int index)
    {
        int last = _graceList.Count - 1;
        _graceList[index] = _graceList[last];
        _graceList.RemoveAt(last);
    }

    private void ProcessPendingAdmissions()
    {
        while (_pendingAdmissions.TryDequeue(out PendingAdmission admission))
        {
            RoomMember member = admission.Member;
            if (!_members.TryGetValue(member.ClientId, out RoomMember? current) || !ReferenceEquals(current, member))
            {
                // Joined and left (or dropped again) before its first tick; nothing was ever registered for
                // it, and the id may already belong to a different session object.
                continue;
            }

            // A resumed session's known set is rebuilt from scratch, never assumed: AddSubscriber wipes it
            // and re-arms the snapshot, which is exactly the resume contract.
            _replication.AddSubscriber(member.ClientId);
            member.SubscriberAdded = true;

            // Focus follows the entities this session spawned, in spawn order — across a resume too, since
            // those entities were frozen rather than despawned.
            RebindFocus(member, member.FirstOwnedEntity);

            SendFullRoomVars(member);

            // After the room vars and before the snapshot, on a join and on a resume alike: a client is
            // never in its own PeerJoinedEvent fan-out, and a resume may have missed a join or a leave
            // during its grace.
            SendRoomRoster(member);

            if (admission.IsResume)
            {
                _logger.LogInformation(
                    "Room {RoomId} resumed client {ClientId} ({DisplayName}) from {RemoteIp}; {OwnedEntities} entities kept",
                    _config.RoomId, member.ClientId, member.Connection.DisplayName, member.Connection.RemoteIp,
                    member.OwnedEntityCount);
                continue;   // peers were never told it left, so nothing is announced
            }

            _peerJoinedScratch.ClientId = member.ClientId;
            _peerJoinedScratch.DisplayName = member.Connection.DisplayName;
            RefreshMemberList();
            BroadcastControlExcept(MessageTypeIds.PeerJoinedEvent, _peerJoinedScratch, member.ClientId);
            member.JoinAnnounced = true;

            _logger.LogInformation(
                "Room {RoomId} admitted client {ClientId} ({DisplayName}) from {RemoteIp}; {PlayerCount}/{MaxPlayers} members",
                _config.RoomId, member.ClientId, member.Connection.DisplayName, member.Connection.RemoteIp,
                PlayerCount, _config.MaxPlayers);
        }
    }

    private void ProcessPendingLeaves()
    {
        while (_pendingLeaves.TryDequeue(out PendingLeave leave))
        {
            RoomMember member = leave.Member;

            if (leave.WithGrace)
            {
                BeginGraceServerSide(in leave);
                continue;
            }

            if (member.SubscriberAdded)
            {
                _replication.RemoveSubscriber(member.ClientId);
                member.SubscriberAdded = false;
            }

            CompleteLeave(member, leave.Reason);
        }
    }

    /// <summary>
    /// The replication half of a socket teardown that is still resumable: stop serving the session and
    /// leave everything else exactly as it is.
    /// </summary>
    /// <remarks>
    /// The subscriber is dropped so the room does no per-tick work for a client that cannot receive
    /// anything — and because a resume rebuilds the known set from scratch anyway, keeping it would only
    /// be a lie the next snapshot has to undo. The session's <b>entities are untouched</b>: nobody owns
    /// the socket that was moving them, so they simply stop changing. That is what "frozen" means here.
    /// </remarks>
    private void BeginGraceServerSide(in PendingLeave leave)
    {
        RoomMember member = leave.Member;
        if (member.SubscriberAdded)
        {
            _replication.RemoveSubscriber(member.ClientId);
            member.SubscriberAdded = false;
        }

        _graceList.Add(new GraceEntry(member, leave.GraceKey, leave.GraceStartTimestamp, leave.GraceEpoch));

        _logger.LogInformation(
            "Room {RoomId} client {ClientId} dropped; resumable for {GraceSeconds}s with {OwnedEntities} entities frozen",
            _config.RoomId, member.ClientId, _options.ResumeGraceSeconds, member.OwnedEntityCount);
    }

    /// <summary>
    /// Finishes a leave for good: <c>Owned</c> entities despawn, host migration runs,
    /// <c>Shared</c>/<c>Transferable</c> entities move to the heir, and peers are told.
    /// </summary>
    private void CompleteLeave(RoomMember member, LeaveReason reason)
    {
        _despawnScratch.Clear();
        _replication.RemoveOwner(member.ClientId, _despawnScratch);
        for (int i = 0; i < _despawnScratch.Count; i++)
        {
            _entities.Remove(_despawnScratch[i]);
        }

        int despawned = _despawnScratch.Count;

        // Host migration first, so the entities that survive their owner have somewhere to go.
        uint heir = HostClientId;
        if (heir == member.ClientId)
        {
            heir = PromoteHost(member.ClientId);
        }

        int reassigned = ResolveSurvivingEntities(member.ClientId, heir);

        member.ClearOwnedEntities();
        member.FocusNetId = NetId.None;

        if (member.JoinAnnounced)
        {
            member.JoinAnnounced = false;
            _peerLeftScratch.ClientId = member.ClientId;
            _peerLeftScratch.Reason = (byte)reason;
            RefreshMemberList();
            BroadcastControl(MessageTypeIds.PeerLeftEvent, _peerLeftScratch);
        }

        _logger.LogInformation(
            "Room {RoomId} released client {ClientId} ({Reason}); {DespawnedCount} entities despawned, "
            + "{ReassignedCount} reassigned to {Heir}, {PlayerCount} members left",
            _config.RoomId, member.ClientId, reason, despawned, reassigned, heir, PlayerCount);
    }

    /// <summary>
    /// Moves the leaver's <c>Shared</c>/<c>Transferable</c> entities to <paramref name="heir"/>, or
    /// despawns them when the room has no host left to inherit them.
    /// </summary>
    /// <returns>How many entities changed owner.</returns>
    private int ResolveSurvivingEntities(uint leaverId, uint heir)
    {
        if (heir != 0u && heir != leaverId)
        {
            _reassignScratch.Clear();
            _replication.ReassignOwner(leaverId, heir, _reassignScratch);
            for (int i = 0; i < _reassignScratch.Count; i++)
            {
                uint netId = _reassignScratch[i];
                if (_entities.TryGetValue(netId, out EntityInfo info))
                {
                    // The heir inherits ownership but NOT the entity's place in its focus order: a host
                    // that inherits a pickup must not have its camera hijacked by it.
                    info.OwnerId = heir;
                    _entities[netId] = info;
                }
            }

            return _reassignScratch.Count;
        }

        // Nobody left to inherit: a Shared entity with a departed owner would linger for the room's whole
        // life, visible and immovable, so it goes with its owner.
        _despawnScratch.Clear();
        foreach (KeyValuePair<uint, EntityInfo> pair in _entities)
        {
            if (pair.Value.OwnerId == leaverId)
            {
                _despawnScratch.Add(pair.Key);
            }
        }

        for (int i = 0; i < _despawnScratch.Count; i++)
        {
            uint netId = _despawnScratch[i];
            _entities.Remove(netId);
            _replication.TryDespawn(netId, 0u, out _);   // requester 0 = the server, which may despawn anything
        }

        return 0;
    }

    /// <summary>
    /// Hands the host role to the longest-present remaining member (or nobody, if the room is empty) and
    /// announces it.
    /// </summary>
    /// <remarks>
    /// A member inside its resume grace is <b>not</b> a promotion candidate but does not lose the role
    /// either: promotion only runs when the previous host leaves for real. Without host migration a
    /// departing host's pickups vanish and every public "play with friends" session dies when its creator
    /// backgrounds their phone.
    /// </remarks>
    private uint PromoteHost(uint leavingClientId)
    {
        RefreshMemberList();

        uint host = 0u;
        long best = long.MaxValue;
        for (int i = 0; i < _memberCount; i++)
        {
            RoomMember candidate = _memberList[i];
            if (candidate.ClientId == leavingClientId)
            {
                continue;
            }

            if (candidate.JoinSequence < best)
            {
                best = candidate.JoinSequence;
                host = candidate.ClientId;
            }
        }

        uint previous = Interlocked.Exchange(ref _hostClientId, host);
        if (previous == host)
        {
            return host;
        }

        Interlocked.Increment(ref _hostMigrations);
        _logger.LogInformation(
            "Room {RoomId} host migrated from client {PreviousHost} to client {Host}",
            _config.RoomId, previous, host);

        _hostChangedScratch.HostClientId = host;
        _hostChangedScratch.PreviousHostClientId = previous;
        BroadcastControl(MessageTypeIds.HostChangedEvent, _hostChangedScratch);
        return host;
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

    // ── Hot-plane fan-out ─────────────────────────────────────────────────────

    /// <summary>Which writer a hot frame comes from.</summary>
    private enum HotFrameKind
    {
        Snapshot,
        Delta,
        SignalBatch,
    }

    /// <summary>What happened to one attempted hot frame.</summary>
    private enum HotFrameResult
    {
        /// <summary>The writer had nothing to send; no frame existed and nothing was committed.</summary>
        None,

        /// <summary>The frame was accepted by the hot lane and its known-set intent committed.</summary>
        Sent,

        /// <summary>The hot lane was full: the buffer went back, the intent rolled back, resync requested.</summary>
        Overflow,
    }

    /// <summary>
    /// Sends every member the frames it is owed this tick: outstanding snapshot frames <i>or</i> this
    /// tick's delta, then its AOI signal batch.
    /// </summary>
    /// <remarks>
    /// A snapshot and a delta are mutually exclusive by construction — <c>WriteDelta</c> refuses while a
    /// snapshot is pending, because update records are slot-addressed and a delta arriving before the full
    /// records that define those slots would be undecodable.
    /// </remarks>
    private void FanOutState()
    {
        int frameCapacity = _options.MaxFrameBytes;

        for (int i = 0; i < _memberCount; i++)
        {
            RoomMember member = _memberList[i];

            if (!member.Connection.IsOpen)
            {
                // Self-healing: the transport should have called Leave, but a room must never keep a dead
                // member occupying a slot. Disconnected, so the session stays resumable.
                Leave(member.ClientId, LeaveReason.Disconnected);
                continue;
            }

            bool healthy = _replication.IsSnapshotPending(member.ClientId)
                ? WriteSnapshotFrames(member, frameCapacity)
                : SendHotFrame(member, HotFrameKind.Delta, frameCapacity) != HotFrameResult.Overflow;

            if (!healthy)
            {
                // The lane is full; a signal batch would only be refused too (and signals are events, not
                // state, so there is nothing to carry).
                continue;
            }

            SendHotFrame(member, HotFrameKind.SignalBatch, frameCapacity);
        }
    }

    /// <summary>
    /// Emits up to <see cref="RoomServerOptions.MaxSnapshotFramesPerTick"/> snapshot frames. False when
    /// the hot lane overflowed, so the caller stops sending this client anything else this tick.
    /// </summary>
    private bool WriteSnapshotFrames(RoomMember member, int frameCapacity)
    {
        for (int emitted = 0; emitted < _options.MaxSnapshotFramesPerTick; emitted++)
        {
            HotFrameResult result = SendHotFrame(member, HotFrameKind.Snapshot, frameCapacity);
            if (result == HotFrameResult.Overflow)
            {
                return false;
            }

            if (result == HotFrameResult.None || !_replication.IsSnapshotPending(member.ClientId))
            {
                return true;
            }
        }

        return true;
    }

    /// <summary>
    /// Writes one hot frame, ships it, and settles its known-set intent — the two-phase commit in one
    /// place so no caller can get the ordering wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Buffer ownership.</b> The rented buffer is ours until <c>TryEnqueue</c> returns true; every
    /// other path returns it exactly once, right here.
    /// </para>
    /// <para>
    /// <b>Commit ordering.</b> A known-set bit may only be flipped after the frame carrying it was
    /// accepted for sending, so <c>Commit</c> follows a successful enqueue and <c>Rollback</c> follows a
    /// failed one — never the reverse, and never a retry: re-sending would need a second <c>Seq</c>, and
    /// the rolled-back frame is re-derived from current state next tick anyway. A zero-byte write needs no
    /// special case: <c>Commit</c> and <c>Rollback</c> are both no-ops on an empty handle.
    /// </para>
    /// <para>
    /// Exactly one frame per client is ever uncommitted, because this method commits or rolls back before
    /// it returns.
    /// </para>
    /// </remarks>
    private HotFrameResult SendHotFrame(RoomMember member, HotFrameKind kind, int frameCapacity)
    {
        byte[] buffer = FramePool.Rent(frameCapacity);
        PendingKnownSetCommit commit;
        int written;

        if (kind == HotFrameKind.Snapshot)
        {
            written = _replication.WriteSnapshot(member.ClientId, buffer.AsSpan(0, frameCapacity), out commit);
        }
        else if (kind == HotFrameKind.Delta)
        {
            written = _replication.WriteDelta(member.ClientId, buffer.AsSpan(0, frameCapacity), out commit);
        }
        else
        {
            written = _replication.WriteSignalBatch(member.ClientId, buffer.AsSpan(0, frameCapacity), out commit);
        }

        if (written <= 0)
        {
            FramePool.Return(buffer);
            _replication.Rollback(commit);   // no-op on an empty handle
            return HotFrameResult.None;
        }

        if (member.Connection.TryEnqueue(new OutboundFrame(buffer, written), FrameLane.Hot))
        {
            // OWNERSHIP TRANSFER: the send loop returns the buffer once it is on the socket.
            _replication.Commit(commit);
            _bytesOutTotal += written;
            return HotFrameResult.Sent;
        }

        // OWNERSHIP: the enqueue failed, so the buffer is still ours.
        FramePool.Return(buffer);
        _replication.Rollback(commit);

        // A full hot lane means the client read too little, which is recoverable: its known set is cleared
        // and rebuilt by a snapshot rather than the session being closed.
        _replication.RequestResync(member.ClientId);
        _droppedFrames++;
        _resyncs++;
        return HotFrameResult.Overflow;
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

        PublishViolations();

        int players = PlayerCount;
        int entities = _entityCountSnapshot;
        _roomInfoScratch.PlayerCount = players > ushort.MaxValue ? ushort.MaxValue : (ushort)players;
        _roomInfoScratch.EntityCount = entities > ushort.MaxValue ? ushort.MaxValue : (ushort)entities;
        _roomInfoScratch.ServerTick = tick;
        BroadcastControl(MessageTypeIds.RoomInfoEvent, _roomInfoScratch);
    }

    /// <summary>
    /// Publishes each member's merged violation tallies for cross-thread readers, ~1 Hz.
    /// </summary>
    /// <remarks>
    /// <see cref="RoomReplication"/> is single-threaded by contract, so an admin thread may never read it
    /// directly. The tick thread therefore merges Replication's per-client counters with the room's own
    /// quota tally and publishes the record. A snapshot object is allocated only when a number actually
    /// changed, so a room full of well-behaved clients allocates nothing here.
    /// </remarks>
    private void PublishViolations()
    {
        long total = 0;
        for (int i = 0; i < _memberCount; i++)
        {
            RoomMember member = _memberList[i];
            ViolationCounters merged = _replication.SnapshotViolations(member.ClientId) with
            {
                Quota = member.QuotaViolations,
            };

            ViolationsSnapshot? previous = member.Violations;
            if (previous is null || previous.Counters != merged)
            {
                previous = new ViolationsSnapshot(in merged);
                member.PublishViolations(previous);
            }

            total += previous.Total;
        }

        Volatile.Write(ref _violationTotal, total);
    }

    // ── Control-plane fan-out ─────────────────────────────────────────────────

    /// <summary>
    /// Encodes <paramref name="message"/> once and sends a private copy to every member of <b>this</b>
    /// room. Tick-thread only.
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
    /// Sends one control message to one member. Returns false when the client is not a member, its socket
    /// is closed, or its send queue was full. Tick-thread only.
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
        return Enqueue(member, frame.Buffer, frame.Length);
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
    /// Hands a rented control frame to a member's control lane, taking ownership on success and returning
    /// the buffer to the pool (and counting a drop) on failure.
    /// </summary>
    /// <remarks>
    /// A refused control frame means the lane is full, which the transport treats as unrecoverable: it has
    /// already asked the connection to close. There is nothing to retry and nothing to repair — the only
    /// job left here is to give the buffer back exactly once.
    /// </remarks>
    private bool Enqueue(RoomMember member, byte[] buffer, int length)
    {
        if (member.Connection.TryEnqueue(new OutboundFrame(buffer, length), FrameLane.Control))
        {
            _bytesOutTotal += length;
            return true;
        }

        FramePool.Return(buffer);
        _droppedFrames++;
        return false;
    }

    // ── Stats and teardown ────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Safe from any thread. Entity count, server tick and violation totals are values the tick thread
    /// published, not live reads of <see cref="RoomReplication"/> — that instance is single-threaded by
    /// contract.
    /// </remarks>
    public RoomStats SnapshotStats() => new(
        PlayerCount,
        Volatile.Read(ref _entityCountSnapshot),
        Volatile.Read(ref _serverTickSnapshot),
        _tickHistogram.Percentile(0.50),
        _tickHistogram.Percentile(0.99),
        _jitterHistogram.Percentile(0.99),
        Volatile.Read(ref _bytesOutPerSecond),
        Volatile.Read(ref _droppedFrames) + Interlocked.Read(ref _inboundDropped),
        Volatile.Read(ref _budgetOverruns),
        Volatile.Read(ref _resyncs),
        Volatile.Read(ref _violationTotal));

    /// <inheritdoc />
    /// <remarks>
    /// Returns the tally the tick thread last published (≈1 Hz), merged from Replication's per-client
    /// counters and the room's own quota refusals. A client that has fully left is unknown here; one
    /// inside its resume grace still reports, because it has not really left.
    /// </remarks>
    public ViolationCounters SnapshotViolations(uint clientId)
    {
        if (_members.TryGetValue(clientId, out RoomMember? member))
        {
            return member.Violations?.Counters ?? default;
        }

        // Off the tick path and bounded by MaxPlayers, so a linear scan is cheaper than a second index.
        foreach (KeyValuePair<ResumeKey, RoomMember> pair in _pendingResumes)
        {
            if (pair.Value.ClientId == clientId)
            {
                return pair.Value.Violations?.Counters ?? default;
            }
        }

        return default;
    }

    /// <summary>
    /// Closes the inbound queue and returns every buffer still sitting in it, so a destroyed room cannot
    /// leak pooled arrays.
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
