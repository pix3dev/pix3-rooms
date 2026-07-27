using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Pix3.Rooms.Protocol;

namespace Pix3.Rooms.Server.Replication;

/// <summary>
/// Frame assembly and the two-phase known-set commit. Split out of <c>RoomReplication.cs</c> because this
/// half is where every wire-layout and every recovery invariant lives.
/// </summary>
public sealed partial class RoomReplication
{
    // ── SnapshotPacket ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The scan walks the table's dense live list from this client's snapshot cursor and emits a
    /// <c>FullRecord</c> for every visible slot it does not already know. The frame that reaches the end of
    /// the list is stamped <c>FrameFlags.Final</c>: without it a client has no way to know a split snapshot
    /// is complete.
    /// </para>
    /// <para>
    /// An <b>empty</b> final frame is still emitted (10 bytes) when the client can see nothing at all.
    /// "A tick with nothing produces no frame" is a rule about deltas; a joiner that never receives a
    /// <c>Final</c> would wait forever before trusting its (empty) known set.
    /// </para>
    /// <para>
    /// Despawns between resumed calls can reorder the dense list (swap-remove); an entity skipped that way
    /// stays un-known and is delivered by the very next delta's enter path, so resume is self-healing.
    /// </para>
    /// </remarks>
    public int WriteSnapshot(uint clientId, Span<byte> destination, out PendingKnownSetCommit commit)
    {
        if (!TryBeginFrame(
                clientId,
                destination,
                HotWire.SnapshotPacketHeaderSize + HotWire.FullRecordSize,
                out SubscriberState? sub,
                out int limit,
                out commit))
        {
            return 0;
        }

        if (!sub.SnapshotPending)
        {
            return 0;   // nothing outstanding: this client is served by deltas
        }

        EnsureVisibility(sub);
        sub.DiscardPendingFrame();

        Span<byte> frame = destination.Slice(0, limit);
        int cursor = HotWire.WriteSnapshotPacketHeader(frame, sub.NextSeq, _serverTick);
        int count = 0;
        bool truncated = false;

        ReadOnlySpan<int> live = _table.LiveSlots;
        int index = sub.SnapshotCursor;
        if (index < 0 || index > live.Length)
        {
            index = 0;
        }

        for (; index < live.Length; index++)
        {
            int slot = live[index];
            if (!sub.VisibleInner.Get(slot))
            {
                continue;
            }

            if (sub.Known.Get(slot) && sub.KnownGeneration[slot] == _table.Generation[slot])
            {
                continue;   // already delivered by an earlier continuation frame
            }

            if (count >= sub.PendingEnters.Length
                || count == ushort.MaxValue
                || cursor + HotWire.FullRecordSize > limit)
            {
                truncated = true;
                break;      // frame full — resume from this dense index next call
            }

            _scratch.AsSpan(GetFullRecordOffset(slot), HotWire.FullRecordSize).CopyTo(frame.Slice(cursor));
            cursor += HotWire.FullRecordSize;

            // INTENT ONLY: the known bit is flipped by Commit, never here.
            sub.PendingEnters[count] = slot;
            sub.PendingEnterGenerations[count] = _table.Generation[slot];
            count++;
        }

        sub.PendingEnterCount = count;
        sub.PendingHasSnapshotCursor = true;
        sub.PendingIsFinalSnapshot = !truncated;
        sub.PendingSnapshotCursor = truncated ? index : 0;

        if (truncated)
        {
            SplitSnapshotFrameCount++;
            if (count == 0)
            {
                // Defensive: the validated minimum byte budget guarantees room for one full record, so no
                // progress at all should be impossible. Emitting a zero-record non-final frame would burn a
                // Seq for nothing, so refuse instead and let the caller retry.
                sub.DiscardPendingFrame();
                commit = default;
                return 0;
            }
        }

        HotWire.TryPatchSnapshotPacketCount(frame, count);
        if (!truncated)
        {
            HotWire.TryPatchSnapshotPacketFrameFlags(frame, FrameFlags.Final);
        }

        commit = OpenFrame(sub, !truncated);
        RecordFrameBytes(cursor);
        return cursor;
    }

    // ── DeltaPacket ────────────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Sections are written in wire order — removals, enters, updates — which is also the order the client
    /// must apply them: a slot's removal always precedes any reuse of that slot, so <c>u16 Slot</c>
    /// addressing is unambiguous on an ordered stream.
    /// </para>
    /// <para>
    /// <b>Truncation is a debt, not a loss.</b> A removal that did not fit keeps its known bit and is
    /// re-detected next tick; an enter that did not fit stays un-known and is retried from the carry cursor;
    /// an update that did not fit is remembered per client and re-sent from <i>current</i> state later,
    /// because a dropped absolute value only self-heals if the entity keeps changing.
    /// </para>
    /// </remarks>
    public int WriteDelta(uint clientId, Span<byte> destination, out PendingKnownSetCommit commit)
    {
        if (!TryBeginFrame(
                clientId,
                destination,
                HotWire.DeltaPacketFixedOverhead,
                out SubscriberState? sub,
                out int limit,
                out commit))
        {
            return 0;
        }

        if (sub.SnapshotPending)
        {
            // No delta before the snapshot that establishes this client's slot→netId mapping. Update
            // records are slot-addressed, so a delta arriving first would be undecodable.
            return 0;
        }

        EnsureVisibility(sub);
        sub.DiscardPendingFrame();

        Span<byte> frame = destination.Slice(0, limit);
        int cursor = HotWire.WriteDeltaPacketHeader(frame, sub.NextSeq, _serverTick);

        // ── Removed section: despawned, left AOI (beyond the exit radius or trimmed by the k-nearest cap),
        // or the slot was reused / its owner changed (generation mismatch — the client must drop the old
        // entity before the new full record arrives).
        int removedCountOffset = cursor;
        cursor += HotWire.WriteSectionCountPlaceholder(frame.Slice(cursor));
        int removedCount = 0;
        int tailReserve = HotWire.SectionCountSize * 2;   // the two section counts still to come
        foreach (int slot in sub.Known.EnumerateSetBits())
        {
            bool stillSame = sub.VisibleOuter.Get(slot)
                && _table.IsAlive(slot)
                && _table.Generation[slot] == sub.KnownGeneration[slot];
            if (stillSame)
            {
                continue;
            }

            if (removedCount >= sub.PendingRemovals.Length
                || removedCount == ushort.MaxValue
                || cursor + HotWire.RemovedSlotSize + tailReserve > limit)
            {
                // Out of space: the known bit stays set, so this exit is re-detected and sent next tick —
                // removals are never lost, even for entities that despawned long ago.
                TruncatedRemovalSectionCount++;
                break;
            }

            cursor += HotWire.WriteRemovedSlot(frame.Slice(cursor), (ushort)slot);
            sub.PendingRemovals[removedCount++] = slot;   // INTENT ONLY
        }

        sub.PendingRemovalCount = removedCount;
        HotWire.TryPatchSectionCount(frame, removedCountOffset, removedCount);

        // Known-set snapshot for the rest of the frame: what the client knows once this frame's removals
        // are applied and before its enters are. Enter membership is decided against this, so an entity
        // that both enters AOI and is dirty this tick is sent as a full record only — never full + delta —
        // and a slot whose removal was just written can be re-entered in the same frame. Removals are
        // subtracted rather than the real known set being mutated: Known belongs to Commit.
        sub.KnownBeforeEnters.CopyFrom(sub.Known);
        for (int i = 0; i < removedCount; i++)
        {
            sub.KnownBeforeEnters.Unset(sub.PendingRemovals[i]);
        }

        // ── Enter section: full records for entities inside the enter radius the client does not know,
        // capped at MaxEntersPerTick and resumed from the carry cursor so the same low slots do not win the
        // budget every tick and a starved client eventually sees everything. The scan wraps exactly once.
        int enterCountOffset = cursor;
        cursor += HotWire.WriteSectionCountPlaceholder(frame.Slice(cursor));
        int enterCap = Math.Min(_maxEntersPerTick, sub.PendingEnters.Length);
        int carryStart = sub.EnterCursor;
        if ((uint)carryStart >= (uint)_table.Capacity)
        {
            carryStart = 0;
        }

        EnterScan scan = new() { Cursor = cursor, LastSlot = -1 };
        AppendEnters(sub, frame, limit, HotWire.SectionCountSize, enterCap, carryStart, _table.Capacity, ref scan);
        AppendEnters(sub, frame, limit, HotWire.SectionCountSize, enterCap, 0, carryStart, ref scan);
        cursor = scan.Cursor;

        sub.PendingEnterCount = scan.Count;
        HotWire.TryPatchSectionCount(frame, enterCountOffset, scan.Count);

        sub.PendingHasEnterCursor = true;
        if (scan.Truncated)
        {
            // Resume just past the last entity that made it in; if nothing did (the byte budget was spent
            // on removals), stay put so the same candidates are retried.
            sub.PendingEnterCursor = scan.LastSlot >= 0 ? (scan.LastSlot + 1) % _table.Capacity : carryStart;
            EnterCarryCount++;
        }
        else
        {
            sub.PendingEnterCursor = 0;   // the scan covered the whole slot space: nothing is carried
        }

        // ── Update section: records for entities the client knew before this frame's enters and that are
        // dirty this tick or still owed to it. Membership via KnownBeforeEnters guarantees the client has
        // previously received a full record for every delta it gets.
        int updateCountOffset = cursor;
        cursor += HotWire.WriteSectionCountPlaceholder(frame.Slice(cursor));
        int updateCount = 0;
        foreach (int slot in sub.KnownBeforeEnters.EnumerateAnd(_encodedDirty))
        {
            // _encodedDirty is the union across ALL clients, so without this probe one slow client's backlog
            // would be amplified onto every other client. One extra bitset read, no new enumerator. (The
            // _tickDirty half is very nearly implied by the debt Tick registered; it still covers a slot this
            // client only learned about earlier in this same tick, at the cost of one redundant record.)
            if (!_tickDirty.Get(slot) && !sub.PendingUpdates.Get(slot))
            {
                continue;
            }

            // Skip entities whose removal is pending (the removal section ran out of space) or whose slot
            // was reused: spending bytes on soon-dead state is waste, and the client's copy is a different
            // entity anyway.
            if (!sub.VisibleOuter.Get(slot)
                || !_table.IsAlive(slot)
                || _table.Generation[slot] != sub.KnownGeneration[slot])
            {
                continue;
            }

            int size = _updateSize[slot];
            if (updateCount >= sub.PendingUpdateSlots.Length
                || updateCount == ushort.MaxValue
                || cursor + size > limit)
            {
                // The debt registered by Tick is simply not cleared, so this entity is re-offered — from
                // current state — on a later tick.
                TruncatedUpdateSectionCount++;
                break;
            }

            _scratch.AsSpan(_updateOffset[slot], size).CopyTo(frame.Slice(cursor));
            cursor += size;
            sub.PendingUpdateSlots[updateCount++] = slot;   // INTENT ONLY: the owed bit is cleared by Commit
        }

        sub.PendingUpdateSlotCount = updateCount;
        HotWire.TryPatchSectionCount(frame, updateCountOffset, updateCount);

        if (removedCount == 0 && scan.Count == 0 && updateCount == 0)
        {
            // Nothing for this client → no frame at all (per protocol). Dropping the intent is safe by
            // construction: debts were registered by Tick and are only ever cleared by a commit, so an
            // undelivered update stays owed whether or not a frame existed.
            sub.DiscardPendingFrame();
            commit = default;
            return 0;
        }

        commit = OpenFrame(sub, isFinalSnapshotFrame: false);
        LastTickDeltaCalls++;
        RecordFrameBytes(cursor);
        return cursor;
    }

    /// <summary>Cursor and progress of the wrapping enter scan, threaded through both range passes.</summary>
    private struct EnterScan
    {
        /// <summary>Write cursor inside the frame.</summary>
        public int Cursor;

        /// <summary>Enter records written so far.</summary>
        public int Count;

        /// <summary>Highest slot actually written, or -1; seeds the next tick's carry cursor.</summary>
        public int LastSlot;

        /// <summary>True once the cap or the byte budget stopped the scan.</summary>
        public bool Truncated;
    }

    /// <summary>
    /// Appends enter records for candidate slots in <c>[minSlot, maxSlotExclusive)</c>. Called twice — once
    /// from the carry cursor to the top of the slot space, once from zero back to it — which is how the scan
    /// wraps exactly once without needing a second enumerator shape.
    /// </summary>
    private void AppendEnters(
        SubscriberState sub,
        Span<byte> frame,
        int limit,
        int tailReserve,
        int enterCap,
        int minSlot,
        int maxSlotExclusive,
        ref EnterScan scan)
    {
        if (scan.Truncated || minSlot >= maxSlotExclusive)
        {
            return;
        }

        foreach (int slot in sub.VisibleInner.EnumerateAndNot(sub.KnownBeforeEnters))
        {
            if (slot < minSlot || slot >= maxSlotExclusive)
            {
                continue;
            }

            if (!_table.IsAlive(slot))
            {
                continue;   // defensive: despawned between Tick and assembly
            }

            if (scan.Count >= enterCap
                || scan.Count == ushort.MaxValue
                || scan.Cursor + HotWire.FullRecordSize + tailReserve > limit)
            {
                // Out of budget: the entity stays un-known and is retried from the carry cursor. Only
                // entities whose full record actually shipped are ever marked known — and only by Commit.
                scan.Truncated = true;
                return;
            }

            _scratch.AsSpan(GetFullRecordOffset(slot), HotWire.FullRecordSize)
                .CopyTo(frame.Slice(scan.Cursor));
            scan.Cursor += HotWire.FullRecordSize;
            sub.PendingEnters[scan.Count] = slot;                            // INTENT ONLY
            sub.PendingEnterGenerations[scan.Count] = _table.Generation[slot];
            scan.Count++;
            scan.LastSlot = slot;
        }
    }

    // ── SignalBatchPacket ──────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Entries were encoded once when they were queued; this copies exactly those whose sender's focus slot
    /// is in this recipient's visible set, <b>excluding the sender itself</b> — the emitter already simulated
    /// its own event locally. A truncated batch is not carried: signals are events (a fire effect, a hit
    /// announcement), and replaying one a tick late is worse than dropping it.
    /// </remarks>
    public int WriteSignalBatch(uint clientId, Span<byte> destination, out PendingKnownSetCommit commit)
    {
        commit = default;
        if (_signals.Count == 0)
        {
            return 0;
        }

        if (!TryBeginFrame(
                clientId,
                destination,
                HotWire.SignalBatchPacketHeaderSize + HotWire.MinSignalEntrySize,
                out SubscriberState? sub,
                out int limit,
                out commit))
        {
            return 0;
        }

        EnsureVisibility(sub);
        sub.DiscardPendingFrame();

        Span<byte> frame = destination.Slice(0, limit);
        int cursor = HotWire.WriteSignalBatchPacketHeader(frame, sub.NextSeq, _serverTick);
        int count = 0;
        int entries = _signals.Count;
        for (int i = 0; i < entries; i++)
        {
            if (_signals.SenderClientIdOf(i) == clientId)
            {
                continue;
            }

            int senderSlot = _signals.SenderSlotOf(i);
            if (!_table.IsAlive(senderSlot) || !sub.VisibleInner.Get(senderSlot))
            {
                continue;
            }

            ReadOnlySpan<byte> entry = _signals.EntryBytes(i);
            if (count >= HotWire.MaxSignalBatchEntries || cursor + entry.Length > limit)
            {
                TruncatedSignalBatchCount++;
                break;
            }

            entry.CopyTo(frame.Slice(cursor));
            cursor += entry.Length;
            count++;
        }

        if (count == 0)
        {
            sub.DiscardPendingFrame();
            commit = default;
            return 0;
        }

        HotWire.TryPatchSignalBatchPacketCount(frame, count);
        AoiSignalEntriesCopiedCount += count;

        // No known-set intent — but Seq must still advance only on a successful enqueue, which is why a
        // signal batch is a pending frame like any other.
        commit = OpenFrame(sub, isFinalSnapshotFrame: false);
        RecordFrameBytes(cursor);
        return cursor;
    }

    // ── Two-phase commit ───────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// This is the only place a known-set bit is ever flipped, and the only place <c>Seq</c> ever advances.
    /// Both facts are load-bearing: the frame carrying an enter or a removal has, by the time this runs,
    /// been accepted by the send queue, so the client's known set and the server's model of it agree.
    /// </para>
    /// <para>
    /// A duplicate or stale handle is refused by the token pairing rather than applied twice — applying a
    /// removal twice would be harmless, but applying an enter twice against a slot that has since been
    /// reused would tell the server the client knows an entity it has never seen.
    /// </para>
    /// </remarks>
    public void Commit(in PendingKnownSetCommit commit)
    {
        if (!TryResolvePending(in commit, out SubscriberState? sub))
        {
            return;
        }

        // (1) Removals, first — mirroring the wire order, where removals precede any reuse of a slot.
        for (int i = 0; i < sub.PendingRemovalCount; i++)
        {
            int slot = sub.PendingRemovals[i];
            sub.Known.Unset(slot);
            sub.KnownGeneration[slot] = 0;
            sub.PendingUpdates.Unset(slot);   // nothing is owed for an entity the client no longer knows
        }

        // (2) Owed updates: clear the debt for exactly the records whose bytes shipped. Tick already owed
        //     this client every dirty entity it knows, so "not cleared here" is precisely "still owed" —
        //     which is what turns a truncated update section into a debt instead of a permanently stale
        //     entity. Nothing is cleared for a client that never got a frame.
        for (int i = 0; i < sub.PendingUpdateSlotCount; i++)
        {
            sub.PendingUpdates.Unset(sub.PendingUpdateSlots[i]);
        }

        // (3) Enters. A FullRecord is absolute state, so it also settles any owed update for that slot.
        for (int i = 0; i < sub.PendingEnterCount; i++)
        {
            int slot = sub.PendingEnters[i];
            sub.Known.Set(slot);
            sub.KnownGeneration[slot] = sub.PendingEnterGenerations[i];
            sub.PendingUpdates.Unset(slot);
        }

        // (4) Cursors.
        if (sub.PendingHasEnterCursor)
        {
            sub.EnterCursor = sub.PendingEnterCursor;
        }

        if (sub.PendingHasSnapshotCursor)
        {
            sub.SnapshotCursor = sub.PendingSnapshotCursor;
            if (sub.PendingIsFinalSnapshot)
            {
                sub.SnapshotPending = false;   // the client's known set is complete as of this frame
            }
        }

        // (5) Seq advances ONLY here, so a client's sequence has no gaps for frames that never shipped.
        sub.NextSeq = (ushort)(sub.NextSeq + 1);

        sub.DiscardPendingFrame();
        CommitCount++;
    }

    /// <inheritdoc />
    public void Rollback(in PendingKnownSetCommit commit)
    {
        if (!TryResolvePending(in commit, out SubscriberState? sub))
        {
            return;
        }

        ApplyRollback(sub);
        RollbackCount++;
    }

    /// <summary>
    /// Discards a pending frame's intent. Shared by <see cref="Rollback"/>, <see cref="RequestResync"/> and
    /// the abandoned-frame sweep at the top of <see cref="Tick"/>.
    /// </summary>
    /// <remarks>
    /// <b>Everything rolls back by doing nothing</b>, which is the whole point of recording intent instead of
    /// applying it: removals keep their known bit so the exit is re-detected next tick, enters stay un-known
    /// and are retried from an unadvanced carry cursor, the snapshot cursor does not move and
    /// <c>SnapshotPending</c> stays set, the owed-update debts registered by <see cref="Tick"/> are simply
    /// never cleared, and <c>Seq</c> is untouched — so the client never sees a gap for a frame that never
    /// existed. Discarding the record is therefore the entire operation; if this method ever needs to *undo*
    /// something, a writer has mutated state it had no business mutating.
    /// </remarks>
    private static void ApplyRollback(SubscriberState sub) => sub.DiscardPendingFrame();

    /// <summary>
    /// Validates a commit handle against the subscriber's current pending frame. False means "ignore this
    /// call": either it is empty, or the client left, or the handle is a duplicate or stale one.
    /// </summary>
    private bool TryResolvePending(in PendingKnownSetCommit commit, [NotNullWhen(true)] out SubscriberState? sub)
    {
        sub = null;
        if (commit.IsEmpty)
        {
            return false;   // no frame was produced; Commit/Rollback are no-ops
        }

        if (!_subscribers.TryGetValue(commit.ClientId, out SubscriberState? found))
        {
            OrphanCommitCount++;   // the client left between write and enqueue: benign
            return false;
        }

        if (!found.HasPendingFrame || found.PendingToken != commit.Token)
        {
            // Duplicate or stale. In debug this is a hard stop; in release, ignoring the call is strictly
            // better than corrupting a known set, which no resync can detect and no client can recover from
            // on its own.
            Debug.Assert(false, "duplicate or stale known-set commit handle");
            StaleCommitCount++;
            return false;
        }

        Debug.Assert(found.PendingTickStamp == _tickSeq, "a commit must be applied in the tick that wrote it");
        sub = found;
        return true;
    }

    // ── Frame plumbing ─────────────────────────────────────────────────────────

    /// <summary>
    /// Shared preamble for all three writers: resolves the subscriber, applies the hidden and send-divisor
    /// gates, enforces the one-uncommitted-frame-per-client invariant, and resolves the byte budget.
    /// </summary>
    private bool TryBeginFrame(
        uint clientId,
        Span<byte> destination,
        int minimumFrameBytes,
        [NotNullWhen(true)] out SubscriberState? sub,
        out int limit,
        out PendingKnownSetCommit commit)
    {
        sub = null;
        limit = 0;
        commit = default;

        if (_tickSeq == 0 || !_subscribers.TryGetValue(clientId, out SubscriberState? found))
        {
            return false;
        }

        if (found.Hidden)
        {
            // A backgrounded tab cannot drain a 20 Hz stream, it buffers it. No hot frames at all, and Seq
            // stands still, so nothing looks like a gap when it comes back (it re-snapshots instead).
            HiddenSuppressedFrameCount++;
            return false;
        }

        if (!IsDueThisTick(found))
        {
            DivisorSkippedFrameCount++;
            return false;
        }

        if (found.HasPendingFrame)
        {
            // Composing a second frame would overwrite the first one's recorded intent, and the caller
            // would then commit the wrong thing. Refuse instead.
            Debug.Assert(false, "a frame is already awaiting Commit/Rollback for this client");
            ConcurrentFrameRefusedCount++;
            return false;
        }

        limit = Math.Min(destination.Length, _byteBudget);
        if (limit < minimumFrameBytes)
        {
            return false;
        }

        sub = found;
        return true;
    }

    /// <summary>
    /// Whether a rate-divided client is served on this tick. The client id is mixed into the phase so
    /// divided clients spread across ticks instead of all bunching onto the same one.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsDueThisTick(SubscriberState sub)
        => sub.SendDivisor <= 1 || unchecked(_serverTick + sub.ClientId) % sub.SendDivisor == 0;

    /// <summary>
    /// Marks a written frame as awaiting commit and mints its pairing token. Called only once the frame is
    /// known to be non-empty, so an empty write leaves no pending state behind.
    /// </summary>
    private PendingKnownSetCommit OpenFrame(SubscriberState sub, bool isFinalSnapshotFrame)
    {
        uint token = _nextPendingToken++;
        if (_nextPendingToken == 0u)
        {
            _nextPendingToken = 1u;   // 0 is reserved: it is what makes a default handle "empty"
        }

        sub.PendingToken = token;
        sub.HasPendingFrame = true;
        sub.PendingTickStamp = _tickSeq;

        // Seq is the PEEK value. Commit advances the counter; Rollback leaves it, so the client never
        // learns a rolled-back frame existed — which is right, because its known-set changes went with it.
        return new PendingKnownSetCommit(sub.ClientId, sub.NextSeq, isFinalSnapshotFrame, token);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordFrameBytes(int bytes)
    {
        LastTickBytesWritten += bytes;
        LastTickFramesWritten++;
        TotalBytesWritten += bytes;
        TotalFramesWritten++;
    }
}
