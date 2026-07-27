using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// C→S, TypeId <see cref="MessageTypeIds.EmitSignalCommand"/>. A <b>signal</b> is a networked game
/// event; <see cref="Target"/> selects the routing. The server never interprets <see cref="Name"/> or
/// <see cref="Payload"/>.
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class EmitSignalCommand
{
    /// <summary>
    /// Application-defined signal name. Length-capped by the server; on the
    /// <see cref="SignalTarget.AoiPeers"/> path it must fit the 1-64 UTF-8 bytes a
    /// <c>SignalBatchPacket</c> entry can express.
    /// </summary>
    [MemoryPackOrder(0)]
    public string Name { get; set; } = "";

    /// <summary>A <see cref="SignalTarget"/> value selecting the routing.</summary>
    [MemoryPackOrder(1)]
    public byte Target { get; set; }

    /// <summary>Recipient when <see cref="Target"/> is <see cref="SignalTarget.SinglePeer"/>; ignored otherwise.</summary>
    [MemoryPackOrder(2)]
    public uint TargetClientId { get; set; }

    /// <summary>
    /// Opaque payload. Size-capped by the server; a payload above 255 B is not eligible for the
    /// <see cref="SignalTarget.AoiPeers"/> hot path at all and is refused with the <c>quota</c> counter.
    /// </summary>
    [MemoryPackOrder(3)]
    public byte[] Payload { get; set; } = [];

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public EmitSignalCommand()
    {
    }
}
