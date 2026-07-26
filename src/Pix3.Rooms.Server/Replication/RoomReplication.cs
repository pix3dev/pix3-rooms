using System.Diagnostics;
using System.Runtime.CompilerServices;
using Pix3.Rooms.Protocol;

namespace Pix3.Rooms.Server.Replication;

/// <summary>
/// The replication core for one room: entity table + spatial hash AOI + per-subscriber known-sets +
/// encode-once frame assembly. Single-threaded by contract; depends on <c>Pix3.Rooms.Protocol</c>
/// only, so it is unit-testable with no sockets.
/// </summary>
/// <remarks>
/// <para><b>Data layout / tick flow.</b> Client updates accumulate dirty state in the
/// <see cref="EntityTable"/> while the room drains its inbound queue. <see cref="Tick"/> then
/// (1) rebuilds the <see cref="SpatialHashGrid"/> from the packed live slots, (2) serializes every
/// dirty entity's <c>DeltaRecord</c> <i>exactly once</i> into one preallocated scratch buffer
/// (recording per-slot offset/length) and (3) clears the table's dirty state. <c>FullRecord</c>s are
/// encoded into the same scratch lazily, at most once per tick per slot (tick-stamped), the first
/// time any client needs one. <see cref="WriteDelta"/>/<see cref="WriteSnapshot"/> assemble
/// per-client frames purely by <c>Span.CopyTo</c> from that scratch — encode once, memcpy many.
/// The scratch is sized <c>capacity × (MaxDeltaRecordSize + FullRecordSize)</c>, so it can never
/// overflow and is never resized.</para>
/// <para><b>Visibility diffing.</b> Per client, the grid fills an inner (enter-radius) and an outer
/// (exit-radius = enter + hysteresis) visibility bitset in one pass. Exits are
/// <c>Known ∧ ¬outer</c> plus generation mismatches; enters are <c>inner ∧ ¬Known</c>; updates are
/// <c>Known(before enters) ∧ dirty</c>. A client therefore never receives a delta for an entity it
/// was not first given a full record for, and an entity that enters AOI while dirty is sent as a
/// full record only. A client's own entities are replicated to it like any other — the client
/// runtime decides whether to ignore echoes of its own authority.</para>
/// <para><b>Backpressure.</b> Frames never exceed <c>MaxPayloadBytes</c>. When a client's payload
/// would overflow, sections are filled in priority order (exits &gt; enters &gt; updates) and the
/// remainder carries over: an exit that was not written keeps its Known bit (re-detected next tick),
/// an enter that was not written leaves the entity un-Known (re-entered next tick). Known state is
/// mutated only when the corresponding bytes were actually placed in the frame.</para>
/// </remarks>
public sealed class RoomReplication : IRoomReplication
{
    private readonly ReplicationOptions _options;
    private readonly EntityTable _table;
    private readonly SpatialHashGrid _grid;

    private readonly float _aoiEnterRadius;
    private readonly float _aoiExitRadius;
    private readonly int _maxPayloadBytes;

    // Encode-once scratch: delta records first (eagerly, in Tick), full records appended lazily.
    private readonly byte[] _scratch;
    private int _scratchCursor;

    private readonly int[] _deltaOffset;   // valid only for slots set in _encodedDirty
    private readonly byte[] _deltaSize;
    private readonly int[] _fullOffset;    // valid only when _fullStamp[slot] == _tickSeq
    private readonly ulong[] _fullStamp;

    /// <summary>Dirty set snapshotted at <see cref="Tick"/>; what the per-client update pass diffs against.</summary>
    private readonly SlotBitset _encodedDirty;

    private readonly Dictionary<uint, SubscriberState> _subscribers;
    private readonly Stack<SubscriberState> _subscriberPool;

    private ulong _tickSeq;      // internal monotonic tick counter; 0 = Tick never ran
    private uint _serverTick;    // room-supplied tick echoed into frames

    /// <summary>Creates a replication core with all storage allocated up front.</summary>
    public RoomReplication(ReplicationOptions options)
    {
        options.Validate();
        _options = options;
        _table = new EntityTable(options.MaxEntities);
        _grid = new SpatialHashGrid(options.MaxEntities, options.EffectiveCellSize);

        _aoiEnterRadius = options.AoiRadius;
        _aoiExitRadius = options.AoiRadius + options.EffectiveHysteresis;
        _maxPayloadBytes = options.MaxPayloadBytes;

        _scratch = new byte[options.MaxEntities * (HotWire.MaxDeltaRecordSize + HotWire.FullRecordSize)];
        _deltaOffset = new int[options.MaxEntities];
        _deltaSize = new byte[options.MaxEntities];
        _fullOffset = new int[options.MaxEntities];
        _fullStamp = new ulong[options.MaxEntities];
        _encodedDirty = new SlotBitset(options.MaxEntities);

        _subscribers = new Dictionary<uint, SubscriberState>(options.MaxPlayers);
        _subscriberPool = new Stack<SubscriberState>();
    }

    // ── Metrics (plain properties; read by the room when publishing stats) ─────

    /// <inheritdoc />
    public int EntityCount => _table.LiveCount;

    /// <summary>The entity table, exposed for tests and room-level introspection.</summary>
    public EntityTable Table => _table;

    /// <summary>Currently tracked subscribers.</summary>
    public int SubscriberCount => _subscribers.Count;

    /// <summary>Tick value passed to the most recent <see cref="Tick"/>.</summary>
    public uint LastServerTick => _serverTick;

    /// <summary>Delta records encoded into scratch by the last <see cref="Tick"/> (dirty entities).</summary>
    public int LastTickDirtyCount { get; private set; }

    /// <summary>Bytes of delta records encoded into scratch by the last <see cref="Tick"/>.</summary>
    public int LastTickDeltaScratchBytes { get; private set; }

    /// <summary>Full records lazily encoded into scratch since the last <see cref="Tick"/>.</summary>
    public int LastTickFullRecordsEncoded { get; private set; }

    /// <summary>Frame bytes returned by all writers since the last <see cref="Tick"/>.</summary>
    public long LastTickBytesWritten { get; private set; }

    /// <summary>Non-empty frames produced since the last <see cref="Tick"/>.</summary>
    public int LastTickFramesWritten { get; private set; }

    /// <summary>Σ inner-visible entities over this tick's <see cref="WriteDelta"/> calls (divide by <see cref="LastTickDeltaCalls"/> for the average).</summary>
    public long LastTickVisibleSum { get; private set; }

    /// <summary><see cref="WriteDelta"/> calls since the last <see cref="Tick"/>.</summary>
    public int LastTickDeltaCalls { get; private set; }

    /// <summary>Total frame bytes returned over the room's lifetime.</summary>
    public long TotalBytesWritten { get; private set; }

    /// <summary>Total non-empty frames over the room's lifetime.</summary>
    public long TotalFramesWritten { get; private set; }

    /// <summary>Client updates rejected for carrying server-only mask bits.</summary>
    public long IllegalMaskCount { get; private set; }

    /// <summary>Client requests naming an entity they do not own.</summary>
    public long OwnershipViolationCount { get; private set; }

    /// <summary>Client requests naming an unknown/stale entity id.</summary>
    public long UnknownEntityCount { get; private set; }

    /// <summary><see cref="AddSubscriber"/> calls beyond <see cref="ReplicationOptions.MaxPlayers"/> (room-side bug indicator).</summary>
    public long SubscriberOverflowCount { get; private set; }

    /// <summary>Slots permanently retired after exhausting their generation space.</summary>
    public int RetiredSlotCount => _table.RetiredSlotCount;

    // ── Entity mutation ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public bool TrySpawn(uint ownerId, ushort kind, in EntityWireState state, out uint netId, out RejectCode reject)
    {
        if (!_table.TrySpawn(ownerId, kind, state, NextTickStamp(), out netId))
        {
            reject = RejectCode.EntityLimitReached;
            return false;
        }

        // No dirty-marking here: the new entity reaches every interested client through the AOI
        // enter path as a FullRecord, never as a delta.
        reject = RejectCode.None;
        return true;
    }

    /// <inheritdoc />
    public bool TryDespawn(uint netId, uint requesterId, out RejectCode reject)
    {
        if (!_table.TryResolve(netId, out int slot))
        {
            UnknownEntityCount++;
            reject = RejectCode.BadRequest;
            return false;
        }

        // Server (requester 0) may despawn anything; a client only its own.
        if (requesterId != 0 && _table.OwnerId[slot] != requesterId)
        {
            OwnershipViolationCount++;
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
            return false;
        }

        if (!_table.TryResolve(netId, out int slot))
        {
            // Stale ids are expected traffic (entity despawned in flight), not necessarily abuse.
            UnknownEntityCount++;
            return false;
        }

        if (ownerId != 0 && _table.OwnerId[slot] != ownerId)
        {
            OwnershipViolationCount++;
            return false;
        }

        // Masked merge — unmasked fields survive. A Teleport bit is accumulated into the dirty mask
        // and travels in the delta record so already-known clients snap; clients that see the entity
        // enter AOI this tick get a FullRecord anyway (absolute state), so no separate path is needed.
        _table.ApplyUpdate(slot, mask, state, NextTickStamp());
        return true;
    }

    /// <summary>
    /// Marks an entity's cold props as changed (<see cref="DeltaMask.ColdDirty"/>), promising a
    /// follow-up <see cref="EntityColdPropsEvent"/> on the control plane. Server-authored — the room
    /// calls this after validating a cold-props change; ownership is not re-checked here. False when
    /// the id is unknown or stale. (Additive helper, not part of <see cref="IRoomReplication"/>.)
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
            despawned.Add(_table.PackId(slot));
            _table.Despawn(slot);
            slot = next;
        }
        // No per-subscriber cleanup needed: dead slots leave every client's outer-visible set, so the
        // Known diff emits the removed ids (packed with the generation each client knew) even for
        // clients whose frame is assembled long after the despawn.
    }

    // ── Subscribers ─────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void AddSubscriber(uint clientId)
    {
        if (_subscribers.TryGetValue(clientId, out SubscriberState? existing))
        {
            existing.Reset(clientId);   // rejoin: forget everything it knew
            return;
        }

        if (_subscribers.Count >= _options.MaxPlayers)
        {
            // The room enforces MaxPlayers before joining; hitting this means a room-side bug.
            // Count it loudly rather than growing past the sized pool.
            SubscriberOverflowCount++;
            return;
        }

        SubscriberState sub = _subscriberPool.Count > 0
            ? _subscriberPool.Pop()
            : new SubscriberState(_options.MaxEntities);
        sub.Reset(clientId);
        _subscribers.Add(clientId, sub);
    }

    /// <inheritdoc />
    public void RemoveSubscriber(uint clientId)
    {
        if (_subscribers.Remove(clientId, out SubscriberState? sub))
        {
            _subscriberPool.Push(sub);   // state is wiped on next checkout, not here
        }
    }

    /// <inheritdoc />
    public void SetSubscriberFocus(uint clientId, float x, float y)
    {
        if (_subscribers.TryGetValue(clientId, out SubscriberState? sub))
        {
            sub.FocusX = x;
            sub.FocusY = y;
        }
    }

    // ── Tick: rebuild grid + encode-once scratch ───────────────────────────────

    /// <inheritdoc />
    public void Tick(uint serverTick)
    {
        _tickSeq++;
        _serverTick = serverTick;

        _grid.Build(_table);

        _scratchCursor = 0;
        LastTickFullRecordsEncoded = 0;
        LastTickBytesWritten = 0;
        LastTickFramesWritten = 0;
        LastTickVisibleSum = 0;
        LastTickDeltaCalls = 0;

        // Serialize every dirty entity's DeltaRecord exactly once, regardless of how many clients
        // will receive it. Per-client assembly is pure memcpy from here on.
        _encodedDirty.CopyFrom(_table.Dirty);
        int deltaRecords = 0;
        int deltaBytes = 0;
        foreach (int slot in _encodedDirty.EnumerateSetBits())
        {
            Debug.Assert(_table.IsAlive(slot), "despawn scrubs dirty state, so dirty slots are alive");
            byte mask = _table.DirtyMask[slot];
            _table.FillWireState(slot, out EntityWireState state);
            int size = HotWire.WriteDeltaRecord(_scratch.AsSpan(_scratchCursor), _table.PackId(slot), mask, state);
            Debug.Assert(size > 0, "scratch is sized to always fit");
            _deltaOffset[slot] = _scratchCursor;
            _deltaSize[slot] = (byte)size;
            _scratchCursor += size;
            deltaRecords++;
            deltaBytes += size;
        }

        _table.ClearDirty();
        LastTickDirtyCount = deltaRecords;
        LastTickDeltaScratchBytes = deltaBytes;
    }

    // ── Per-client frame assembly ──────────────────────────────────────────────

    /// <inheritdoc />
    public int WriteDelta(uint clientId, Span<byte> destination)
    {
        if (_tickSeq == 0 || !_subscribers.TryGetValue(clientId, out SubscriberState? sub))
        {
            return 0;
        }

        int limit = Math.Min(destination.Length, _maxPayloadBytes);
        if (limit < HotWire.MinDeltaFrameSize)
        {
            return 0;
        }

        _grid.QueryRadiusWithHysteresis(
            sub.FocusX, sub.FocusY, _aoiEnterRadius, _aoiExitRadius, sub.VisibleInner, sub.VisibleOuter);

        LastTickDeltaCalls++;
        LastTickVisibleSum += sub.VisibleInner.Count();

        Span<byte> frame = destination.Slice(0, limit);
        int cursor = HotWire.WriteDeltaFrameHeader(frame, _serverTick);

        SlotBitset known = sub.Known;
        SlotBitset inner = sub.VisibleInner;
        SlotBitset outer = sub.VisibleOuter;
        ushort[] knownGen = sub.KnownGeneration;

        // ── Removed section: despawned, left AOI (beyond the exit radius), or the slot was reused
        // by a different entity (generation mismatch — the client must drop the old one first).
        int removedCountOffset = cursor;
        cursor += HotWire.WriteSectionCountPlaceholder(frame.Slice(cursor));
        int removedCount = 0;
        int tailReserve = HotWire.SectionCountSize * 2;   // the two section counts still to come
        foreach (int slot in known.EnumerateSetBits())
        {
            bool stillSame = outer.Get(slot)
                && _table.IsAlive(slot)
                && _table.Generation[slot] == knownGen[slot];
            if (stillSame)
            {
                continue;
            }

            if (removedCount == ushort.MaxValue || cursor + HotWire.RemovedIdSize + tailReserve > limit)
            {
                // Out of space: the Known bit stays set, so this exit is re-detected and sent next
                // tick — removed ids are never lost, even for entities that despawned long ago.
                break;
            }

            // Pack with the generation the CLIENT knew — the table's current generation may already
            // belong to a different entity.
            cursor += HotWire.WriteRemovedId(frame.Slice(cursor), NetId.Pack(slot, knownGen[slot]));
            known.Unset(slot);   // safe: the enumerator snapshots each word as it loads it
            knownGen[slot] = 0;
            removedCount++;
        }

        HotWire.TryPatchSectionCount(frame, removedCountOffset, removedCount);

        // Snapshot Known between the exit and enter passes: update membership below is decided
        // against this, so an entity that both enters AOI and is dirty this tick is sent as a full
        // record only — never full + delta.
        SlotBitset knownBefore = sub.KnownBeforeEnters;
        knownBefore.CopyFrom(known);

        // ── Enter section: full records for entities inside the enter radius the client does not
        // know (including slots whose removal was just written — reuse becomes remove + enter).
        int enterCountOffset = cursor;
        cursor += HotWire.WriteSectionCountPlaceholder(frame.Slice(cursor));
        int enterCount = 0;
        tailReserve = HotWire.SectionCountSize;           // the update-section count still to come
        foreach (int slot in inner.EnumerateAndNot(knownBefore))
        {
            if (!_table.IsAlive(slot))
            {
                continue;   // defensive: despawned between Tick and assembly
            }

            if (enterCount == ushort.MaxValue || cursor + HotWire.FullRecordSize + tailReserve > limit)
            {
                // Out of space: the entity stays un-Known and re-enters next tick. Only entities
                // whose full record actually shipped are marked Known.
                break;
            }

            int src = GetFullRecordOffset(slot);
            _scratch.AsSpan(src, HotWire.FullRecordSize).CopyTo(frame.Slice(cursor));
            cursor += HotWire.FullRecordSize;
            known.Set(slot);
            knownGen[slot] = _table.Generation[slot];
            enterCount++;
        }

        HotWire.TryPatchSectionCount(frame, enterCountOffset, enterCount);

        // ── Update section: delta records for entities that were already known before this frame's
        // enters and are dirty this tick. Membership via Known guarantees the client has previously
        // received a full record for every delta it gets.
        int updateCountOffset = cursor;
        cursor += HotWire.WriteSectionCountPlaceholder(frame.Slice(cursor));
        int updateCount = 0;
        foreach (int slot in knownBefore.EnumerateAnd(_encodedDirty))
        {
            // Skip entities whose removal is pending (exit section ran out of space): the client
            // still knows them, but spending bytes on soon-dead state is waste — and if the slot was
            // reused, the delta's netId would not even match what the client holds.
            if (!outer.Get(slot)
                || !_table.IsAlive(slot)
                || _table.Generation[slot] != knownGen[slot])
            {
                continue;
            }

            int size = _deltaSize[slot];
            if (updateCount == ushort.MaxValue || cursor + size > limit)
            {
                break;   // dropped updates are not carried over: next tick's delta supersedes them
            }

            _scratch.AsSpan(_deltaOffset[slot], size).CopyTo(frame.Slice(cursor));
            cursor += size;
            updateCount++;
        }

        HotWire.TryPatchSectionCount(frame, updateCountOffset, updateCount);

        if (removedCount == 0 && enterCount == 0 && updateCount == 0)
        {
            return 0;   // nothing for this client → no frame at all (per protocol)
        }

        LastTickBytesWritten += cursor;
        LastTickFramesWritten++;
        TotalBytesWritten += cursor;
        TotalFramesWritten++;
        return cursor;
    }

    /// <inheritdoc />
    public int WriteSnapshot(uint clientId, Span<byte> destination, ref int continuationCursor)
    {
        if (_tickSeq == 0 || !_subscribers.TryGetValue(clientId, out SubscriberState? sub))
        {
            continuationCursor = 0;
            return 0;
        }

        int limit = Math.Min(destination.Length, _maxPayloadBytes);
        if (limit < HotWire.SnapshotFrameHeaderSize + HotWire.FullRecordSize)
        {
            return 0;   // cannot make progress; cursor unchanged so the caller can retry
        }

        _grid.QueryRadiusWithHysteresis(
            sub.FocusX, sub.FocusY, _aoiEnterRadius, _aoiExitRadius, sub.VisibleInner, sub.VisibleOuter);

        Span<byte> frame = destination.Slice(0, limit);
        int cursor = HotWire.WriteSnapshotFrameHeader(frame, _serverTick);
        int count = 0;

        ReadOnlySpan<int> live = _table.LiveSlots;
        SlotBitset inner = sub.VisibleInner;
        SlotBitset known = sub.Known;
        ushort[] knownGen = sub.KnownGeneration;

        // The cursor is an index into the dense live list. Despawns between resumed calls can
        // reorder that list (swap-remove); an entity skipped that way stays un-Known and is
        // delivered by the very next delta's enter path, so resume is self-healing.
        int i = continuationCursor;
        if (i < 0 || i > live.Length)
        {
            i = 0;
        }

        for (; i < live.Length; i++)
        {
            int slot = live[i];
            if (!inner.Get(slot))
            {
                continue;
            }

            if (known.Get(slot) && knownGen[slot] == _table.Generation[slot])
            {
                continue;   // already delivered by an earlier continuation frame
            }

            if (count == ushort.MaxValue || cursor + HotWire.FullRecordSize > limit)
            {
                break;      // frame full — resume from this dense index next call
            }

            int src = GetFullRecordOffset(slot);
            _scratch.AsSpan(src, HotWire.FullRecordSize).CopyTo(frame.Slice(cursor));
            cursor += HotWire.FullRecordSize;
            known.Set(slot);
            knownGen[slot] = _table.Generation[slot];
            count++;
        }

        continuationCursor = i >= live.Length ? 0 : i;

        if (count == 0)
        {
            return 0;   // nothing (left) visible — no frame
        }

        HotWire.TryPatchSnapshotFrameCount(frame, count);
        LastTickBytesWritten += cursor;
        LastTickFramesWritten++;
        TotalBytesWritten += cursor;
        TotalFramesWritten++;
        return cursor;
    }

    // ── Internals ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Offset of this slot's FullRecord in the scratch buffer, encoding it on first use this tick.
    /// Tick-stamped so each slot is serialized at most once per tick no matter how many clients
    /// gain visibility of it.
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
    /// Stamp for LastChangedTick on mutations that happen while the room drains its inbound queue —
    /// i.e. before the Tick that will broadcast them.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint NextTickStamp() => _serverTick + 1;
}
