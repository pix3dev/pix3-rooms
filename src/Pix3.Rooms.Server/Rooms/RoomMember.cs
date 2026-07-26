using Pix3.Rooms.Protocol;
using Pix3.Rooms.Server.Net;

namespace Pix3.Rooms.Server.Rooms;

/// <summary>
/// One member of one room: its connection plus the room-side bookkeeping the tick loop needs.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a mutable class with fields rather than a record: the tick loop touches these every
/// tick and takes a <c>ref</c> to <see cref="SnapshotCursor"/> when handing it to
/// <c>IRoomReplication.WriteSnapshot</c>, so no copying and no property accessors.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="Connection"/>, <see cref="ClientId"/> and <see cref="JoinSequence"/>
/// are immutable and readable from anywhere. Every other field belongs to the room's tick thread;
/// nothing outside <c>Room</c>'s tick path may touch them.
/// </para>
/// </remarks>
internal sealed class RoomMember
{
    /// <summary>The socket. Room logic only ever sees this interface.</summary>
    internal readonly IClientConnection Connection;

    /// <summary>Cached <see cref="IClientConnection.ClientId"/> — also the entity owner id.</summary>
    internal readonly uint ClientId;

    /// <summary>Monotonic admission order inside this room; decides host succession.</summary>
    internal readonly long JoinSequence;

    /// <summary>True once <c>IRoomReplication.AddSubscriber</c> has run for this member.</summary>
    internal bool SubscriberAdded;

    /// <summary>True once peers have been told about this member, so a matching leave can be announced.</summary>
    internal bool JoinAnnounced;

    /// <summary>True while the joiner still owes snapshot frames.</summary>
    internal bool SnapshotPending;

    /// <summary>Continuation cursor for a snapshot split across frames; 0 means "start / finished".</summary>
    internal int SnapshotCursor;

    /// <summary>
    /// The entity whose position drives this member's area of interest: the first entity it spawned
    /// (by convention its avatar). <see cref="NetId.None"/> when it owns none.
    /// </summary>
    internal uint FocusNetId;

    /// <summary>Latest known focus X.</summary>
    internal float FocusX;

    /// <summary>Latest known focus Y.</summary>
    internal float FocusY;

    /// <summary>
    /// Set when the focus moved this tick. The room publishes focus at most once per tick per client,
    /// as <c>IRoomReplication.SetSubscriberFocus</c> requires, however many update frames arrived.
    /// </summary>
    internal bool FocusDirty;

    /// <summary>Start of the current chat rate-limit window, in <c>Stopwatch</c> timestamp ticks.</summary>
    internal long ChatWindowStart;

    /// <summary>Chat messages accepted inside the current window.</summary>
    internal int ChatCountInWindow;

    /// <summary>Entities this member currently owns, for the per-owner spawn quota.</summary>
    internal int OwnedEntityCount;

    internal RoomMember(IClientConnection connection, long joinSequence)
    {
        Connection = connection;
        ClientId = connection.ClientId;
        JoinSequence = joinSequence;
        FocusNetId = NetId.None;
    }
}

/// <summary>
/// A membership removal waiting for the tick thread. <c>Leave</c> can be called from a socket thread,
/// but <c>IRoomReplication</c> is single-threaded, so the replication half of a leave is queued.
/// </summary>
internal readonly struct PendingLeave
{
    internal readonly RoomMember Member;
    internal readonly LeaveReason Reason;

    internal PendingLeave(RoomMember member, LeaveReason reason)
    {
        Member = member;
        Reason = reason;
    }
}

/// <summary>
/// Room-side record of one live entity: who owns it and its last known cold props.
/// </summary>
/// <remarks>
/// The replication table is the authority on existence; this mirror only ever changes on a confirmed
/// spawn/despawn result, and exists so the room can answer "who owns this?" for cold-props writes
/// without reaching into replication, and so cold props are released when the entity dies.
/// </remarks>
internal struct EntityInfo
{
    internal uint OwnerId;
    internal byte[]? ColdProps;

    internal EntityInfo(uint ownerId, byte[]? coldProps)
    {
        OwnerId = ownerId;
        ColdProps = coldProps;
    }
}
