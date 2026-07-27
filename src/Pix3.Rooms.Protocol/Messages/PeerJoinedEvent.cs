using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// S→C, TypeId <see cref="MessageTypeIds.PeerJoinedEvent"/>. Fanned out to the existing members
/// after a successful join. Membership is room-wide and is not AOI-filtered.
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class PeerJoinedEvent
{
    /// <summary>The new member's client id.</summary>
    [MemoryPackOrder(0)]
    public uint ClientId { get; set; }

    /// <summary>The name the server accepted for that member.</summary>
    [MemoryPackOrder(1)]
    public string DisplayName { get; set; } = "";

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public PeerJoinedEvent()
    {
    }
}
