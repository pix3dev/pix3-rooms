using Pix3.Rooms.Protocol;

namespace Pix3.Rooms.Server.Replication;

/// <summary>
/// Owns entity state, area-of-interest filtering and all hot-path encoding for ONE room.
/// </summary>
/// <remarks>
/// <para>
/// <b>Single-threaded by contract.</b> Only the owning room's tick thread may touch an instance, so
/// implementations hold no locks. The type depends on <c>Pix3.Rooms.Protocol</c> and nothing else — it
/// must stay unit-testable with no sockets.
/// </para>
/// <para>
/// <b>Encode once, memcpy many.</b> <see cref="Tick"/> writes each dirty entity's record bytes once
/// into scratch storage; <see cref="WriteDelta"/> then assembles a per-client frame by copying those
/// ranges. Nothing is re-serialized per recipient, and neither method allocates.
/// </para>
/// </remarks>
public interface IRoomReplication
{
    /// <summary>Live entities in the table.</summary>
    int EntityCount { get; }

    /// <summary>
    /// Creates an entity owned by <paramref name="ownerId"/> (0 = server-owned). False when the table
    /// is full (<see cref="RejectCode.EntityLimitReached"/>).
    /// </summary>
    /// <param name="netId">The assigned id, or <see cref="NetId.None"/> on failure.</param>
    bool TrySpawn(uint ownerId, ushort kind, in EntityWireState state, out uint netId, out RejectCode reject);

    /// <summary>
    /// Removes an entity. False when it is unknown or when <paramref name="requesterId"/> is not its
    /// owner (<see cref="RejectCode.NotEntityOwner"/>).
    /// </summary>
    bool TryDespawn(uint netId, uint requesterId, out RejectCode reject);

    /// <summary>
    /// Applies one client delta record; false when the entity is not owned by
    /// <paramref name="ownerId"/>, is unknown, or the mask is illegal
    /// (see <see cref="HotWire.IsClientMaskLegal"/>). Merge with <see cref="EntityWireState.Apply"/> —
    /// unmasked fields must survive untouched.
    /// </summary>
    bool TryApplyOwnedUpdate(uint netId, uint ownerId, byte mask, in EntityWireState state);

    /// <summary>
    /// Despawns everything owned by a leaving client and appends the removed ids to
    /// <paramref name="despawned"/> (the caller supplies a reused list; it is not cleared here).
    /// </summary>
    void RemoveOwner(uint ownerId, List<uint> despawned);

    /// <summary>Starts tracking a client's known-set. It receives a snapshot before its first delta.</summary>
    void AddSubscriber(uint clientId);

    /// <summary>Drops a client's known-set and AOI bookkeeping.</summary>
    void RemoveSubscriber(uint clientId);

    /// <summary>
    /// Area-of-interest centre for this client (normally its own avatar's position). Called at most
    /// once per tick per client.
    /// </summary>
    void SetSubscriberFocus(uint clientId, float x, float y);

    /// <summary>
    /// Rebuilds the spatial grid, recomputes visibility and fills the encode-once scratch buffers.
    /// Call exactly once per tick, before any <see cref="WriteDelta"/>/<see cref="WriteSnapshot"/>.
    /// </summary>
    void Tick(uint serverTick);

    /// <summary>
    /// Writes a complete SnapshotFrame (TypeId included) for a joiner. Returns bytes written, 0 if none.
    /// </summary>
    /// <param name="destination">Target buffer; must be at least <c>MaxPayloadBytes</c> to guarantee progress.</param>
    /// <param name="continuationCursor">
    /// Lets a large snapshot be emitted across several self-contained frames: pass 0 to start, then
    /// keep calling while the returned length is non-zero and the cursor has not returned to 0.
    /// The implementation resets it to 0 once the snapshot is complete.
    /// </param>
    int WriteSnapshot(uint clientId, Span<byte> destination, ref int continuationCursor);

    /// <summary>
    /// Writes a complete DeltaFrame (TypeId included) for one client. Returns 0 when this client has
    /// nothing to receive this tick — a conforming server then sends no frame at all.
    /// </summary>
    int WriteDelta(uint clientId, Span<byte> destination);
}
