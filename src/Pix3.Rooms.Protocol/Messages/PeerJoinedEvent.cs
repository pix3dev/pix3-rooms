using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// S→C, TypeId <see cref="MessageTypeIds.PeerJoinedEvent"/>. Fanned out to the existing members
/// after a successful join. Membership is room-wide and is not AOI-filtered.
/// </summary>
[MemoryPackable]
public sealed partial class PeerJoinedEvent
{
    /// <summary>The new member's client id.</summary>
    public uint ClientId { get; set; }

    /// <summary>The name the server accepted for that member.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public PeerJoinedEvent()
    {
    }
}
