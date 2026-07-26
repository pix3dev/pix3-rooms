namespace Pix3.Rooms.Protocol;

/// <summary>Fan-out selector for <see cref="RemoteEventRequest.Target"/>.</summary>
public enum RemoteEventTarget : byte
{
    /// <summary>Delivered to the server only; no fan-out.</summary>
    Server = 0,

    /// <summary>Delivered to every other member of the room.</summary>
    AllPeers = 1,

    /// <summary>Delivered only to <see cref="RemoteEventRequest.TargetClientId"/>.</summary>
    SinglePeer = 2,

    /// <summary>Delivered to the peers currently inside the sender's area of interest.</summary>
    AoiPeers = 3,
}
