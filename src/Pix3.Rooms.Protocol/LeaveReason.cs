namespace Pix3.Rooms.Protocol;

/// <summary>Why a member is no longer in the room. Travels as <see cref="PeerLeftEvent.Reason"/>.</summary>
public enum LeaveReason : byte
{
    /// <summary>Socket dropped without a protocol-level goodbye.</summary>
    Disconnected = 0,

    /// <summary>Client sent <see cref="LeaveRequest"/>.</summary>
    LeftVoluntarily = 1,

    /// <summary>Removed by an operator or by an admin API call.</summary>
    Kicked = 2,

    /// <summary>Idle timeout or heartbeat loss.</summary>
    Timeout = 3,

    /// <summary>The room itself was destroyed.</summary>
    RoomClosed = 4,

    /// <summary>Removed because of a server-side or protocol error.</summary>
    Error = 5,
}
