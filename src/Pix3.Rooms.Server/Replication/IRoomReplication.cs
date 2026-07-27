using Pix3.Rooms.Protocol;

namespace Pix3.Rooms.Server.Replication;

/// <summary>
/// Owns entity state, AOI, per-client <c>Seq</c> and all hot-path encoding for ONE room.
/// Single-threaded by contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>Single-threaded.</b> Only the owning room's tick thread may touch an instance, so implementations
/// hold no locks. The type depends on <c>Pix3.Rooms.Protocol</c> and nothing else — it must stay
/// unit-testable with no sockets.
/// </para>
/// <para>
/// <b>Encode once, memcpy many.</b> <see cref="Tick"/> writes each dirty entity's record bytes once into
/// scratch storage; the <c>Write*</c> methods then assemble per-client frames by copying those ranges.
/// Nothing is re-serialized per recipient, and no <c>Write*</c> method allocates.
/// </para>
/// <para>
/// <b>Quantized integers are the replicated values</b>, including for dirty detection. There are no
/// float transforms on this seam: <see cref="EntityWireState"/> carries <c>QX/QY/QRot/QVx/QVy</c>, and
/// <see cref="WorldQuantizer"/> converts only at the edges. The one float pair left is
/// <see cref="SetSpectatorFocus"/>, and it is validated and speed-clamped.
/// </para>
/// <para>
/// <b>The tick contract.</b> Per tick, in order: drain inbound (spawn / despawn / update / focus binding),
/// <see cref="Tick"/>, then per client either <see cref="WriteSnapshot"/> or <see cref="WriteDelta"/> plus
/// <see cref="WriteSignalBatch"/> — each followed immediately by <see cref="Commit"/> (the frame was
/// enqueued) or <see cref="Rollback"/> (it was not). A commit must be applied in the same tick as its
/// write: the recorded intent references that tick's scratch state.
/// </para>
/// </remarks>
public interface IRoomReplication
{
    /// <summary>Live entities in the table.</summary>
    int EntityCount { get; }

    /// <summary>
    /// Creates an entity owned by <paramref name="ownerId"/> (0 = server-owned). False when the table is
    /// full (<see cref="RejectCode.EntityLimitReached"/>).
    /// </summary>
    /// <param name="state">
    /// Quantized initial state. <c>Kind</c> and <c>OwnerId</c> inside it are ignored — the explicit
    /// arguments win.
    /// </param>
    /// <param name="netId">The assigned id, or <see cref="NetId.None"/> on failure.</param>
    bool TrySpawn(uint ownerId, ushort kind, in EntityWireState state, out uint netId, out RejectCode reject);

    /// <summary>
    /// Removes an entity. False when it is unknown or stale, or when <paramref name="requesterId"/> is
    /// not its owner (<see cref="RejectCode.NotEntityOwner"/>). Requester 0 is the server and may
    /// despawn anything.
    /// </summary>
    bool TryDespawn(uint netId, uint requesterId, out RejectCode reject);

    /// <summary>
    /// Applies one client update record; false when not owned / unknown / stale generation / illegal mask
    /// / out-of-range quantized field. Dirty detection compares quantized integers, so a client that
    /// re-sends an identical position produces no dirty bit at all.
    /// </summary>
    bool TryApplyOwnedUpdate(uint netId, uint ownerId, byte mask, in EntityWireState state);

    /// <summary>
    /// Despawns the leaving owner's <see cref="OwnershipPolicy.Owned"/> entities; appends removed ids to
    /// <paramref name="despawned"/>. <see cref="OwnershipPolicy.Shared"/>/<see cref="OwnershipPolicy.Transferable"/>
    /// entities are left alone for <see cref="ReassignOwner"/>.
    /// </summary>
    void RemoveOwner(uint ownerId, List<uint> despawned);

    /// <summary>
    /// Host migration: moves <see cref="OwnershipPolicy.Shared"/>/<see cref="OwnershipPolicy.Transferable"/>
    /// entities from one owner to another (usually the promoted host); appends the moved ids to
    /// <paramref name="reassigned"/>.
    /// </summary>
    void ReassignOwner(uint fromOwnerId, uint toOwnerId, List<uint> reassigned);

    /// <summary>Starts tracking a client's known set. It receives a full snapshot before its first delta.</summary>
    void AddSubscriber(uint clientId);

    /// <summary>Drops a client's known set and AOI bookkeeping.</summary>
    void RemoveSubscriber(uint clientId);

    /// <summary>
    /// Binds this client's AOI centre to an owned entity's SERVER-SIDE position, refreshed every tick.
    /// This is the normal path: it deletes focus-teleport amplification at its source.
    /// </summary>
    void BindSubscriberFocus(uint clientId, uint netId);

    /// <summary>
    /// Free-coordinate focus for spectators only. Movement is speed-clamped; a clamp increments the
    /// client's <c>focusClamp</c> counter. Non-finite input is refused and counted as <c>nan</c>.
    /// </summary>
    void SetSpectatorFocus(uint clientId, float x, float y);

    /// <summary>
    /// Clears this client's known set and restarts its snapshot cursor: the next tick emits a full
    /// snapshot. Covers queue overflow, tab refocus and reconnect with one primitive. Safe to call while
    /// a frame is uncommitted — that frame's intent is rolled away first.
    /// </summary>
    void RequestResync(uint clientId);

    /// <summary>
    /// Hidden clients get no hot frames at all and their <c>Seq</c> stops advancing; un-hiding implies a
    /// resync.
    /// </summary>
    void SetSubscriberHidden(uint clientId, bool hidden);

    /// <summary>1 = every tick, n = every nth tick. Clamped to [1, 8]. Control frames are unaffected.</summary>
    void SetSubscriberSendDivisor(uint clientId, byte divisor);

    /// <summary>
    /// Queues one AOI-scoped signal for this tick, encoded once here and copied per recipient by
    /// <see cref="WriteSignalBatch"/>. False = the signal is too large for the hot plane, the tick's batch
    /// is full, or the sender has no bound focus entity to scope it against.
    /// </summary>
    bool TryQueueAoiSignal(uint senderClientId, ReadOnlySpan<byte> name, ReadOnlySpan<byte> payload);

    /// <summary>
    /// Rebuild grid, recompute visibility (k-nearest + hysteresis), fill encode-once scratch, refresh
    /// bound focuses. Call once per tick, before any <c>Write*</c>.
    /// </summary>
    void Tick(uint serverTick);

    /// <summary>
    /// Writes one complete <c>SnapshotPacket</c> (TypeId included). Returns bytes written, 0 if none. The
    /// continuation cursor is per-subscriber state, so a resync restarts it; the frame carrying the last
    /// records reports <see cref="PendingKnownSetCommit.IsFinalSnapshotFrame"/>.
    /// </summary>
    int WriteSnapshot(uint clientId, Span<byte> destination, out PendingKnownSetCommit commit);

    /// <summary>
    /// Writes one complete <c>DeltaPacket</c> (TypeId included), bounded by the destination length and the
    /// per-tick byte budget. Returns 0 when this client has nothing to receive.
    /// </summary>
    int WriteDelta(uint clientId, Span<byte> destination, out PendingKnownSetCommit commit);

    /// <summary>Writes this client's <c>SignalBatchPacket</c> for the current tick. 0 when it has no signals.</summary>
    int WriteSignalBatch(uint clientId, Span<byte> destination, out PendingKnownSetCommit commit);

    /// <summary>The frame was accepted by the send queue: apply the known-set changes and advance <c>Seq</c>.</summary>
    void Commit(in PendingKnownSetCommit commit);

    /// <summary>
    /// The frame was NOT sent: discard the intended changes and leave <c>Seq</c> untouched, so the client
    /// never sees a gap for a frame that never existed.
    /// </summary>
    void Rollback(in PendingKnownSetCommit commit);

    /// <summary>This client's violation tallies. Rooms merges its own numbers into the result.</summary>
    ViolationCounters SnapshotViolations(uint clientId);

    /// <summary>
    /// True while this client still owes snapshot frames — it joined or resynced and its known set is
    /// being rebuilt. The snapshot cursor is core state, so the room asks rather than tracking its own.
    /// </summary>
    bool IsSnapshotPending(uint clientId);

    /// <summary>
    /// Marks an entity's cold props as changed, so the next update for it carries
    /// <see cref="DeltaMask.ColdDirty"/> and the client knows to expect an
    /// <c>EntityPropsChangedEvent</c>. False when the id is unknown or stale.
    /// </summary>
    bool TryMarkColdDirty(uint netId);

    /// <summary>
    /// Records that this client tried to spawn an entity kind the room does not allow. The allowlist
    /// itself lives in <c>RoomConfig</c> — Replication owns the tally, not the policy.
    /// </summary>
    void CountKindViolation(uint clientId);
}
