using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// S→C, TypeId <see cref="MessageTypeIds.PeerLeftEvent"/>. A member is gone; its entities are
/// resolved by their <see cref="OwnershipPolicy"/>. A drop inside the resume grace emits nothing at
/// all, so peers are never told about a blip.
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class PeerLeftEvent
{
    /// <summary>The departed member's client id.</summary>
    [MemoryPackOrder(0)]
    public uint ClientId { get; set; }

    /// <summary>A <see cref="LeaveReason"/> value.</summary>
    [MemoryPackOrder(1)]
    public byte Reason { get; set; }

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public PeerLeftEvent()
    {
    }
}
