using System.Diagnostics;
using Pix3.Rooms.Protocol;
using Pix3.Rooms.Server.Net;
using Pix3.Rooms.Server.Replication;

namespace Pix3.Rooms.Server.Rooms;

/// <summary>
/// One member of one room: its connection plus the room-side bookkeeping the tick loop needs.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a mutable class with fields rather than a record: the tick loop touches these every tick,
/// so no copying and no property accessors.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="ClientId"/> and <see cref="JoinSequence"/> are immutable and readable from
/// anywhere. <see cref="Connection"/> is a volatile publish, because a successful resume swaps a dead
/// socket for a live one on a socket thread. <see cref="AwaitingResume"/> and
/// <see cref="Violations"/> are volatile publishes for the same reason. Every other field belongs to the
/// room's tick thread and nothing outside <c>Room</c>'s tick path may touch it.
/// </para>
/// <para>
/// <b>There is no snapshot cursor here.</b> Snapshot continuation is replication-owned state — a resync
/// has to be able to restart it — so the room asks
/// <c>RoomReplication.IsSnapshotPending</c> instead of remembering a second copy of the answer.
/// </para>
/// </remarks>
internal sealed class RoomMember
{
    /// <summary>Cached <see cref="IClientConnection.ClientId"/> — also the entity owner id.</summary>
    internal readonly uint ClientId;

    /// <summary>
    /// Monotonic admission order inside this room; decides host succession. Preserved across a resume, so
    /// a blip does not cost a member its place in the succession queue.
    /// </summary>
    internal readonly long JoinSequence;

    /// <summary>Entities this member spawned, in spawn order. Sized once, never grown.</summary>
    private readonly uint[] _ownedEntities;

    private IClientConnection _connection;
    private int _awaitingResume;
    private ViolationsSnapshot? _violations;

    /// <summary>True once <c>RoomReplication.AddSubscriber</c> has run for this member.</summary>
    internal bool SubscriberAdded;

    /// <summary>True once peers have been told about this member, so a matching leave can be announced.</summary>
    internal bool JoinAnnounced;

    /// <summary>
    /// This session's current resume credential, regenerated on every connect. A key that leaked from an
    /// earlier session is therefore worthless.
    /// </summary>
    internal ResumeKey ResumeKey;

    /// <summary>The key the pending-session table filed this member under when its socket dropped.</summary>
    internal ResumeKey GraceKey;

    /// <summary><see cref="Stopwatch"/> timestamp the resume grace started at.</summary>
    internal long GraceStartTimestamp;

    /// <summary>
    /// Incremented every time a grace begins. A grace-list entry only expires the epoch it was filed for,
    /// so a member that dropped, resumed and dropped again cannot have its new pending session expired by
    /// its old entry.
    /// </summary>
    internal long GraceEpoch;

    /// <summary>The entity whose server-side position drives this member's AOI, or <see cref="NetId.None"/>.</summary>
    /// <remarks>
    /// The room's record of what it last <i>bound</i>, so it can tell a spawn/despawn transition from a
    /// no-op. The authoritative focus position lives in replication and is refreshed there every tick.
    /// </remarks>
    internal uint FocusNetId;

    /// <summary>Start of the current chat rate-limit window, in <c>Stopwatch</c> timestamp ticks.</summary>
    internal long ChatWindowStart;

    /// <summary>Chat messages accepted inside the current window.</summary>
    internal int ChatCountInWindow;

    /// <summary>Entities this member currently owns <i>and spawned</i>, for the per-owner spawn quota.</summary>
    internal int OwnedEntityCount;

    /// <summary>
    /// Room-level refusals attributed to this member: chat throttling, room-var rejections, cold-props
    /// rejections, per-owner spawn-cap rejections and refused signals. Merged into
    /// <see cref="ViolationCounters.Quota"/>, which Replication always leaves at zero.
    /// </summary>
    internal long QuotaViolations;

    internal RoomMember(IClientConnection connection, long joinSequence, int maxOwnedEntities)
    {
        _connection = connection;
        ClientId = connection.ClientId;
        JoinSequence = joinSequence;
        FocusNetId = NetId.None;
        _ownedEntities = new uint[Math.Max(1, maxOwnedEntities)];
    }

    /// <summary>The socket. Room logic only ever sees this interface.</summary>
    internal IClientConnection Connection => Volatile.Read(ref _connection);

    /// <summary>True while this session sits in the resume grace waiting for a reconnect.</summary>
    internal bool AwaitingResume => Volatile.Read(ref _awaitingResume) != 0;

    /// <summary>
    /// The last violation tallies the tick thread published for this member, or null if none yet. Read
    /// from admin threads; a reference publish, so a reader never sees half a record.
    /// </summary>
    internal ViolationsSnapshot? Violations => Volatile.Read(ref _violations);

    /// <summary>
    /// Files this member into the resume grace: nothing else changes, because its entities are meant to
    /// stay exactly where they are.
    /// </summary>
    /// <param name="timestamp">A <see cref="Stopwatch.GetTimestamp"/> reading for "now".</param>
    /// <returns>The grace epoch this call opened.</returns>
    internal long BeginGrace(long timestamp)
    {
        GraceKey = ResumeKey;
        GraceStartTimestamp = timestamp;
        long epoch = ++GraceEpoch;
        Volatile.Write(ref _awaitingResume, 1);
        return epoch;
    }

    /// <summary>
    /// Adopts a new socket for the same session and mints the fresh resume key that
    /// <c>WelcomeEvent</c> will carry. Called on a socket thread, before the member is re-published.
    /// </summary>
    internal void Reattach(IClientConnection connection)
    {
        ResumeKey = ResumeKey.Create();
        Volatile.Write(ref _connection, connection);
        Volatile.Write(ref _awaitingResume, 0);
    }

    /// <summary>Publishes merged violation tallies for cross-thread readers.</summary>
    internal void PublishViolations(ViolationsSnapshot snapshot) => Volatile.Write(ref _violations, snapshot);

    /// <summary>The entity this member's focus should follow: its first live spawned entity, or none.</summary>
    internal uint FirstOwnedEntity => OwnedEntityCount > 0 ? _ownedEntities[0] : NetId.None;

    /// <summary>
    /// Records a freshly spawned entity in spawn order. False when the per-owner array is full, which the
    /// caller prevents by checking the quota first.
    /// </summary>
    internal bool TryAddOwnedEntity(uint netId)
    {
        if (OwnedEntityCount >= _ownedEntities.Length)
        {
            return false;
        }

        _ownedEntities[OwnedEntityCount++] = netId;
        return true;
    }

    /// <summary>
    /// Forgets a despawned entity, preserving spawn order for the entities after it — focus re-binding
    /// depends on that order.
    /// </summary>
    /// <returns>True when the entity was one this member spawned.</returns>
    internal bool RemoveOwnedEntity(uint netId)
    {
        for (int i = 0; i < OwnedEntityCount; i++)
        {
            if (_ownedEntities[i] != netId)
            {
                continue;
            }

            int remaining = OwnedEntityCount - i - 1;
            if (remaining > 0)
            {
                Array.Copy(_ownedEntities, i + 1, _ownedEntities, i, remaining);
            }

            OwnedEntityCount--;
            _ownedEntities[OwnedEntityCount] = NetId.None;
            return true;
        }

        return false;
    }

    /// <summary>Drops every owned-entity record, e.g. once the session has really left.</summary>
    internal void ClearOwnedEntities()
    {
        Array.Clear(_ownedEntities, 0, OwnedEntityCount);
        OwnedEntityCount = 0;
    }
}

/// <summary>
/// A published, torn-free copy of one member's violation tallies. A class so the tick thread can hand a
/// whole record to another thread with a single reference write.
/// </summary>
/// <remarks>
/// Republished only when a number actually changed, so a room with no misbehaving clients allocates
/// nothing here for its whole lifetime.
/// </remarks>
internal sealed class ViolationsSnapshot
{
    internal ViolationsSnapshot(in ViolationCounters counters)
    {
        Counters = counters;
    }

    /// <summary>The merged replication + room tallies as of the publish.</summary>
    internal ViolationCounters Counters { get; }

    /// <summary>Sum of every field, for <see cref="RoomStats.Violations"/>.</summary>
    internal long Total => Counters.Ownership + Counters.Speed + Counters.Mask + Counters.Nan
                         + Counters.Kind + Counters.Quota + Counters.FocusClamp + Counters.Teleport;
}

/// <summary>
/// A membership addition waiting for the tick thread: <c>IRoomReplication</c> is single-threaded, so the
/// replication half of a join or a resume is queued rather than run on the socket thread.
/// </summary>
internal readonly struct PendingAdmission
{
    internal readonly RoomMember Member;

    /// <summary>
    /// True when this admission re-attached an existing session. A resume is <b>not</b> announced to
    /// peers — they were never told it left — and it does not re-run host assignment.
    /// </summary>
    internal readonly bool IsResume;

    internal PendingAdmission(RoomMember member, bool isResume)
    {
        Member = member;
        IsResume = isResume;
    }
}

/// <summary>A membership removal waiting for the tick thread.</summary>
internal readonly struct PendingLeave
{
    internal readonly RoomMember Member;
    internal readonly LeaveReason Reason;

    /// <summary>
    /// True when the socket dropped and the session is resumable: replication stops serving it, but its
    /// entities stay alive and frozen, no <c>PeerLeftEvent</c> is emitted, and its slot stays reserved.
    /// </summary>
    internal readonly bool WithGrace;

    /// <summary>
    /// The pending-session key, start timestamp and epoch this leave filed, captured at enqueue time.
    /// Snapshotted rather than re-read on the tick thread, so a session that dropped, resumed and dropped
    /// again cannot have its sweep entry describe the wrong grace.
    /// </summary>
    internal readonly ResumeKey GraceKey;
    internal readonly long GraceStartTimestamp;
    internal readonly long GraceEpoch;

    internal PendingLeave(RoomMember member, LeaveReason reason, bool withGrace)
        : this(member, reason, withGrace, default, 0L, 0L)
    {
    }

    internal PendingLeave(
        RoomMember member,
        LeaveReason reason,
        bool withGrace,
        ResumeKey graceKey,
        long graceStartTimestamp,
        long graceEpoch)
    {
        Member = member;
        Reason = reason;
        WithGrace = withGrace;
        GraceKey = graceKey;
        GraceStartTimestamp = graceStartTimestamp;
        GraceEpoch = graceEpoch;
    }
}

/// <summary>One session inside its resume grace, as the tick thread's expiry sweep sees it.</summary>
/// <remarks>
/// The epoch is what makes the sweep safe against a member that dropped, resumed and dropped again inside
/// one grace period: an entry only ever expires the epoch it was filed for.
/// </remarks>
internal readonly struct GraceEntry
{
    internal readonly RoomMember Member;
    internal readonly ResumeKey Key;
    internal readonly long StartTimestamp;
    internal readonly long Epoch;

    internal GraceEntry(RoomMember member, ResumeKey key, long startTimestamp, long epoch)
    {
        Member = member;
        Key = key;
        StartTimestamp = startTimestamp;
        Epoch = epoch;
    }
}

/// <summary>
/// Room-side record of one live entity: who owns it, its last known cold props and its cold-props rate
/// window.
/// </summary>
/// <remarks>
/// The replication table is the authority on existence; this mirror only ever changes on a confirmed
/// spawn/despawn/reassign result. It exists so the room can answer "who owns this?" and "how often have
/// its props changed?" without reaching into replication, and so cold props are released when the entity
/// dies.
/// </remarks>
internal struct EntityInfo
{
    internal uint OwnerId;
    internal byte[]? ColdProps;

    /// <summary>Start of the current cold-props rate window, in <c>Stopwatch</c> timestamp ticks.</summary>
    internal long ColdPropsWindowStart;

    /// <summary>Cold-props writes accepted inside the current window.</summary>
    internal int ColdPropsCountInWindow;

    internal EntityInfo(uint ownerId, byte[]? coldProps)
    {
        OwnerId = ownerId;
        ColdProps = coldProps;
        ColdPropsWindowStart = 0L;
        ColdPropsCountInWindow = 0;
    }
}
