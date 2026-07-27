namespace Pix3.Rooms.Server.Replication;

/// <summary>
/// What a <c>Write*</c> call <i>intends</i> to change in a client's known set, plus the <c>Seq</c> it
/// stamped into the frame. Opaque to callers: hand it back to
/// <see cref="IRoomReplication.Commit"/> (the frame was enqueued) or
/// <see cref="IRoomReplication.Rollback"/> (the enqueue failed). Nothing else may mutate a known set.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists.</b> The lossy link is our own bounded send queue, not the network. Position
/// updates carry absolute values and self-heal, but enter and removal records do not: flipping a
/// known-set bit while composing a frame the queue can still drop leaves the client with a permanent
/// ghost (removal dropped) or a permanently invisible entity (enter dropped). So the writer records
/// intent and the caller decides.
/// </para>
/// <para>
/// <b>The struct carries no payload.</b> The recorded slots live in pre-sized per-subscriber arrays —
/// allocating a variable-length intent record on the tick path would defeat the point. This struct is
/// only the <i>handle</i>: which client, which pending frame (<see cref="Token"/>), what <c>Seq</c> was
/// stamped, and whether the frame closed a split snapshot.
/// </para>
/// <para>
/// <b>One uncommitted frame per client at a time.</b> <see cref="Token"/> pairs the handle with the
/// subscriber's current pending generation, which is bumped every time a frame is opened. A duplicate
/// commit (the same handle applied twice) or a stale one (a handle from an earlier frame) therefore
/// fails the pairing test and is refused: it asserts in debug builds and is ignored in release, because
/// silently corrupting a known set is far worse than dropping a mis-sequenced call.
/// </para>
/// </remarks>
public readonly struct PendingKnownSetCommit
{
    /// <summary>The client whose known set and <c>Seq</c> this handle governs.</summary>
    public readonly uint ClientId;

    /// <summary>
    /// The <c>Seq</c> stamped into the frame header. It is the subscriber's <i>peek</i> value:
    /// <see cref="IRoomReplication.Commit"/> advances the counter,
    /// <see cref="IRoomReplication.Rollback"/> leaves it alone, so a frame that never shipped leaves no
    /// gap for the client to detect.
    /// </summary>
    public readonly ushort Seq;

    /// <summary>
    /// True when this frame is a <c>SnapshotPacket</c> carrying the last records of a (possibly split)
    /// snapshot — the frame stamped with <c>FrameFlags.Final</c>. Committing it clears the subscriber's
    /// snapshot-pending state; rolling it back keeps the snapshot outstanding.
    /// </summary>
    public readonly bool IsFinalSnapshotFrame;

    /// <summary>
    /// Pairing token for the subscriber's current pending frame. Non-zero for a real handle, so
    /// <see cref="IsEmpty"/> is simply "no token". Opaque: callers must not interpret or compare it.
    /// </summary>
    public readonly uint Token;

    /// <summary>Creates a handle for a freshly opened pending frame.</summary>
    internal PendingKnownSetCommit(uint clientId, ushort seq, bool isFinalSnapshotFrame, uint token)
    {
        ClientId = clientId;
        Seq = seq;
        IsFinalSnapshotFrame = isFinalSnapshotFrame;
        Token = token;
    }

    /// <summary>
    /// True when no frame was produced (the writer returned 0 bytes). Both
    /// <see cref="IRoomReplication.Commit"/> and <see cref="IRoomReplication.Rollback"/> are no-ops for
    /// an empty handle, so a caller never has to branch on the byte count.
    /// </summary>
    public bool IsEmpty => Token == 0u;
}
