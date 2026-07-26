using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>S→C, TypeId <see cref="MessageTypeIds.PeerLeftEvent"/>. A member is gone; its entities are despawned.</summary>
[MemoryPackable]
public sealed partial class PeerLeftEvent
{
    /// <summary>The departed member's client id.</summary>
    public uint ClientId { get; set; }

    /// <summary>A <c>LeaveReason</c> value.</summary>
    public byte Reason { get; set; }

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public PeerLeftEvent()
    {
    }
}
