namespace Pix3.Rooms.Protocol;

/// <summary>
/// Routing selector for <see cref="EmitSignalCommand.Target"/>. A <b>signal</b> is a networked game
/// event — pix3's own term, matching its signals engine.
/// </summary>
public enum SignalTarget : byte
{
    /// <summary>Handled by the room itself (Level 2/3 rules); nothing is fanned out.</summary>
    Server = 0,

    /// <summary>
    /// Delivered to every other member of the room as a <see cref="SignalEvent"/>, one frame per
    /// recipient. A 600× amplifier, so its quota is the tightest of the four (2/s).
    /// </summary>
    AllPeers = 1,

    /// <summary>
    /// Delivered only to <see cref="EmitSignalCommand.TargetClientId"/>, as a
    /// <see cref="SignalEvent"/>.
    /// </summary>
    SinglePeer = 2,

    /// <summary>
    /// Delivered to the peers currently inside the sender's area of interest, batched into one
    /// <c>SignalBatchPacket</c> per recipient per tick and flushed with that recipient's delta. This is
    /// the path a shooter's fire events take, and it must never cost an extra socket send.
    /// </summary>
    AoiPeers = 3,
}
