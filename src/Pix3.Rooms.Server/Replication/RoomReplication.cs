using System.Diagnostics;
using System.Runtime.CompilerServices;
using Pix3.Rooms.Protocol;

namespace Pix3.Rooms.Server.Replication;

/// <summary>
/// The replication core for one room: quantized entity table + spatial hash AOI + per-subscriber known
/// sets and <c>Seq</c> + encode-once frame assembly. Single-threaded by contract; depends on
/// <c>Pix3.Rooms.Protocol</c> only, so it is unit-testable with no sockets.
/// </summary>
/// <remarks>
/// <para><b>Tick flow.</b> Client updates accumulate dirty state in the <see cref="EntityTable"/> while
/// the room drains its inbound queue. <see cref="Tick"/> then (1) rolls away any frame the caller left
/// uncommitted, (2) refreshes every bound focus from server-side positions, (3) rebuilds the
/// <see cref="SpatialHashGrid"/> from the packed live slots, (4) serializes every entity in
/// <c>_encodedDirty</c> into one preallocated scratch buffer <i>exactly once</i> (recording per-slot
/// offset/length) and (5) clears the table's dirty state. <c>FullRecord</c>s go into the same scratch
/// lazily, at most once per tick per slot (tick-stamped), the first time any client needs one. The
/// <c>Write*</c> methods assemble per-client frames purely by <c>Span.CopyTo</c> from that scratch — encode
/// once, memcpy many.</para>
/// <para><b>What gets encoded, and why it is not just the dirty set.</b> <c>_encodedDirty</c> is
/// <c>(_tickDirty ∪ every subscriber's PendingUpdates) ∧ Alive</c>, so a record exists in scratch for
/// anything <i>any</i> client still owes. Delivery is then filtered per client: a candidate is skipped
/// unless <c>_tickDirty</c> or <i>that client's own</i> <c>PendingUpdates</c> holds it — one extra bitset
/// probe, and no client ever receives another client's backlog. The naive version (serve every client from
/// the union) amplifies one slow client's debt onto all 600.</para>
/// <para><b>Owed updates are a debt, registered up front.</b> <see cref="Tick"/> owes every non-hidden client
/// the current state of every dirty entity it knows, and only a <see cref="Commit"/> clears a debt — for
/// exactly the records whose bytes shipped. That single rule covers a truncated update section, a tick a
/// send divisor skipped, a change that landed part-way through a split snapshot, and a rolled-back frame,
/// all without any of them needing to be special-cased: an entity that moves once and then stops is
/// re-offered until some frame actually carries it. It is also why <see cref="Rollback"/> has nothing to
/// undo.</para>
/// <para><b>Visibility.</b> Per client the grid fills an inner (enter-radius) and an outer
/// (exit-radius = <c>AoiRadius × AoiExitFactor</c>) set in one pass, then the k-nearest cap trims both.
/// Exits are <c>Known ∧ ¬outer</c> plus generation mismatches; enters are <c>inner ∧ ¬KnownBeforeEnters</c>;
/// updates are <c>KnownBeforeEnters ∧ _encodedDirty</c>. A client therefore never receives a delta for an
/// entity it was not first given a full record for, and an entity that enters AOI while dirty is sent as a
/// full record only.</para>
/// <para><b>Two-phase known-set commit.</b> No <c>Write*</c> method mutates per-client state. Each records
/// intent into pre-sized per-subscriber arrays and hands back a <see cref="PendingKnownSetCommit"/>; the
/// caller applies it with <see cref="Commit"/> after a successful enqueue or discards it with
/// <see cref="Rollback"/>. <c>Seq</c> is stamped from the peek value and advanced only by a commit, so a
/// frame that never shipped leaves no gap. See <c>RoomReplication.Frames.cs</c>.</para>
/// </remarks>
public sealed partial class RoomReplication : IRoomReplication
{
    /// <summary>
    /// Mask used when re-sending an update a client was owed: every value field, absolute, no signal bits.
    /// </summary>
    /// <remarks>
    /// A carried record must fully re-state the entity, because the original masked record is long gone and
    /// the client's copy could be stale in any field. Signal bits are deliberately excluded — they are
    /// events (<c>ColdDirty</c> promises a control-plane follow-up, <c>Teleport</c> announces a
    /// discontinuity), and replaying an event a tick later is wrong. 13 bytes in a path that only runs
    /// after a truncation is a good trade for never stranding a stopped entity.
    /// </remarks>
    private const byte CarryRefreshMask = DeltaMask.PayloadBits;

    /// <summary>
    /// Ceiling on how many ticks of movement allowance a single update may bank. Without it an entity that
    /// sat idle for a minute would be allowed to teleport across the world "plausibly".
    /// </summary>
    private const uint MaxSpeedCheckElapsedTicks = 64;

    private readonly ReplicationOptions _options;
    private readonly WorldQuantizer _quantizer;
    private readonly EntityTable _table;
    private readonly SpatialHashGrid _grid;
    private readonly NearestSlotSelector _nearest;
    private readonly AoiSignalBuffer _signals;

    private readonly float _aoiEnterRadius;
    private readonly float _aoiExitRadius;
    private readonly int _byteBudget;
    private readonly int _maxEntersPerTick;
    private readonly int _maxVisibleEntities;
    private readonly float _focusMaxStepPerTick;
    private readonly float _plausibleMovePerTick;

    // Encode-once scratch: update records first (eagerly, in Tick), full records appended lazily.
    private readonly byte[] _scratch;
    private int _scratchCursor;

    private readonly int[] _updateOffset;   // valid only for slots set in _encodedDirty
    private readonly byte[] _updateSize;
    private readonly int[] _fullOffset;     // valid only when _fullStamp[slot] == _tickSeq
    private readonly ulong[] _fullStamp;

    /// <summary>Slots the entity table reported dirty for THIS tick — the only thing a client may be newly owed.</summary>
    private readonly SlotBitset _tickDirty;

    /// <summary>Union of every subscriber's owed-update set, used to widen carried records.</summary>
    private readonly SlotBitset _pendingUnion;

    /// <summary>Slots with an update record in scratch this tick: <c>(_tickDirty ∪ _pendingUnion) ∧ Alive</c>.</summary>
    private readonly SlotBitset _encodedDirty;

    /// <summary>Scratch for the k-nearest selection result; shared by all clients (single-threaded).</summary>
    private readonly SlotBitset _capScratch;

    /// <summary>Per-slot tick of the last speed check, so Δt is real elapsed time rather than a guess.</summary>
    private readonly uint[] _speedRefTick;

    private readonly Dictionary<uint, SubscriberState> _subscribers;
    private readonly Stack<SubscriberState> _subscriberPool;
    private readonly int _maxRemovalsPerFrame;
    private readonly int _maxEntersPerFrame;
    private readonly int _maxUpdatesPerFrame;

    private ulong _tickSeq;      // internal monotonic tick counter; 0 = Tick never ran
    private uint _serverTick;    // room-supplied tick echoed into frames
    private uint _nextPendingToken = 1;   // 0 is reserved for "empty commit handle"
    private bool _signalBatchSealed;      // set by Tick: the next queued signal starts a new batch

    /// <summary>Creates a replication core with all storage allocated up front.</summary>
    public RoomReplication(ReplicationOptions options)
    {
        options.Validate();
        _options = options;
        _quantizer = options.CreateQuantizer();
        _table = new EntityTable(options.MaxEntities, _quantizer);
        _grid = new SpatialHashGrid(options.MaxEntities, options.EffectiveCellSize);
        _nearest = new NearestSlotSelector(Math.Min(options.MaxVisibleEntities, options.MaxEntities));
        _signals = new AoiSignalBuffer();

        _aoiEnterRadius = options.AoiRadius;
        _aoiExitRadius = options.EffectiveExitRadius;
        _byteBudget = options.MaxBytesPerClientPerTick;
        _maxEntersPerTick = options.MaxEntersPerTick;
        _maxVisibleEntities = options.MaxVisibleEntities;
        _focusMaxStepPerTick = options.MaxSpectatorFocusSpeed / options.TickHz;

        // |Δpos| <= maxSpeed × Δt × 1.25, pre-divided into "per elapsed tick" so the check itself is two
        // multiplies and a compare.
        _plausibleMovePerTick = options.MaxEntitySpeed / options.TickHz * 1.25f;

        _scratch = new byte[options.MaxEntities * (HotWire.MaxUpdateRecordSize + HotWire.FullRecordSize)];
        _updateOffset = new int[options.MaxEntities];
        _updateSize = new byte[options.MaxEntities];
        _fullOffset = new int[options.MaxEntities];
        _fullStamp = new ulong[options.MaxEntities];
        _tickDirty = new SlotBitset(options.MaxEntities);
        _pendingUnion = new SlotBitset(options.MaxEntities);
        _encodedDirty = new SlotBitset(options.MaxEntities);
        _capScratch = new SlotBitset(options.MaxEntities);
        _speedRefTick = new uint[options.MaxEntities];

        // Per-frame section ceilings follow from the byte budget: a section can never record more entries
        // than the frame could physically carry, so running out of an array is exactly running out of bytes.
        int deltaPayload = Math.Max(_byteBudget - HotWire.DeltaPacketFixedOverhead, 0);
        _maxRemovalsPerFrame = Math.Max(deltaPayload / HotWire.RemovedSlotSize, 1);
        _maxUpdatesPerFrame = Math.Max(deltaPayload / HotWire.MinUpdateRecordSize, 1);

        // Enters: a DeltaPacket is capped by MaxEntersPerTick, but a SnapshotPacket is capped by bytes
        // alone (a resync is meant to be one or two frames, not a 24-per-tick trickle), so the intent array
        // has to be sized for the larger of the two.
        int snapshotRecordCapacity =
            Math.Max((_byteBudget - HotWire.SnapshotPacketHeaderSize) / HotWire.FullRecordSize, 1);
        _maxEntersPerFrame = Math.Max(_maxEntersPerTick, snapshotRecordCapacity);

        _subscribers = new Dictionary<uint, SubscriberState>(options.MaxPlayers);
        _subscriberPool = new Stack<SubscriberState>();
    }

    // ── Introspection ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public int EntityCount => _table.LiveCount;

    /// <summary>The entity table, exposed for tests and room-level introspection.</summary>
    public EntityTable Table => _table;

    /// <summary>This room's world bounds — the only thing that maps quantized state to coordinates.</summary>
    public WorldQuantizer Quantizer => _quantizer;

    /// <summary>Currently tracked subscribers.</summary>
    public int SubscriberCount => _subscribers.Count;

    /// <summary>Tick value passed to the most recent <see cref="Tick"/>.</summary>
    public uint LastServerTick => _serverTick;

    /// <summary>
    /// True while this client still owes a full snapshot, i.e. <see cref="WriteSnapshot"/> can make progress
    /// and <see cref="WriteDelta"/> will refuse. The continuation cursor itself is private state — a resync
    /// has to be able to restart it — so this is the one thing a caller needs to drive the snapshot loop.
    /// </summary>
    public bool IsSnapshotPending(uint clientId)
        => _subscribers.TryGetValue(clientId, out SubscriberState? sub) && sub.SnapshotPending;

    /// <summary>The <c>Seq</c> the client's next emitted frame will carry. Diagnostics and tests.</summary>
    public ushort PeekSeq(uint clientId)
        => _subscribers.TryGetValue(clientId, out SubscriberState? sub) ? sub.NextSeq : (ushort)0;

    /// <summary>AOI enter radius in world units.</summary>
    public float AoiEnterRadius => _aoiEnterRadius;

    /// <summary>AOI exit radius in world units (<c>AoiRadius × AoiExitFactor</c>).</summary>
    public float AoiExitRadius => _aoiExitRadius;

    // ── Per-tick diagnostics ────────────────────────────────────────────────────

    /// <summary>Update records encoded into scratch by the last <see cref="Tick"/>.</summary>
    public int LastTickDirtyCount { get; private set; }

    /// <summary>Of those, records widened because some client still owed them.</summary>
    public int LastTickCarriedUpdateCount { get; private set; }

    /// <summary>Bytes of update records encoded into scratch by the last <see cref="Tick"/>.</summary>
    public int LastTickUpdateScratchBytes { get; private set; }

    /// <summary>Full records lazily encoded into scratch since the last <see cref="Tick"/>.</summary>
    public int LastTickFullRecordsEncoded { get; private set; }

    /// <summary>Frame bytes returned by all writers since the last <see cref="Tick"/>.</summary>
    public long LastTickBytesWritten { get; private set; }

    /// <summary>Non-empty frames produced since the last <see cref="Tick"/>.</summary>
    public int LastTickFramesWritten { get; private set; }

    /// <summary>Σ inner-visible entities over this tick's visibility computations.</summary>
    public long LastTickVisibleSum { get; private set; }

    /// <summary>Visibility computations (grid queries) since the last <see cref="Tick"/>: one per served client.</summary>
    public int LastTickVisibilityQueries { get; private set; }

    /// <summary><see cref="WriteDelta"/> calls that produced a frame since the last <see cref="Tick"/>.</summary>
    public int LastTickDeltaCalls { get; private set; }

    /// <summary>AOI signal entries queued for the current tick.</summary>
    public int LastTickSignalEntries => _signals.Count;

    // ── Lifetime diagnostics ────────────────────────────────────────────────────

    /// <summary>Total frame bytes returned over the room's lifetime.</summary>
    public long TotalBytesWritten { get; private set; }

    /// <summary>Total non-empty frames over the room's lifetime.</summary>
    public long TotalFramesWritten { get; private set; }

    /// <summary>Client updates rejected for carrying server-only mask bits.</summary>
    public long IllegalMaskCount { get; private set; }

    /// <summary>Client requests naming an entity they do not own.</summary>
    public long OwnershipViolationCount { get; private set; }

    /// <summary>Client requests naming an unknown/stale entity id (counts as a <c>mask</c> violation).</summary>
    public long UnknownEntityCount { get; private set; }

    /// <summary>Client updates whose effective mask was empty — an identical re-send, replicated to nobody.</summary>
    public long NoOpUpdateCount { get; private set; }

    /// <summary>Moves that failed the Level-1 plausibility check. Counted, never enforced.</summary>
    public long SpeedViolationCount { get; private set; }

    /// <summary>Client-set teleport bits.</summary>
    public long TeleportBitCount { get; private set; }

    /// <summary>Non-finite spectator focus coordinates refused.</summary>
    public long NanFocusCount { get; private set; }

    /// <summary>Spectator focus moves that hit the speed clamp.</summary>
    public long FocusClampCount { get; private set; }

    /// <summary>Kind-allowlist rejections attributed by Rooms through <see cref="CountKindViolation"/>.</summary>
    public long KindViolationCount { get; private set; }

    /// <summary><see cref="AddSubscriber"/> calls beyond <see cref="ReplicationOptions.MaxPlayers"/> (room-side bug indicator).</summary>
    public long SubscriberOverflowCount { get; private set; }

    /// <summary>Slots permanently retired after exhausting their generation space.</summary>
    public int RetiredSlotCount => _table.RetiredSlotCount;

    /// <summary>Known sets cleared and snapshots restarted (queue overflow, un-hide, reconnect).</summary>
    public long ResyncCount { get; private set; }

    /// <summary>Ticks×clients where the k-nearest cap actually trimmed a visibility set.</summary>
    public long CappedVisibilityCount { get; private set; }

    /// <summary>Delta frames whose enter section stopped early and left a carry cursor behind.</summary>
    public long EnterCarryCount { get; private set; }

    /// <summary>Delta frames whose removal section was cut short by the byte budget.</summary>
    public long TruncatedRemovalSectionCount { get; private set; }

    /// <summary>Snapshot frames that did not reach the end of the live list.</summary>
    public long SplitSnapshotFrameCount { get; private set; }

    /// <summary>Delta frames whose update section was cut short, leaving owed updates behind.</summary>
    public long TruncatedUpdateSectionCount { get; private set; }

    /// <summary>Signal batches cut short by the byte budget. Signals are events and are not carried.</summary>
    public long TruncatedSignalBatchCount { get; private set; }

    /// <summary>Frames whose intent was applied.</summary>
    public long CommitCount { get; private set; }

    /// <summary>Frames whose intent was discarded because the send queue refused them.</summary>
    public long RollbackCount { get; private set; }

    /// <summary>Commit/rollback handles refused as duplicate or stale. Should stay at 0.</summary>
    public long StaleCommitCount { get; private set; }

    /// <summary>Commit/rollback handles for a client that has since left. Benign.</summary>
    public long OrphanCommitCount { get; private set; }

    /// <summary>Frames still uncommitted when the next <see cref="Tick"/> began. A caller bug; should stay at 0.</summary>
    public long AbandonedFrameCount { get; private set; }

    /// <summary>Writes refused because that client already had an uncommitted frame. A caller bug.</summary>
    public long ConcurrentFrameRefusedCount { get; private set; }

    /// <summary>Hot frames suppressed because the client is hidden.</summary>
    public long HiddenSuppressedFrameCount { get; private set; }

    /// <summary>Hot frames skipped because the client's send divisor excludes this tick.</summary>
    public long DivisorSkippedFrameCount { get; private set; }

    /// <summary>AOI signals accepted into the per-tick batch.</summary>
    public long AoiSignalsQueuedCount { get; private set; }

    /// <summary>AOI signals refused because the sender has no bound focus entity to scope them against.</summary>
    public long AoiSignalsRefusedNoFocusCount { get; private set; }

    /// <summary>AOI signals refused because the tick's batch was full or the signal was too large.</summary>
    public long AoiSignalsRefusedCapacityCount { get; private set; }

    /// <summary>Signal entries copied into recipient batches (fan-out volume, not encode volume).</summary>
    public long AoiSignalEntriesCopiedCount { get; private set; }

    /// <summary>Entities moved to a new owner by host migration.</summary>
    public long ReassignedEntityCount { get; private set; }

    /// <summary>Entities a departing owner left behind because their policy is not <c>Owned</c>.</summary>
    public long PolicyPreservedEntityCount { get; private set; }

    // ── Entity mutation ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public bool TrySpawn(uint ownerId, ushort kind, in EntityWireState state, out uint netId, out RejectCode reject)
    {
        uint tick = NextTickStamp();
        if (!_table.TrySpawn(ownerId, kind, state, tick, out netId))
        {
            reject = RejectCode.EntityLimitReached;
            return false;
        }

        // Start the speed-check clock at the spawn tick so the first update is measured against a real
        // interval rather than against whatever the previous occupant of this slot left behind.
        _speedRefTick[NetId.Slot(netId)] = tick;

        // No dirty-marking here: the new entity reaches every interested client through the AOI enter path
        // as a FullRecord, never as a delta.
        reject = RejectCode.None;
        return true;
    }

    /// <inheritdoc />
    public bool TryDespawn(uint netId, uint requesterId, out RejectCode reject)
    {
        if (!_table.TryResolve(netId, out int slot))
        {
            UnknownEntityCount++;
            CountMaskViolation(requesterId);
            reject = RejectCode.BadRequest;
            return false;
        }

        // Server (requester 0) may despawn anything; a client only its own.
        if (requesterId != 0 && _table.OwnerId[slot] != requesterId)
        {
            OwnershipViolationCount++;
            CountOwnershipViolation(requesterId);
            reject = RejectCode.NotEntityOwner;
            return false;
        }

        _table.Despawn(slot);
        reject = RejectCode.None;
        return true;
    }

    /// <inheritdoc />
    public bool TryApplyOwnedUpdate(uint netId, uint ownerId, byte mask, in EntityWireState state)
    {
        if (!HotWire.IsClientMaskLegal(mask))
        {
            IllegalMaskCount++;
            CountMaskViolation(ownerId);
            return false;
        }

        if (!_table.TryResolve(netId, out int slot))
        {
            // A netId whose generation no longer matches its slot is a record-integrity violation per the
            // protocol's validation table, even though stale ids are also ordinary in-flight traffic.
            UnknownEntityCount++;
            CountMaskViolation(ownerId);
            return false;
        }

        if (ownerId != 0 && _table.OwnerId[slot] != ownerId)
        {
            OwnershipViolationCount++;
            CountOwnershipViolation(ownerId);
            return false;
        }

        if ((mask & DeltaMask.Teleport) != 0)
        {
            // Legitimate under client authority (respawns), so counted rather than refused. Stripped at L2.
            TeleportBitCount++;
            if (_subscribers.TryGetValue(ownerId, out SubscriberState? teleportSub))
            {
                teleportSub.ViolationTeleport++;
            }
        }

        uint tick = NextTickStamp();

        if ((mask & (DeltaMask.X | DeltaMask.Y)) != 0)
        {
            ushort candidateQX = (mask & DeltaMask.X) != 0 ? state.QX : _table.QX[slot];
            ushort candidateQY = (mask & DeltaMask.Y) != 0 ? state.QY : _table.QY[slot];
            if (!IsMovePlausible(slot, candidateQX, candidateQY, tick))
            {
                // COUNTED, NEVER ENFORCED at Level 1: the update is applied regardless. This call is the
                // Level-2 validator written early behind the same seam — when authority moves server-side,
                // the only change here is to return false instead.
                SpeedViolationCount++;
                if (_subscribers.TryGetValue(ownerId, out SubscriberState? speedSub))
                {
                    speedSub.ViolationSpeed++;
                }
            }
        }

        // Masked merge against the stored QUANTIZED values: the effective mask is the subset that actually
        // changed, so an identical re-send marks nothing dirty and costs the room nothing.
        byte effective = _table.ApplyUpdate(slot, mask, state, tick);
        if (effective == DeltaMask.None)
        {
            NoOpUpdateCount++;
        }

        return true;
    }

    /// <summary>
    /// The Level-1 speed check: <c>|Δpos| ≤ MaxEntitySpeed × Δt × 1.25</c>, computed on dequantized
    /// positions and compared squared (no square root). Returns whether the move was plausible; the caller
    /// counts implausible ones and applies the update anyway.
    /// </summary>
    /// <remarks>
    /// Δt is real elapsed time since this slot's last check, so a client sending at half rate is not
    /// punished for it. Several records in the <i>same</i> tick share one tick of allowance — at 20 Hz that
    /// is the honest reading of "how far could this have moved since I last looked", and it stops a burst of
    /// eight records in one packet from reading as eight teleports.
    /// </remarks>
    private bool IsMovePlausible(int slot, ushort candidateQX, ushort candidateQY, uint tick)
    {
        uint reference = _speedRefTick[slot];
        uint elapsed = tick > reference ? tick - reference : 1u;
        if (elapsed > MaxSpeedCheckElapsedTicks)
        {
            elapsed = MaxSpeedCheckElapsedTicks;
        }

        _speedRefTick[slot] = tick;

        float dx = _quantizer.DequantizeX(candidateQX) - _table.X[slot];
        float dy = _quantizer.DequantizeY(candidateQY) - _table.Y[slot];
        float allowed = elapsed * _plausibleMovePerTick;
        return (dx * dx) + (dy * dy) <= allowed * allowed;
    }

    /// <summary>
    /// Marks an entity's cold props as changed (<see cref="DeltaMask.ColdDirty"/>), promising a follow-up
    /// <c>EntityPropsChangedEvent</c> on the control plane. Server-authored — the room calls this after
    /// validating a cold-props change; ownership is not re-checked here. False when the id is unknown or
    /// stale. (Additive helper, not part of <see cref="IRoomReplication"/>.)
    /// </summary>
    public bool TryMarkColdDirty(uint netId)
    {
        if (!_table.TryResolve(netId, out int slot))
        {
            return false;
        }

        _table.MarkDirty(slot, DeltaMask.ColdDirty, NextTickStamp());
        return true;
    }

    /// <inheritdoc />
    public void RemoveOwner(uint ownerId, List<uint> despawned)
    {
        // Walk the owner's intrusive chain; Despawn unlinks as we go, so grab next first.
        int slot = _table.GetOwnerHead(ownerId);
        while (slot != -1)
        {
            int next = _table.GetOwnerNext(slot);

            // Policy-aware: only `Owned` (and the reserved encoding, which is treated as Owned) dies with
            // its owner. Without this a departing host's pickups, spawners and world props vanish with it.
            OwnershipPolicy policy = _table.PolicyOf(slot);
            if (policy is OwnershipPolicy.Shared or OwnershipPolicy.Transferable)
            {
                PolicyPreservedEntityCount++;
                slot = next;
                continue;
            }

            despawned.Add(_table.PackId(slot));
            _table.Despawn(slot);
            slot = next;
        }
        // No per-subscriber cleanup needed: dead slots leave every client's outer-visible set, so the Known
        // diff emits the removals even for clients whose frame is assembled long after the despawn.
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>How observers learn about the new owner.</b> <c>OwnerId</c> travels only in a <c>FullRecord</c>,
    /// so an update record cannot carry it. This implementation therefore forces a <b>remove-then-re-enter</b>
    /// for every client that knows the entity: each knower's <c>KnownGeneration</c> is set to 0, which can
    /// never match a live generation (generations start at 1), so the next delta emits a removal for the slot
    /// and — if the entity is still inside that client's enter radius — a fresh <c>FullRecord</c> carrying
    /// the new owner in the same frame. Removals precede enters within a frame, so the sequence is
    /// unambiguous on an ordered stream.
    /// </para>
    /// <para>
    /// The alternative — re-sending a <c>FullRecord</c> without a removal — is 2 bytes cheaper but leaves a
    /// client that currently sees the entity only in the hysteresis band (known, but outside the enter
    /// radius) holding a stale owner indefinitely, because no enter would ever be generated for it. A stale
    /// owner means that client would accept, or refuse, authority from the wrong peer. 22 bytes and one
    /// re-enter is the honest price.
    /// </para>
    /// </remarks>
    public void ReassignOwner(uint fromOwnerId, uint toOwnerId, List<uint> reassigned)
    {
        if (fromOwnerId == toOwnerId)
        {
            return;
        }

        uint tick = NextTickStamp();
        int slot = _table.GetOwnerHead(fromOwnerId);
        while (slot != -1)
        {
            // SetOwner re-threads the chains, so the successor must be captured first.
            int next = _table.GetOwnerNext(slot);

            OwnershipPolicy policy = _table.PolicyOf(slot);
            if (policy is OwnershipPolicy.Shared or OwnershipPolicy.Transferable)
            {
                _table.SetOwner(slot, toOwnerId);

                // The flags byte carries the ownership policy, so mark the entity dirty in the flags sense:
                // clients that keep the entity through the re-enter get a consistent view either way, and
                // the dirty bit keeps the change visible to any future observer path.
                _table.MarkDirty(slot, DeltaMask.Flags, tick);
                ForceKnownRefresh(slot);
                reassigned.Add(_table.PackId(slot));
                ReassignedEntityCount++;
            }

            slot = next;
        }
    }

    /// <summary>
    /// Marks a slot as needing a fresh <c>FullRecord</c> for every client that knows it, by invalidating the
    /// remembered generation. Known-bit plus generation 0 is "known, but must be dropped and re-introduced";
    /// generation 0 is safe as a sentinel because live generations start at 1.
    /// </summary>
    private void ForceKnownRefresh(int slot)
    {
        foreach (SubscriberState sub in _subscribers.Values)
        {
            if (sub.Known.Get(slot))
            {
                sub.KnownGeneration[slot] = 0;
            }
        }
    }

    // ── Subscribers ─────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void AddSubscriber(uint clientId)
    {
        if (_subscribers.TryGetValue(clientId, out SubscriberState? existing))
        {
            existing.Reset(clientId);       // rejoin: forget everything it knew
            existing.SnapshotPending = true;
            return;
        }

        if (_subscribers.Count >= _options.MaxPlayers)
        {
            // The room enforces MaxPlayers before joining; hitting this means a room-side bug. Count it
            // loudly rather than growing past the sized pool.
            SubscriberOverflowCount++;
            return;
        }

        SubscriberState sub = _subscriberPool.Count > 0
            ? _subscriberPool.Pop()
            : new SubscriberState(_options.MaxEntities, _maxRemovalsPerFrame, _maxEntersPerFrame, _maxUpdatesPerFrame);
        sub.Reset(clientId);
        sub.SnapshotPending = true;         // a joiner's first hot frame is always a full snapshot
        _subscribers.Add(clientId, sub);
    }

    /// <inheritdoc />
    public void RemoveSubscriber(uint clientId)
    {
        if (_subscribers.Remove(clientId, out SubscriberState? sub))
        {
            _subscriberPool.Push(sub);      // state is wiped on next checkout, not here
        }
    }

    /// <inheritdoc />
    public void BindSubscriberFocus(uint clientId, uint netId)
    {
        if (!_subscribers.TryGetValue(clientId, out SubscriberState? sub))
        {
            return;
        }

        sub.FocusNetId = netId;
        sub.FocusIsFree = false;

        // Resolve immediately so a client that binds mid-tick is served from the right centre this tick;
        // Tick refreshes it every tick thereafter.
        if (_table.TryResolve(netId, out int slot))
        {
            sub.FocusX = _table.X[slot];
            sub.FocusY = _table.Y[slot];
            sub.HasFocus = true;
        }

        sub.VisibilityStamp = 0;            // focus moved: any cached visibility is stale
    }

    /// <inheritdoc />
    public void SetSpectatorFocus(uint clientId, float x, float y)
    {
        if (!_subscribers.TryGetValue(clientId, out SubscriberState? sub))
        {
            return;
        }

        // One NaN poisons the spatial hash. After quantization these are the only inbound floats left on
        // the entity path, so this is where the finiteness check lives.
        if (!float.IsFinite(x) || !float.IsFinite(y))
        {
            NanFocusCount++;
            sub.ViolationNan++;
            return;
        }

        sub.FocusIsFree = true;
        sub.FocusNetId = NetId.None;

        if (!sub.HasFocus)
        {
            // First focus ever: accept verbatim. Clamping against the (0, 0) initial value would strand a
            // joining spectator near the world origin, and a joiner gets a full snapshot anyway.
            sub.FocusX = x;
            sub.FocusY = y;
            sub.HasFocus = true;
        }
        else
        {
            // Clamp the MOVEMENT, not the position: free-position focus is what made "teleport my focus
            // every tick to force enormous enter sets" work. Double intermediates so a 1e38 coordinate
            // cannot overflow the squared distance into an infinity (and then a NaN direction).
            double dx = (double)x - sub.FocusX;
            double dy = (double)y - sub.FocusY;
            double distanceSquared = (dx * dx) + (dy * dy);
            double maxStep = _focusMaxStepPerTick;
            if (distanceSquared > maxStep * maxStep)
            {
                double scale = maxStep / Math.Sqrt(distanceSquared);
                sub.FocusX = (float)(sub.FocusX + (dx * scale));
                sub.FocusY = (float)(sub.FocusY + (dy * scale));
                FocusClampCount++;
                sub.ViolationFocusClamp++;
            }
            else
            {
                sub.FocusX = x;
                sub.FocusY = y;
            }
        }

        sub.VisibilityStamp = 0;
    }

    /// <inheritdoc />
    public void RequestResync(uint clientId)
    {
        if (!_subscribers.TryGetValue(clientId, out SubscriberState? sub))
        {
            return;
        }

        // Safe to call while a frame is uncommitted: that frame's intent is rolled away first, so its
        // enters and removals are never applied to a known set we are about to clear anyway.
        if (sub.HasPendingFrame)
        {
            ApplyRollback(sub);
        }

        sub.Known.Clear();
        Array.Clear(sub.KnownGeneration, 0, sub.KnownGeneration.Length);
        sub.PendingUpdates.Clear();         // a full snapshot supersedes every owed update
        sub.EnterCursor = 0;
        sub.SnapshotCursor = 0;
        sub.SnapshotPending = true;
        ResyncCount++;
    }

    /// <inheritdoc />
    public void SetSubscriberHidden(uint clientId, bool hidden)
    {
        if (!_subscribers.TryGetValue(clientId, out SubscriberState? sub) || sub.Hidden == hidden)
        {
            return;                         // guard on change: a redundant "un-hide" must not force a resync
        }

        sub.Hidden = hidden;
        if (hidden)
        {
            // Drop the owed-update debts now. They are worthless — un-hiding resyncs — and letting a hidden
            // client's debts accumulate would widen the shared update records of every other client in the
            // room for as long as the tab stays backgrounded.
            sub.PendingUpdates.Clear();
        }
        else
        {
            // While hidden the client received nothing and its Seq stood still, so its known set is a
            // fiction by now. Un-hiding is a resync by definition.
            RequestResync(clientId);
        }
    }

    /// <inheritdoc />
    public void SetSubscriberSendDivisor(uint clientId, byte divisor)
    {
        if (!_subscribers.TryGetValue(clientId, out SubscriberState? sub))
        {
            return;
        }

        // 0 and 1 both mean "every tick" on the wire; the internal representation is always 1..8.
        sub.SendDivisor = divisor <= 1 ? (byte)1 : Math.Min(divisor, (byte)8);
    }

    /// <summary>
    /// Attributes a kind-allowlist rejection to a client. The allowlist is <c>RoomConfig</c> data, so Rooms
    /// makes the decision, but the per-client tally lives here with the rest of them.
    /// </summary>
    public void CountKindViolation(uint clientId)
    {
        KindViolationCount++;
        if (_subscribers.TryGetValue(clientId, out SubscriberState? sub))
        {
            sub.ViolationKind++;
        }
    }

    /// <inheritdoc />
    public ViolationCounters SnapshotViolations(uint clientId)
        => _subscribers.TryGetValue(clientId, out SubscriberState? sub) ? sub.SnapshotViolations() : default;

    // ── AOI signals ─────────────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Queue during the room's inbound drain, i.e. <i>before</i> <see cref="Tick"/>: the first queue call
    /// after a <see cref="Tick"/> starts a fresh batch, so entries queued before the tick are exactly the
    /// ones that tick's <see cref="WriteSignalBatch"/> calls deliver.
    /// </remarks>
    public bool TryQueueAoiSignal(uint senderClientId, ReadOnlySpan<byte> name, ReadOnlySpan<byte> payload)
    {
        if (_signalBatchSealed)
        {
            _signals.Clear();
            _signalBatchSealed = false;
        }

        // A sender with no bound focus entity cannot be scoped to an AOI at all. Falling back to "send to
        // everyone" here would turn one signal into a 600× amplifier, so it is refused and counted.
        if (!_subscribers.TryGetValue(senderClientId, out SubscriberState? sub)
            || sub.FocusIsFree
            || sub.FocusNetId == NetId.None
            || !_table.TryResolve(sub.FocusNetId, out int senderSlot))
        {
            AoiSignalsRefusedNoFocusCount++;
            return false;
        }

        if (!_signals.TryAdd(senderClientId, senderSlot, name, payload))
        {
            AoiSignalsRefusedCapacityCount++;
            return false;
        }

        AoiSignalsQueuedCount++;
        return true;
    }

    // ── Tick: focus refresh, grid rebuild, encode-once scratch ──────────────────

    /// <inheritdoc />
    public void Tick(uint serverTick)
    {
        _tickSeq++;
        _serverTick = serverTick;

        // (1) Any frame left uncommitted by the previous tick is a caller bug: roll it away, which by
        //     construction restores nothing (a rolled-back frame applied nothing in the first place). Folded
        //     into the same pass that unions the *existing* owed-update sets — existing, because a debt
        //     incurred by this tick must not widen this tick's own records.
        _pendingUnion.Clear();
        foreach (SubscriberState sub in _subscribers.Values)
        {
            if (sub.HasPendingFrame)
            {
                Debug.Assert(false, "a frame was written but never committed or rolled back");
                AbandonedFrameCount++;
                ApplyRollback(sub);
            }

            _pendingUnion.UnionWith(sub.PendingUpdates);
        }

        // (2) Bound focuses come from server state, refreshed every tick. This is what deletes
        //     focus-teleport amplification at its source: a client cannot claim a position at all.
        RefreshBoundFocuses();

        // (3) Spatial index over the derived float positions.
        _grid.Build(_table);

        _scratchCursor = 0;
        LastTickFullRecordsEncoded = 0;
        LastTickBytesWritten = 0;
        LastTickFramesWritten = 0;
        LastTickVisibleSum = 0;
        LastTickVisibilityQueries = 0;
        LastTickDeltaCalls = 0;

        // (4) Encode-once. _encodedDirty covers what any client still owes, not just what changed this
        //     tick, so a truncated update section can be re-served from CURRENT state later. Dead slots are
        //     excluded: an owed update for an entity that has since despawned is settled by its removal.
        _tickDirty.CopyFrom(_table.Dirty);

        // (4a) Provisionally owe every client the current state of every dirty entity it knows. Commit clears
        //      the debt for records that actually ship, so anything not delivered stays owed — which is what
        //      makes a truncated update section, a tick skipped by a send divisor, and a change that happened
        //      part-way through a split snapshot all heal the same way, instead of stranding an entity that
        //      moved once and then stopped. Hidden clients are excluded: they resync on un-hide, so
        //      accumulating debts for them would only widen everyone else's records for nothing.
        //      Registered AFTER _pendingUnion was taken, so a same-tick debt does not widen the very record
        //      that is about to satisfy it.
        foreach (SubscriberState sub in _subscribers.Values)
        {
            if (!sub.Hidden)
            {
                sub.PendingUpdates.UnionWithIntersection(sub.Known, _tickDirty, _table.Alive);
            }
        }

        _encodedDirty.CopyFrom(_tickDirty);
        _encodedDirty.UnionWith(_pendingUnion);
        _encodedDirty.IntersectWith(_table.Alive);

        int records = 0;
        int bytes = 0;
        int carried = 0;
        foreach (int slot in _encodedDirty.EnumerateSetBits())
        {
            Debug.Assert(_table.IsAlive(slot), "_encodedDirty is intersected with Alive");

            byte mask = _table.DirtyMask[slot];
            if (_pendingUnion.Get(slot))
            {
                // Somebody is owed this entity: widen the shared record so it fully re-states the entity.
                // One record serves every recipient, so a debtor widens the record for everyone — a few
                // bytes, versus encoding a second record per slot.
                mask |= CarryRefreshMask;
                carried++;
            }

            _table.FillWireState(slot, out EntityWireState state);
            int size = HotWire.WriteUpdateRecord(_scratch.AsSpan(_scratchCursor), (ushort)slot, mask, state);
            Debug.Assert(size > 0, "scratch is sized to always fit");
            _updateOffset[slot] = _scratchCursor;
            _updateSize[slot] = (byte)size;
            _scratchCursor += size;
            records++;
            bytes += size;
        }

        _table.ClearDirty();
        LastTickDirtyCount = records;
        LastTickUpdateScratchBytes = bytes;
        LastTickCarriedUpdateCount = carried;

        // (5) The batch queued during this tick's drain is now the one being delivered; the next queued
        //     signal belongs to the next tick.
        _signalBatchSealed = true;
    }

    /// <summary>
    /// Re-resolves every bound focus from its entity's server-side dequantized position. A focus whose
    /// entity is dead or unresolvable keeps its last known value: snapping to the origin would teleport a
    /// dying client's AOI across the map and hand it a full enter set for a region it never asked about.
    /// </summary>
    private void RefreshBoundFocuses()
    {
        foreach (SubscriberState sub in _subscribers.Values)
        {
            if (sub.FocusIsFree || sub.FocusNetId == NetId.None)
            {
                continue;
            }

            if (_table.TryResolve(sub.FocusNetId, out int slot))
            {
                sub.FocusX = _table.X[slot];
                sub.FocusY = _table.Y[slot];
                sub.HasFocus = true;
            }
        }
    }

    /// <summary>
    /// Fills this client's inner/outer visibility sets, at most once per tick, and applies the k-nearest
    /// cap. Cached because all three writers need it and a grid query is the most expensive per-client step.
    /// </summary>
    private void EnsureVisibility(SubscriberState sub)
    {
        if (sub.VisibilityStamp == _tickSeq)
        {
            return;
        }

        _grid.QueryRadiusWithHysteresis(
            sub.FocusX, sub.FocusY, _aoiEnterRadius, _aoiExitRadius, sub.VisibleInner, sub.VisibleOuter);
        ApplyVisibilityCap(sub);
        sub.VisibilityStamp = _tickSeq;

        LastTickVisibilityQueries++;
        LastTickVisibleSum += sub.VisibleInner.Count();
    }

    /// <summary>
    /// Trims a client's visibility to the <c>MaxVisibleEntities</c> nearest entities by squared distance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The common case costs one popcount.</b> When the candidate count is within the cap the method
    /// returns immediately — no distances, no selection, nothing. This runs per client per tick, so that
    /// matters more than the dogpile path being pretty.
    /// </para>
    /// <para>
    /// <b>The cap is applied to the OUTER set, and that is deliberate.</b> Capping only the enter set would
    /// bound enters but not retention: exits are <c>Known ∧ ¬outer</c>, so an entity that fell out of the
    /// nearest-k but is still inside the exit radius would be kept, and over ticks a client's known set
    /// would creep up to the whole outer population — breaking both the "entities this client can be told
    /// about at once" contract and the bandwidth guarantee that rests on it. Capping the outer set instead
    /// bounds <c>Known</c> by construction, and it costs nothing in enter semantics: every entity in the
    /// hysteresis band is strictly farther than every entity inside the enter radius, so the nearest-k of
    /// the outer set restricted to the enter radius <i>is</i> the nearest-k of the inner set.
    /// </para>
    /// </remarks>
    private void ApplyVisibilityCap(SubscriberState sub)
    {
        int candidates = sub.VisibleOuter.Count();
        if (candidates <= _maxVisibleEntities)
        {
            return;
        }

        _nearest.Reset();
        float focusX = sub.FocusX;
        float focusY = sub.FocusY;
        float[] xs = _table.X;
        float[] ys = _table.Y;
        foreach (int slot in sub.VisibleOuter.EnumerateSetBits())
        {
            float dx = xs[slot] - focusX;
            float dy = ys[slot] - focusY;
            _nearest.Offer(slot, (dx * dx) + (dy * dy));   // squared: ranking never needs a square root
        }

        _nearest.AssertHeapInvariant();
        _nearest.FillBitset(_capScratch);
        sub.VisibleOuter.IntersectWith(_capScratch);
        sub.VisibleInner.IntersectWith(_capScratch);
        CappedVisibilityCount++;
    }

    // ── Internals ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Offset of this slot's <c>FullRecord</c> in the scratch buffer, encoding it on first use this tick.
    /// Tick-stamped so each slot is serialized at most once per tick no matter how many clients gain
    /// visibility of it.
    /// </summary>
    private int GetFullRecordOffset(int slot)
    {
        if (_fullStamp[slot] == _tickSeq)
        {
            return _fullOffset[slot];
        }

        _table.FillWireState(slot, out EntityWireState state);
        int size = HotWire.WriteFullRecord(_scratch.AsSpan(_scratchCursor), _table.PackId(slot), state);
        Debug.Assert(size == HotWire.FullRecordSize, "scratch is sized to always fit");
        _fullOffset[slot] = _scratchCursor;
        _fullStamp[slot] = _tickSeq;
        _scratchCursor += size;
        LastTickFullRecordsEncoded++;
        return _fullOffset[slot];
    }

    /// <summary>
    /// Stamp for <c>LastChangedTick</c> on mutations that happen while the room drains its inbound queue —
    /// i.e. before the <see cref="Tick"/> that will broadcast them.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint NextTickStamp() => _serverTick + 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CountMaskViolation(uint clientId)
    {
        if (_subscribers.TryGetValue(clientId, out SubscriberState? sub))
        {
            sub.ViolationMask++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CountOwnershipViolation(uint clientId)
    {
        if (_subscribers.TryGetValue(clientId, out SubscriberState? sub))
        {
            sub.ViolationOwnership++;
        }
    }
}
