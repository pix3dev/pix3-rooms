namespace Pix3.Rooms.Protocol;

/// <summary>
/// Why a member is no longer in the room. Travels as <see cref="PeerLeftEvent.Reason"/>.
/// </summary>
/// <remarks>
/// A drop inside the resume grace emits <b>no</b> <see cref="PeerLeftEvent"/> at all — peers are not
/// told about a blip. <see cref="Timeout"/> is what peers see when the grace expires.
/// </remarks>
public enum LeaveReason : byte
{
    /// <summary>
    /// Socket dropped without a protocol-level goodbye, and no resume grace applied — the room was
    /// configured with <c>ResumeGraceSeconds = 0</c>, or is closing. A drop <i>into</i> a grace emits no
    /// leave at all while the grace runs, and <see cref="Timeout"/> when it expires.
    /// </summary>
    Disconnected = 0,

    /// <summary>Client sent <see cref="LeaveCommand"/>.</summary>
    LeftVoluntarily = 1,

    /// <summary>Removed by an operator or by an admin API call.</summary>
    Kicked = 2,

    /// <summary>The 30-second resume grace expired, or an idle/heartbeat timeout fired.</summary>
    Timeout = 3,

    /// <summary>The room itself was destroyed.</summary>
    RoomClosed = 4,

    /// <summary>Removed because of a server-side or protocol error.</summary>
    Error = 5,
}
